using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;
using Qubic.Toolkit;
using Qubic.Toolkit.Components;

namespace Qubic.Toolkit;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--server"))
        {
            // WinExe suppresses the console; allocate one for server mode
            // so the user can see the URL and press Ctrl+C to stop.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                AllocConsole();
            RunServer(args);
        }
        else
        {
            try
            {
                RunDesktop(args);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex.InnerException is DllNotFoundException)
            {
                Console.Error.WriteLine("Desktop mode failed: native library not available.");
                Console.Error.WriteLine(ex.InnerException?.Message ?? ex.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Falling back to server mode (--server)...");
                Console.Error.WriteLine();
                RunServer(args);
            }
        }
    }

    static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ToolkitSettingsService>();
        services.AddSingleton<ContractDiscovery>();
        services.AddSingleton<ToolkitBackendService>();
        services.AddSingleton<SeedSessionService>();
        services.AddSingleton<TickMonitorService>();
        services.AddSingleton<TransactionTrackerService>();
        services.AddSingleton<AssetRegistryService>();
        services.AddSingleton<PeerAutoDiscoverService>();
    }

    static void RunDesktop(string[] args)
    {
        var wwwrootPath = GetWwwrootPath();
        var fileProvider = new PhysicalFileProvider(wwwrootPath);

        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(fileProvider, args);
        RegisterServices(appBuilder.Services);
        appBuilder.RootComponents.Add<Routes>("app");

        var app = appBuilder.Build();
        var iconPath = GetIconPath();
        app.MainWindow
            .SetTitle("Qubic.Net Toolkit")
            .SetSize(1400, 900);
        if (iconPath != null)
            app.MainWindow.SetIconFile(iconPath);

        AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        {
            app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString());
        };

        app.Run();
    }

    static void RunServer(string[] args)
    {
        var wwwrootPath = GetWwwrootPath();

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        RegisterServices(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.WebRootPath = wwwrootPath;

        var app = builder.Build();
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwrootPath)
        });
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5060";
            Console.WriteLine($"Qubic.Net Toolkit running at {address}");
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
    }

    /// <summary>
    /// Returns an absolute path to icon.ico, extracting from embedded resources if needed.
    /// </summary>
    static string? GetIconPath()
    {
        // Dev / normal publish: icon.ico copied to output directory
        var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        if (File.Exists(basePath))
            return basePath;

        // Single-file publish: extract from embedded resource
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("icon.ico");
        if (stream == null)
            return null;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Qubic.Toolkit");
        Directory.CreateDirectory(appData);
        var iconPath = Path.Combine(appData, "icon.ico");
        using var fs = File.Create(iconPath);
        stream.CopyTo(fs);
        return iconPath;
    }

    /// <summary>
    /// Returns the path to the wwwroot directory, extracting from the embedded zip if needed.
    /// For dotnet run / normal publish: wwwroot is already at BaseDirectory via Content copy.
    /// For single-file publish: extracts embedded wwwroot.zip to LocalApplicationData.
    /// </summary>
    static string GetWwwrootPath()
    {
        // Check for embedded wwwroot.zip (present in single-file publish)
        var stream = typeof(Program).Assembly.GetManifestResourceStream("wwwroot.zip");
        if (stream == null)
        {
            // No embedded zip — use wwwroot from output directory (dotnet run / normal publish)
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        }

        // Single-file publish: extract to local app data
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Qubic.Toolkit");
        var wwwrootDir = Path.Combine(appData, "wwwroot");
        var versionFile = Path.Combine(wwwrootDir, ".version");
        var currentVersion = typeof(Program).Assembly.ManifestModule.ModuleVersionId.ToString();

        // Already up to date?
        if (Directory.Exists(wwwrootDir) && File.Exists(versionFile)
            && File.ReadAllText(versionFile).Trim() == currentVersion)
        {
            stream.Dispose();
            return wwwrootDir;
        }

        // Extract
        using (stream)
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            if (Directory.Exists(wwwrootDir))
                Directory.Delete(wwwrootDir, true);
            Directory.CreateDirectory(wwwrootDir);
            archive.ExtractToDirectory(wwwrootDir);
        }

        File.WriteAllText(versionFile, currentVersion);
        return wwwrootDir;
    }
}
