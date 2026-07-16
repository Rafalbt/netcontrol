// Przepustnica backend service (Etap 1 of PLAN_WDROZENIA_WINDOWS.md):
// real per-process traffic monitoring over ETW, rules + history in SQLite,
// and a named-pipe IPC server streaming 1 Hz telemetry to the Tauri UI.
//
// Run modes:
//   dotnet run                      — console mode for development (elevated!)
//   sc create Przepustnica ...      — Windows Service (Etap 4 packaging)
//
// ETW kernel sessions and reading other processes' module paths require
// admin/SYSTEM, so an elevated terminal is needed in console mode.

using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrzepustnicaService;

using (var identity = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine(
            "UWAGA: proces nie jest uruchomiony jako administrator — sesja ETW (licznik ruchu) nie wystartuje.");
    }
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Przepustnica");

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<IpcAuthToken>();
builder.Services.AddSingleton<EtwTrafficCounter>();
builder.Services.AddSingleton<ProcessCatalog>();
builder.Services.AddSingleton<IpcServer>();
builder.Services.AddSingleton<Throttler>();

builder.Services.AddHostedService(sp => sp.GetRequiredService<EtwTrafficCounter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessCatalog>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpcServer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Throttler>());
builder.Services.AddHostedService<TelemetryLoop>();

var host = builder.Build();
host.Run();
