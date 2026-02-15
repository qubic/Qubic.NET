using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Photino.Blazor;
using Qubic.Services;
using Qubic.Services.Storage;
using Qubic.Net.Wallet.Components;

namespace Qubic.Net.Wallet;

class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--server"))
        {
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
        SQLitePCL.Batteries_V2.Init();

        services.AddSingleton(new QubicSettingsService("QubicWallet"));
        services.AddSingleton<QubicBackendService>();
        services.AddSingleton<SeedSessionService>();
        services.AddSingleton<TickMonitorService>();
        services.AddSingleton<WalletDatabase>();
        services.AddSingleton<WalletSyncService>();
        services.AddSingleton<WalletStorageService>();
        services.AddSingleton<TransactionTrackerService>();
        services.AddSingleton<AssetRegistryService>();
        services.AddSingleton<PeerAutoDiscoverService>();
        services.AddSingleton<LabelService>();
        services.AddLocalization();
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
            .SetTitle("Qubic.Net Wallet")
            .SetSize(1200, 800);
        if (iconPath != null)
            app.MainWindow.SetIconFile(iconPath);

        AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
        {
            app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject.ToString());
        };

        app.Run();
    }

    const string SessionCookieName = ".QubicWallet.Session";

    static void RunServer(string[] args)
    {
        var wwwrootPath = GetWwwrootPath();
        var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        RegisterServices(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.WebRootPath = wwwrootPath;

        var app = builder.Build();

        // Session token middleware: validates every request before anything else.
        // First request arrives with ?token=xxx — we set an HttpOnly cookie and redirect
        // to strip the token from the URL. All subsequent requests are validated via cookie.
        app.Use(async (context, next) =>
        {
            // Check for token in query string (initial browser open)
            if (context.Request.Query.TryGetValue("token", out var tokenValue)
                && tokenValue.ToString() == sessionToken)
            {
                context.Response.Cookies.Append(SessionCookieName, sessionToken, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = false, // localhost HTTP
                    Path = "/",
                    IsEssential = true
                });

                // Redirect to root to strip token from URL / browser history
                context.Response.Redirect("/");
                return;
            }

            // Validate cookie on all requests
            if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var cookie)
                || cookie != sessionToken)
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("Access denied. Open the app from the URL shown in the console.");
                return;
            }

            await next();
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwrootPath)
        });
        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5060";
            var authUrl = $"{address}?token={sessionToken}";
            Console.WriteLine($"Qubic.Net Wallet running at {address}");
            Console.WriteLine($"Session token: {sessionToken}");
            Console.WriteLine();
            Console.WriteLine($"Open in browser: {authUrl}");
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start("open", authUrl);
                else
                    Process.Start("xdg-open", authUrl);
            }
            catch { /* Browser auto-open is best-effort */ }
        });

        app.Run();
    }

    static string? GetIconPath()
    {
        var basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
        if (File.Exists(basePath))
            return basePath;

        using var stream = typeof(Program).Assembly.GetManifestResourceStream("icon.ico");
        if (stream == null)
            return null;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QubicWallet");
        Directory.CreateDirectory(appData);
        var iconPath = Path.Combine(appData, "icon.ico");
        using var fs = File.Create(iconPath);
        stream.CopyTo(fs);
        return iconPath;
    }

    static string GetWwwrootPath()
    {
        var stream = typeof(Program).Assembly.GetManifestResourceStream("wwwroot.zip");
        if (stream == null)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QubicWallet");
        var wwwrootDir = Path.Combine(appData, "wwwroot");
        var versionFile = Path.Combine(wwwrootDir, ".version");
        var currentVersion = typeof(Program).Assembly.ManifestModule.ModuleVersionId.ToString();

        if (Directory.Exists(wwwrootDir) && File.Exists(versionFile)
            && File.ReadAllText(versionFile).Trim() == currentVersion)
        {
            stream.Dispose();
            return wwwrootDir;
        }

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
