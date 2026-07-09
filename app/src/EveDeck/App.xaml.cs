using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EveDeck;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    // App.xaml is no longer an ApplicationDefinition (see EveDeck.csproj), so this is the real
    // entry point. VelopackApp.Build().Run() must be the very first thing that runs -- it handles
    // Velopack's own hidden install/update/uninstall shortcut-management invocations and exits
    // immediately when called that way, so nothing below it runs in that case (by design).
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "EveDeck.SingleInstance", out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        // Also block alongside pre-1.3.1 builds that used the old mutex name.
        Mutex? legacyMutex = null;
        var legacyRunning = isFirstInstance && Mutex.TryOpenExisting("EveWindowCommander.SingleInstance", out legacyMutex);
        legacyMutex?.Dispose();

        if (!isFirstInstance || legacyRunning)
        {
            MessageBox.Show(
                "EveDeck is already running. Close the existing window before starting another copy.",
                "EveDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        DpiBootstrap.TryEnablePerMonitorV2();

        // Custom splash (the built-in WPF SplashScreen isn't DPI-aware, so it drifts off-centre
        // on scaled displays). CenterScreen positioning here respects per-monitor DPI.
        var splash = new Views.SplashWindow();
        splash.Show();

        base.OnStartup(e);

        // No StartupUri anymore (see EveDeck.csproj) -- base.OnStartup no longer creates the
        // window for us, so do it explicitly.
        var mainWindow = new Views.MainWindow();
        MainWindow = mainWindow;
        mainWindow.ContentRendered += (_, _) => CloseSplash(splash);
        mainWindow.Show();
    }

    private static void CloseSplash(Window splash)
    {
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            new Duration(TimeSpan.FromMilliseconds(250)));
        fade.Completed += (_, _) => splash.Close();
        splash.BeginAnimation(Window.OpacityProperty, fade);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"EveDeck hit a startup error and wrote a crash log.\n\n{e.Exception.Message}",
            "EveDeck",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null) return;
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EveDeck",
                "logs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
        }
        catch
        {
            // Last-chance logging should never cause another crash.
        }
    }
}

internal static class DpiBootstrap
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    public static void TryEnablePerMonitorV2()
    {
        try
        {
            SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { } // best-effort DPI fallback
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}
