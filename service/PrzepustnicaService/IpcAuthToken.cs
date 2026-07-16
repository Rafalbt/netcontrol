using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace PrzepustnicaService;

// Shared-secret handshake for the IPC pipe (PLAN_WDROZENIA_WINDOWS.md §2:
// "named pipe + auth token"). The service persists a random token under
// %ProgramData%\Przepustnica; the UI reads the same file and must present it
// in its `hello` before any command is accepted. The pipe ACL (Authenticated
// Users) is the primary gate — the token additionally binds clients to this
// machine's filesystem so a remote pipe connection alone is not enough.
public sealed class IpcAuthToken
{
    public string Value { get; }

    public IpcAuthToken(ILogger<IpcAuthToken> logger)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Przepustnica");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ipc.token");

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32)
                {
                    Value = existing;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read existing IPC token — generating a new one");
        }

        Value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, Value);
        logger.LogInformation("IPC auth token written to {Path}", path);
    }
}
