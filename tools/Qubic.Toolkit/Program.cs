using System.Diagnostics;
using System.Runtime.InteropServices;
using Qubic.Toolkit;
using Qubic.Toolkit.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ContractDiscovery>();
builder.Services.AddSingleton<ToolkitBackendService>();
builder.Services.AddSingleton<SeedSessionService>();
builder.Services.AddSingleton<TickMonitorService>();
builder.Services.AddSingleton<TransactionTrackerService>();
builder.Services.AddSingleton<ToolkitSettingsService>();

builder.WebHost.UseUrls("http://127.0.0.1:0");

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5060";
    Console.WriteLine($"Qubic Toolkit running at {address}");
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start("open", address);
        else
            Process.Start("xdg-open", address);
    }
    catch { /* Browser auto-open is best-effort */ }
});

app.Run();
