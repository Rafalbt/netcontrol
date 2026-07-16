using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrzepustnicaService;

// Named-pipe IPC server (PLAN_WDROZENIA_WINDOWS.md §2): newline-delimited
// JSON messages, camelCase. Commands in: hello, getRules, setRule, deleteRule,
// toggle, listProcesses, getHistory. Messages out: rules, usage (1 Hz,
// broadcast by TelemetryLoop), processes, history, error.
public sealed class IpcServer : BackgroundService
{
    public const string PipeName = "przepustnica";

    private sealed class Client
    {
        public required NamedPipeServerStream Pipe { get; init; }

        // Outgoing messages go through a bounded queue drained by a single
        // writer task, so one slow or hung client can never stall the 1 Hz
        // broadcast for everyone else — it just gets dropped when its queue
        // overflows.
        public Channel<string> Outgoing { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256) { SingleReader = true });
    }

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly Database _db;
    private readonly ProcessCatalog _catalog;
    private readonly EtwTrafficCounter _etw;
    private readonly IpcAuthToken _token;
    private readonly ILogger<IpcServer> _logger;

    public IpcServer(Database db, ProcessCatalog catalog, EtwTrafficCounter etw, IpcAuthToken token,
        ILogger<IpcServer> logger)
    {
        _db = db;
        _catalog = catalog;
        _etw = etw;
        _token = token;
        _logger = logger;
    }

    public int ClientCount => _clients.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(@"IPC listening on \\.\pipe\{PipeName}", PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create pipe server instance");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            _ = HandleClientAsync(pipe, stoppingToken);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // The UI runs unelevated while the service is SYSTEM, so the default
        // pipe ACL would reject it — grant Authenticated Users read/write.
        // TODO Etap 4: add the auth token from the plan on top of this.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Creating further instances of an existing pipe requires
        // CreateNewInstance for the creator itself — grant it explicitly, or
        // an unelevated dev run denies its own second instance.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } user)
            {
                security.AddAccessRule(new PipeAccessRule(
                    user, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }

        // Non-zero buffers matter: with 0-byte buffers every write is a
        // rendezvous that blocks until the peer reads, which deadlocks a
        // client that writes its first command before reading our initial
        // rules push.
        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var id = Guid.NewGuid();
        var client = new Client { Pipe = pipe };
        _clients[id] = client;
        _logger.LogInformation("IPC client connected ({Count} total)", _clients.Count);

        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var writerTask = WriteLoopAsync(client, clientCts.Token);

        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

            // Handshake: the very first message must be a hello carrying the
            // shared token from %ProgramData%\Przepustnica\ipc.token.
            var helloLine = await reader.ReadLineAsync(stoppingToken);
            if (helloLine is null || !IsValidHello(helloLine))
            {
                _logger.LogWarning("IPC client rejected — bad or missing auth token");
                Send(client, new { type = "error", message = "auth required: send hello with a valid token" });
                await Task.Delay(100, stoppingToken); // let the writer flush the error
                return;
            }

            // Authenticated: push the full state so the UI can render without
            // explicit round-trips.
            Send(client, new { type = "rules", rules = _db.GetRules() });
            Send(client, new { type = "settings", settings = _db.GetSettings() });

            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stoppingToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Dispatch(client, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Client disconnected mid-read — normal.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC client handler failed");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            clientCts.Cancel();
            try { await writerTask; } catch { }
            try { await pipe.DisposeAsync(); } catch { }
            _logger.LogInformation("IPC client disconnected ({Count} total)", _clients.Count);
        }
    }

    private async Task WriteLoopAsync(Client client, CancellationToken ct)
    {
        var writer = new StreamWriter(client.Pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
        try
        {
            await foreach (var json in client.Outgoing.Reader.ReadAllAsync(ct))
            {
                await writer.WriteLineAsync(json.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Client went away mid-write — the read loop cleans up.
        }
    }

    private void Dispatch(Client client, string line)
    {
        string? type = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "hello":
                case "getRules":
                    Send(client, new { type = "rules", rules = _db.GetRules() });
                    break;

                case "getSettings":
                    Send(client, new { type = "settings", settings = _db.GetSettings() });
                    break;

                case "setSettings":
                {
                    var settings = root.GetProperty("settings").Deserialize<AppSettings>(Json.Options);
                    if (settings is null || settings.LinkCapacityMbps <= 0)
                        throw new InvalidOperationException("setSettings requires settings with linkCapacityMbps > 0");
                    _db.SaveSettings(settings);
                    Broadcast(new { type = "settings", settings = _db.GetSettings() });
                    break;
                }

                case "setRule":
                {
                    var rule = root.GetProperty("rule").Deserialize<AppRule>(Json.Options);
                    if (rule is null || string.IsNullOrWhiteSpace(rule.Id))
                        throw new InvalidOperationException("setRule requires rule.id");
                    _db.UpsertRule(rule);
                    BroadcastRules();
                    break;
                }

                case "deleteRule":
                    _db.DeleteRule(GetId(root));
                    BroadcastRules();
                    break;

                case "toggle":
                    _db.ToggleRule(GetId(root));
                    BroadcastRules();
                    break;

                case "listProcesses":
                    Send(client, new
                    {
                        type = "processes",
                        processes = _catalog.ListProcesses(_etw.ActivePids.ToHashSet()),
                    });
                    break;

                case "getHistory":
                {
                    var period = root.TryGetProperty("period", out var p) ? p.GetString() ?? "week" : "week";
                    var history = _db.GetHistory(period);
                    Send(client, new
                    {
                        type = "history",
                        period = history.Period,
                        labels = history.Labels,
                        seriesGb = history.SeriesGb,
                        savedGb = history.SavedGb,
                        throttleEvents = history.ThrottleEvents,
                    });
                    break;
                }

                default:
                    Send(client, new { type = "error", message = $"Unknown command: {type}" });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bad IPC command ({Type})", type);
            Send(client, new { type = "error", command = type, message = ex.Message });
        }
    }

    private bool IsValidHello(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.GetProperty("type").GetString() == "hello"
                && root.TryGetProperty("token", out var token)
                && token.GetString() == _token.Value;
        }
        catch
        {
            return false;
        }
    }

    private static string GetId(JsonElement root) =>
        root.GetProperty("id").GetString() ?? throw new InvalidOperationException("id required");

    private void BroadcastRules() => Broadcast(new { type = "rules", rules = _db.GetRules() });

    public void Broadcast(object message)
    {
        if (_clients.IsEmpty) return;
        var json = JsonSerializer.Serialize(message, Json.Options);
        foreach (var client in _clients.Values)
        {
            SendRaw(client, json);
        }
    }

    private void Send(Client client, object message) =>
        SendRaw(client, JsonSerializer.Serialize(message, Json.Options));

    private void SendRaw(Client client, string json)
    {
        if (!client.Outgoing.Writer.TryWrite(json))
        {
            // 256 unread messages (~4 min of telemetry) — the client stopped
            // reading. Disposing its pipe unblocks both of its loops.
            try { client.Pipe.Dispose(); } catch { }
        }
    }
}
