using System.Configuration;
using System.Data;
using System.Windows;
using System.Media;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TRPServerPanel.Views;
using TRPServerPanel.Services;
using TRPServerPanel.ViewModels;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;

namespace TRPServerPanel;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;
    public static CoreWebView2Environment? SharedEnvironment { get; private set; }
    public static string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public App()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Network
        services.AddSingleton<System.Net.Http.HttpClient>(sp => {
            var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            return client;
        });

        // Services
        services.AddSingleton<ServerManager>();
        services.AddSingleton<RconService>();
        services.AddSingleton<GeminiService>();
        services.AddSingleton<PluginService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<WipeService>();
        services.AddSingleton<A2SQueryService>();
        services.AddSingleton<SystemService>();
        services.AddSingleton<NetworkService>();
        services.AddSingleton<RustDatabaseService>();
        services.AddSingleton<PlayerHistoryService>();
        services.AddSingleton<SteamApiService>();
        services.AddSingleton<NotificationService>();

        // ViewModels
        services.AddSingleton<ConsoleViewModel>();
        services.AddSingleton<MarketplaceViewModel>();
        services.AddSingleton<ServerManagerViewModel>();
        services.AddSingleton<MainViewModel>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // [CRITICAL] Prevent WPF from shutting down when Splash closes
        this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Early Boot Log
        try { File.WriteAllText("boot.log", $"[{DateTime.Now}] STAGE: Booting\n"); } catch { }

        // Global Error Handling
        AppDomain.CurrentDomain.UnhandledException += (s, ev) => ShowFatalError(ev.ExceptionObject as Exception, "AppDomain Early Crash");
        this.DispatcherUnhandledException += (s, ev) => { if (ev.Exception != null) ShowFatalError(ev.Exception, "Dispatcher UI Crash"); ev.Handled = true; };
        TaskScheduler.UnobservedTaskException += (s, ev) => { if (ev.Exception != null) ShowFatalError(ev.Exception, "Async Task Crash"); ev.SetObserved(); };

        base.OnStartup(e);

        // Check if WebView2 runtime is installed
        if (!IsWebView2RuntimeInstalled())
        {
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: WebView2 missing. Showing installer.\n"); } catch { }
            var installer = new WebView2InstallerWindow();
            if (installer.ShowDialog() != true)
            {
                try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: WebView2 installation declined or failed. Exiting.\n"); } catch { }
                System.Windows.Application.Current.Shutdown();
                return;
            }
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: WebView2 installed successfully.\n"); } catch { }
        }

        // [PRE-WARM] Start WebView2 environment initialization in background (now safe to run)
        _ = GetSharedEnvironmentAsync();

        try
        {
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Creating Splash\n"); } catch { }
            // 1. Show TRP Orchestrator (Splash with real diagnostics)
            var splash = new SplashWindow();
            
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Showing Splash Dialog\n"); } catch { }
            bool? result = splash.ShowDialog();
            
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Splash Closed (Result: {result})\n"); } catch { }
            
            if (result != true)
            {
                try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Manual Shutdown via Splash\n"); } catch { }
                System.Windows.Application.Current.Shutdown();
                return;
            }

            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Creating MainWindow\n"); } catch { }
            // 2. Transition to Main Window
            var main = new MainWindow();
            
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Showing MainWindow\n"); } catch { }
            this.MainWindow = main;
            main.Show();
            
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: Initialization Complete\n"); } catch { }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText("boot.log", $"[{DateTime.Now}] STAGE: FATAL ERROR - {ex.Message}\n"); } catch { }
            ShowFatalError(ex, "MainWindow Startup Failure");
        }
    }

    public static bool IsWebView2RuntimeInstalled()
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        if (SharedEnvironment != null) return SharedEnvironment;

        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            "TRPServerPanel", "WebView2");
        
        if (!Directory.Exists(userDataFolder))
            Directory.CreateDirectory(userDataFolder);

        SharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        return SharedEnvironment;
    }

    public static void ShowFatalError(Exception? ex, string source)
    {
        if (ex == null) return;
        string message = ex.Message;
        string errorDetails = ex.ToString();

        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                try 
                {
                    var crashWin = new CrashReportWindow($"Source: {source}\nError: {message}", errorDetails);
                    crashWin.ShowDialog();
                }
                catch
                {
                    // Fallback to standard MessageBox if custom UI fails
                    System.Windows.MessageBox.Show(errorDetails, "TRP PANEL | FATAL ERROR");
                }
            });
        }
        else
        {
            // Early crash or no Dispatcher
            System.Windows.MessageBox.Show(errorDetails, "TRP PANEL | EARLY FATAL ERROR");
        }
        
        try { System.Windows.Application.Current?.Shutdown(); } catch { Environment.Exit(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { ServiceProvider?.GetRequiredService<RustDatabaseService>()?.ClearCache(); } catch { }
        try { _ = ServiceProvider?.GetRequiredService<RconService>()?.DisconnectAsync(); } catch { }
        base.OnExit(e);
        Environment.Exit(0);
    }
}
