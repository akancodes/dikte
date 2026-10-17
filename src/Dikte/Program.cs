namespace Dikte;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, @"Local\Dikte.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Dikte is already running. Look for the microphone in the notification area.", "Dikte",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        // The context reads SynchronizationContext.Current, which WinForms installs with the first control.
        using var anchor = new Control();
        anchor.CreateControl();
        Application.Run(new TrayApp());
    }
}
