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
    private Services.ProtocolHandlerService? _protocolHandler;

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

        // Registered here, not in MainWindow.xaml. Declaring it in XAML needs an
        // xmlns:clr-namespace pointing back at this assembly, and any local-type reference forces
        // WPF's markup-compile pass 2: it builds a throwaway <Name>_<random>_wpftmp project on
        // every build, which spams the IDE's design-time builds with spurious errors (duplicate
        // Compile items, missing nuget.g.targets for a temp project that was never restored).
        // Application.Resources is populated before Run() creates any window, so MainWindow's
        // StaticResource lookups still resolve at parse time.
        app.Resources["IndexToVisibility"] = new Converters.IndexToVisibilityConverter();

        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "EveDeck.SingleInstance", out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        // The OS starts a fresh instance to service an evedeck:// link; hand the URL to the
        // running instance over its protocol pipe and exit silently.
        var protocolUrl = e.Args.FirstOrDefault(a =>
            a.StartsWith($"{Services.ProtocolHandlerService.Scheme}://", StringComparison.OrdinalIgnoreCase));

        if (!isFirstInstance)
        {
            if (protocolUrl is not null && Services.ProtocolHandlerService.TryForwardToRunningInstance(protocolUrl))
            {
                Shutdown(0);
                return;
            }

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

        // Opt out of Windows' background-process power throttling (EcoQoS) -- see the doc comment
        // on Win32Native.DisableOwnProcessPowerThrottling for why: EveDeck's main window is usually
        // hidden to tray, so Task Manager buckets it as "Background", which can throttle exactly the
        // time-sensitive Win32 calls (hover-peek z-order reassertion) that need to run promptly.
        Utilities.Win32Native.DisableOwnProcessPowerThrottling();

        DpiBootstrap.TryEnablePerMonitorV2();

        // Custom splash (the built-in WPF SplashScreen isn't DPI-aware, so it drifts off-center
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

        // evedeck:// protocol: refresh the per-user registration (self-heals a moved exe) and
        // listen for URLs forwarded by secondary instances. Commands route through the
        // view-model's SafetyGuard-validated dispatcher only.
        try { Services.ProtocolHandlerService.RegisterUrlProtocol(); } catch { } // best-effort; registry may be locked down
        _protocolHandler = new Services.ProtocolHandlerService();
        _protocolHandler.StartServer(url =>
            Dispatcher.BeginInvoke(() => mainWindow.HandleProtocolUrl(url)));

        // Native Windows toast notifications (Action Center mirror of EveDeck's own toast popups --
        // see NativeNotificationService). Same best-effort posture as the protocol registration above.
        try
        {
            Services.NativeNotificationService.Initialize(payload =>
                Dispatcher.BeginInvoke(() => mainWindow.HandleNativeNotificationActivated(payload)));
        }
        catch { } // best-effort; OS notification plumbing is outside EveDeck's control

        // Launched directly via a link with no prior instance: run the command once the UI is up.
        if (protocolUrl is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                () => mainWindow.HandleProtocolUrl(protocolUrl));
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
        _protocolHandler?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    // True once this has already fired once for the current crash. Without this, an exception
    // thrown while WE handle a crash (e.g. Current.Shutdown() itself faulting mid-teardown) gets
    // caught by the next dispatcher frame up and re-enters this same method -- which showed another
    // MessageBox and wrote another crash log each time, cascading through every nested dispatcher
    // frame (main loop, any open ShowDialog) with the user never seeing any of the dialogs before
    // the process finally died. See the crash-2026071*.log trio this pattern produced.
    private static bool _handlingCrash;

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_handlingCrash)
        {
            Environment.Exit(1);
            return;
        }
        _handlingCrash = true;

        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"EveDeck hit an unexpected error and wrote a crash log.\n\n{e.Exception.Message}",
            "EveDeck",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;

        // Current can be null here (observed in practice, cause not fully understood -- possibly
        // WPF clearing it mid-Shutdown on a re-entrant call); fall back to a hard exit rather than
        // NRE-ing out of our own crash handler.
        if (Current is not null) Current.Shutdown(1);
        else Environment.Exit(1);
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
