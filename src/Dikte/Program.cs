namespace Dikte;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\Dikte.SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Dikte.ShowWindow");
        int from = Array.IndexOf(args, "--updated-from");
        string? updatedFrom = from >= 0 && from + 1 < args.Length ? args[from + 1] : null;
        if (!first && updatedFrom is not null)
        {
            // Started by an update: the old version is quitting and lets go of the mutex in a moment.
            try { first = single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
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
        Application.Run(new TrayApp(openWindow: !args.Contains("--background"), showSignal, updatedFrom));
        single.ReleaseMutex(); // an update's new process is waiting for it
    }
}
