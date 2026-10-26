using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Dikte;

/// <summary>
/// The app window: one HTML page (ui/index.html, embedded) in WebView2, talking to the app through
/// JSON messages. Closing it disposes it, so the browser engine does not sit in memory while Dikte
/// waits in the tray.
/// </summary>
sealed class MainWindow : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly bool _dark = IsDarkMode();

    public event Action<JsonElement>? Message;

    public MainWindow()
    {
        Text = "Dikte";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(640, 560);
        BackColor = _dark ? Color.FromArgb(17, 17, 19) : Color.FromArgb(246, 246, 247);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = _dark ? 1 : 0;
        Native.DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Dir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Log.Write($"WebView2 unavailable: {e.Message}");
            ShowRuntimeMissing();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                Message?.Invoke(doc.RootElement.Clone());
            }
            catch (Exception ex) { Log.Write($"Window message failed: {ex}"); }
        };
        // The page is the whole app: nothing navigates away, links open in the browser.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenUrl(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri == "about:blank") return;
            e.Cancel = true;
            OpenUrl(e.Uri);
        };
        _web.NavigateToString(LoadPage());
    }

    /// <summary>Sends a message to the page; false when the page is not there yet to take it.</summary>
    public bool Post(object message)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return false;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Core.Settings.Json));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or COMException) { return false; }
    }

    public void ShowAndFocus()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    static string LoadPage()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("index.html")
            ?? throw new InvalidOperationException("The window page is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    void ShowRuntimeMissing()
    {
        Controls.Remove(_web);
        var link = new LinkLabel
        {
            Text = "Dikte's window needs Microsoft Edge WebView2, which is missing.\n\nGet it from Microsoft (free), then open Dikte again.\nDictation itself keeps working from the tray.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f),
            ForeColor = _dark ? Color.White : Color.Black,
        };
        link.LinkArea = new LinkArea(link.Text.IndexOf("Get it", StringComparison.Ordinal), 6);
        link.LinkClicked += (_, _) => OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
        Controls.Add(link);
    }

    static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
