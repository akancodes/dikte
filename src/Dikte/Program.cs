namespace Dikte;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\Dikte.SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Dikte.ShowWindow");
        if (!first)
        {
            // Already running: bring its window up instead of starting twice.
            showSignal.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Write($"UI thread error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Background task error: {e.Exception}");
            e.SetObserved();
        };

        Directory.CreateDirectory(Paths.Dir);
        Ducker.RecoverFromCrash();
        // The context reads SynchronizationContext.Current, which WinForms installs with the first control.
        using var anchor = new Control();
        anchor.CreateControl();
        Application.Run(new TrayApp(openWindow: !args.Contains("--background"), showSignal));
    }
}
