using System.Diagnostics;
using Dikte.Core;

namespace Dikte;

/// <summary>Lives in the notification area and runs the whole loop: hotkey → record → transcribe → insert.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    readonly Transcriber _api;
    readonly NotifyIcon _tray;
    readonly Overlay _overlay = new();
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 150 };
    readonly SynchronizationContext _ui;
    readonly Icon _idleIcon, _recordingIcon, _workingIcon;
    readonly FileSystemWatcher _settingsWatcher;

    Settings _settings;
    HotkeyMachine _machine;
    KeyboardHook? _hook;
    History _history;

    Recorder? _recorder;
    DateTime _recordingStarted;
    bool _feedbackShown;
    int _pending;
    CancellationTokenSource _cancel = new();
    Task _lastInsert = Task.CompletedTask;
    byte[]? _failedAudio;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _api = new Transcriber(_http);
        _settings = LoadSettings(out var problem);
        _machine = NewMachine(_settings);
        _history = new History(Paths.History, _settings.HistorySize);

        _idleIcon = TrayIcons.Make(Color.FromArgb(228, 228, 231), null);
        _recordingIcon = TrayIcons.Make(Color.White, Color.FromArgb(239, 68, 68));
        _workingIcon = TrayIcons.Make(Color.White, Color.FromArgb(59, 130, 246));

        _tray = new NotifyIcon { Icon = _idleIcon, Visible = true, Text = "Dikte" };
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFromMenu(); };
        BuildMenu(_tray.ContextMenuStrip);

        _hook = new KeyboardHook(OnKey);
        _watch.Tick += (_, _) => OnWatch();
        _watch.Start();

        _settingsWatcher = new FileSystemWatcher(Paths.Dir, Path.GetFileName(Paths.Settings))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _settingsWatcher.Changed += (_, _) => _ui.Post(_ => ReloadSettings(), null);
        _settingsWatcher.Created += (_, _) => _ui.Post(_ => ReloadSettings(), null);

        Log.Write($"Started {Application.ProductVersion}, hotkey {_settings.Hotkey}, model {_settings.Model}");
        if (problem is not null) _overlay.ShowMessage("Settings file has a problem", problem, 8);

        _ui.Post(_ =>
        {
            if (KeyStore.Get() is null) AskForKey();
            else _tray.ShowBalloonTip(3000, "Dikte is ready", $"Hold {Pretty(_settings.Hotkey)} and speak. Release to paste.", ToolTipIcon.None);
        }, null);
    }

    // ---- Settings -------------------------------------------------------------------------------

    static Settings LoadSettings(out string? problem)
    {
        problem = null;
        try { return Settings.LoadOrCreate(Paths.Settings); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException)
        {
            problem = e.Message;
            Log.Write($"Settings unusable, using defaults: {e.Message}");
            return new Settings();
        }
    }

    static HotkeyMachine NewMachine(Settings s)
    {
        KeyNames.TryParse(s.HandsFreeKey, out var lockVk);
        return new HotkeyMachine(HotkeySpec.Parse(s.Hotkey), lockVk);
    }

    DateTime _lastReload;

    async void ReloadSettings()
    {
        // Editors save in several writes; one reload per burst, once the burst is over.
        if (DateTime.Now - _lastReload < TimeSpan.FromMilliseconds(800)) return;
        _lastReload = DateTime.Now;
        await Task.Delay(300);
        try
        {
            var fresh = Settings.LoadOrCreate(Paths.Settings);
            _settings = fresh;
            if (_recorder is null) _machine = NewMachine(fresh);
            _history = new History(Paths.History, fresh.HistorySize);
            _overlay.ShowMessage("Settings reloaded", $"Hotkey {Pretty(fresh.Hotkey)} · {fresh.Model}", 2.5);
            Log.Write($"Settings reloaded: hotkey {fresh.Hotkey}, model {fresh.Model}");
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException)
        {
            _overlay.ShowMessage("Settings not applied", e.Message, 8);
        }
    }

    // ---- Keyboard -------------------------------------------------------------------------------

    bool OnKey(int vk, bool isDown)
    {
        // Esc while a transcript is on its way cancels it, and must not reach the app (it would interrupt Claude Code).
        if (vk == HotkeyMachine.VkEscape && isDown && !_machine.IsRecording && _pending > 0)
        {
            _ui.Post(_ => CancelPending(), null);
            return true;
        }

        var result = _machine.Feed(vk, isDown);
        if (result.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
        if (result.Command != HotkeyCommand.None)
        {
            var command = result.Command;
            _ui.Post(_ => Run(command), null); // never do slow work inside the hook
        }
        return result.Swallow;
    }

    void OnWatch()
    {
        var command = _machine.Resync(Native.IsPhysicallyDown);
        if (command != HotkeyCommand.None) Run(command);

        if (_recorder is null) return;
        var elapsed = DateTime.Now - _recordingStarted;
        // Show and sound only once a hold is clearly meant, so Ctrl+Win+Arrow stays silent.
        if (!_feedbackShown && elapsed > TimeSpan.FromMilliseconds(180))
        {
            _feedbackShown = true;
            var recorder = _recorder;
            _overlay.ShowListening(() => recorder.Level);
            if (_machine.IsLocked) _overlay.ShowHandsFree();
            if (_settings.Sounds) Sounds.Start();
        }
        if (_recorder.Duration.TotalMinutes >= _settings.MaxMinutes)
        {
            _machine.Reset();
            _ = StopAndSendAsync();
        }
    }

    void Run(HotkeyCommand command)
    {
        switch (command)
        {
            case HotkeyCommand.Start: StartRecording(); break;
            case HotkeyCommand.Lock:
                if (_recorder is not null)
                {
                    var recorder = _recorder;
                    _overlay.ShowListening(() => recorder.Level);
                    _overlay.ShowHandsFree();
                    _feedbackShown = true;
                }
                break;
            case HotkeyCommand.StopAndSend: _ = StopAndSendAsync(); break;
            case HotkeyCommand.Cancel: CancelRecording(); break;
        }
    }

    void ToggleFromMenu()
    {
        if (_recorder is null)
        {
            _machine.EnterLocked();
            StartRecording();
        }
        else
        {
            _machine.Reset();
            _ = StopAndSendAsync();
        }
    }

    // ---- Recording ------------------------------------------------------------------------------

    void StartRecording()
    {
        if (_recorder is not null) return;
        try
        {
            _recorder = new Recorder(_settings.Microphone);
            _recorder.Start();
            _recordingStarted = DateTime.Now;
            _feedbackShown = false;
            _tray.Icon = _recordingIcon;
        }
        catch (Exception e)
        {
            _recorder?.Dispose();
            _recorder = null;
            _machine.Reset();
            Log.Write($"Microphone failed: {e}");
            Fail("Microphone not available", e.Message);
        }
    }

    void CancelRecording()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;
        _ = recorder.StopAsync().ContinueWith(_ => recorder.Dispose(), TaskScheduler.Default);
        bool wasShown = _feedbackShown;
        UpdateIcon();
        if (wasShown) _overlay.ShowMessage("Cancelled", "", 1.2);
        else _overlay.HideNow();
    }

    async Task StopAndSendAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;
        bool wasShown = _feedbackShown;
        byte[] wav;
        float maxLevel;
        TimeSpan length;
        try
        {
            wav = await recorder.StopAsync();
            maxLevel = recorder.MaxLevel;
            length = recorder.Duration;
        }
        catch (Exception e)
        {
            Log.Write($"Stop failed: {e}");
            Fail("Recording failed", e.Message);
            return;
        }
        finally
        {
            recorder.Dispose();
            UpdateIcon();
        }

        // A tap, not a hold.
        if (length < TimeSpan.FromMilliseconds(400) || !wasShown)
        {
            _overlay.HideNow();
            return;
        }
        if (maxLevel < 0.015f)
        {
            _overlay.ShowMessage("No sound from the microphone", "Check the mic in the tray menu", 3);
            if (_settings.Sounds) Sounds.Error();
            return;
        }
        if (_settings.Sounds) Sounds.Stop();
        await ProcessAsync(wav, length);
    }

    // ---- Transcribing ---------------------------------------------------------------------------

    async Task ProcessAsync(byte[] wav, TimeSpan length)
    {
        var key = KeyStore.Get();
        if (key is null)
        {
            _failedAudio = wav;
            AskForKey();
            key = KeyStore.Get();
            if (key is null)
            {
                _overlay.ShowMessage("No API key", "Kept the recording: Retry from the tray menu", 5);
                return;
            }
        }

        var settings = _settings;
        var ct = _cancel.Token;
        // Inserts keep the order the recordings were made in, even if a later one transcribes faster.
        var previous = _lastInsert;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastInsert = done.Task;

        _pending++;
        UpdateIcon();
        if (_recorder is null) _overlay.ShowWorking();
        var clock = Stopwatch.StartNew();
        try
        {
            var text = await _api.TranscribeAsync(wav, settings, key, ct);
            var transcribeMs = clock.ElapsedMilliseconds;
            if (settings.Cleanup && text.Length > 0)
            {
                try { text = await _api.CleanupAsync(text, settings, key, ct); }
                catch (ApiException e) { Log.Write($"Clean-up skipped: {e.Message}"); }
            }
            await previous;
            Log.Write($"Transcribed {length.TotalSeconds:0.0}s in {transcribeMs} ms (total {clock.ElapsedMilliseconds} ms), {text.Length} chars");

            if (text.Length == 0)
            {
                if (_recorder is null) _overlay.ShowMessage("Nothing to write", "No words were heard", 2);
                return;
            }
            if (settings.AppendSpace && !char.IsWhiteSpace(text[^1])) text += " ";
            _history.Add(text.TrimEnd());
            _failedAudio = null;
            await Inserter.InsertAsync(text, settings);
            if (_recorder is null) _overlay.ShowDone($"{CountWords(text)} words");
        }
        catch (OperationCanceledException)
        {
            if (_recorder is null) _overlay.ShowMessage("Cancelled", "", 1.2);
        }
        catch (ApiException e)
        {
            _failedAudio = wav;
            try { File.WriteAllBytes(Paths.FailedAudio, wav); } catch (IOException) { }
            Log.Write($"Transcription failed: {e.Message}");
            Fail(e.Message, "Recording kept: Retry from the tray menu");
        }
        catch (Exception e)
        {
            _failedAudio = wav;
            Log.Write($"Unexpected failure: {e}");
            Fail("Something went wrong", e.Message);
        }
        finally
        {
            _pending--;
            done.TrySetResult();
            UpdateIcon();
        }
    }

    void CancelPending()
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
    }

    void Fail(string title, string hint)
    {
        _overlay.ShowMessage(title, hint, 6);
        if (_settings.Sounds) Sounds.Error();
    }

    void UpdateIcon() =>
        _tray.Icon = _recorder is not null ? _recordingIcon : _pending > 0 ? _workingIcon : _idleIcon;

    static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- Menu -----------------------------------------------------------------------------------

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"Hold {Pretty(_settings.Hotkey)} to dictate") { Enabled = false });
        menu.Items.Add(_recorder is null ? "Start hands-free dictation" : "Stop and send", null, (_, _) => ToggleFromMenu());
        if (_failedAudio is not null) menu.Items.Add("Retry last recording", null, (_, _) => RetryFailed());
        menu.Items.Add(new ToolStripSeparator());

        var recent = new ToolStripMenuItem("Recent (click to copy)");
        if (_history.Items.Count == 0) recent.DropDownItems.Add(new ToolStripMenuItem("Nothing yet") { Enabled = false });
        foreach (var entry in _history.Items)
        {
            var label = entry.Text.Length > 70 ? entry.Text[..67] + "…" : entry.Text;
            var text = entry.Text;
            recent.DropDownItems.Add($"{entry.At:HH:mm}  {label.Replace("&", "&&")}", null, (_, _) => Clipboard.SetText(text));
        }
        if (_history.Items.Count > 0)
        {
            recent.DropDownItems.Add(new ToolStripSeparator());
            recent.DropDownItems.Add("Clear", null, (_, _) => _history.Clear());
        }
        menu.Items.Add(recent);

        var mics = new ToolStripMenuItem("Microphone");
        mics.DropDownItems.Add(MicItem("Windows default", ""));
        try
        {
            foreach (var name in Recorder.Devices()) mics.DropDownItems.Add(MicItem(name, name));
        }
        catch (Exception e) { Log.Write($"Listing microphones failed: {e.Message}"); }
        menu.Items.Add(mics);

        var cleanup = new ToolStripMenuItem("Tidy up text (remove ums, fix punctuation)") { Checked = _settings.Cleanup };
        cleanup.Click += (_, _) => Change(s => s.Cleanup = !s.Cleanup);
        menu.Items.Add(cleanup);
        var sounds = new ToolStripMenuItem("Sounds") { Checked = _settings.Sounds };
        sounds.Click += (_, _) => Change(s => s.Sounds = !s.Sounds);
        menu.Items.Add(sounds);
        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.Enabled };
        autostart.Click += (_, _) => Autostart.Enabled = !Autostart.Enabled;
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set API key…", null, (_, _) => AskForKey());
        menu.Items.Add("Edit settings…", null, (_, _) => Open(Paths.Settings));
        menu.Items.Add("Open log", null, (_, _) => Open(Paths.Log));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }

    ToolStripMenuItem MicItem(string label, string value)
    {
        var item = new ToolStripMenuItem(label) { Checked = string.Equals(_settings.Microphone, value, StringComparison.OrdinalIgnoreCase) };
        item.Click += (_, _) => Change(s => s.Microphone = value);
        return item;
    }

    void Change(Action<Settings> edit)
    {
        edit(_settings);
        _lastReload = DateTime.Now; // our own write: no "reloaded" toast
        try { _settings.Save(Paths.Settings); }
        catch (IOException e) { Log.Write($"Saving settings failed: {e.Message}"); }
    }

    void RetryFailed()
    {
        var wav = _failedAudio;
        if (wav is null) return;
        _ = ProcessAsync(wav, TimeSpan.FromSeconds((wav.Length - 44) / (Recorder.SampleRate * 2.0)));
    }

    void AskForKey()
    {
        using var dialog = new ApiKeyDialog(_api, _settings);
        if (dialog.ShowDialog() == DialogResult.OK)
            _tray.ShowBalloonTip(3000, "Dikte is ready", $"Hold {Pretty(_settings.Hotkey)} and speak. Release to paste.", ToolTipIcon.None);
    }

    static void Open(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    static string Pretty(string hotkey) =>
        string.Join(" + ", hotkey.Split('+', StringSplitOptions.TrimEntries).Select(p => p.Length <= 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));

    protected override void ExitThreadCore()
    {
        _hook?.Dispose();
        _hook = null;
        _watch.Stop();
        _settingsWatcher.Dispose();
        _recorder?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        _http.Dispose();
        base.ExitThreadCore();
    }
}

static class TrayIcons
{
    /// <summary>A microphone glyph from Windows 11's own icon font, on an optional coloured disc.</summary>
    public static Icon Make(Color glyph, Color? disc)
    {
        int size = SystemInformation.SmallIconSize.Width * 2;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (disc is { } d)
            {
                using var brush = new SolidBrush(d);
                g.FillEllipse(brush, 0, 0, size - 1, size - 1);
            }
            using var font = IconFont(size * 0.62f);
            using var fg = new SolidBrush(glyph);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("", font, fg, new RectangleF(0, size * 0.04f, size, size), fmt);
        }
        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Native.DestroyIcon(handle);
        return icon;
    }

    static Font IconFont(float px)
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var font = new Font(name, px, GraphicsUnit.Pixel);
            if (font.Name == name) return font;
            font.Dispose();
        }
        return new Font(FontFamily.GenericSansSerif, px, GraphicsUnit.Pixel);
    }
}
