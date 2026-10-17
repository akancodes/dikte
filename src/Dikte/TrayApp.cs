using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dikte.Core;

namespace Dikte;

/// <summary>Lives in the notification area and runs the whole loop: hotkey → record → transcribe → insert.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    readonly Transcriber _api;
    readonly NotifyIcon _tray;
    readonly Overlay _overlay = new();
    readonly Ducker _ducker = new();
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 150 };
    readonly System.Windows.Forms.Timer _levelTick = new() { Interval = 40 };
    readonly SynchronizationContext _ui;
    readonly Icon _idleIcon, _recordingIcon, _workingIcon;
    readonly FileSystemWatcher _settingsWatcher;
    readonly Stats _stats = new(Paths.Stats);
    readonly RegisteredWaitHandle _showRequests;
    readonly Updater _updater = new();
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 4 * 60 * 60 * 1000 };

    Settings _settings;
    HotkeyMachine _machine;
    ToggleKey? _toggle;
    KeyboardHook? _hook;
    MouseHook? _mouseHook;
    KeyCapture? _capture;
    string? _captureTarget;
    DateTime _captureStarted;
    readonly HashSet<int> _eatUps = new();
    History _history;
    MainWindow? _window;

    Recorder? _recorder;
    Recorder? _micTest;
    DateTime _recordingStarted;
    bool _feedbackShown;
    int _pending;
    CancellationTokenSource _cancel = new();
    Task _lastInsert = Task.CompletedTask;
    byte[]? _failedAudio;
    string? _errorMessage, _errorHint;
    DateTime _lastReload;
    DateTime _lastUse = DateTime.Now;
    string? _staged;
    bool _updating;
    string _updateStatus = "";

    public TrayApp(bool openWindow, EventWaitHandle showSignal, string? updatedFrom)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _api = new Transcriber(_http);
        _settings = LoadSettings(out var problem);
        _machine = NewMachine(_settings);
        _toggle = NewToggle(_settings);
        _history = new History(Paths.History, _settings.HistorySize);

        _idleIcon = TrayIcons.Load("idle");
        _recordingIcon = TrayIcons.Load("listening");
        _workingIcon = TrayIcons.Load("working");

        _tray = new NotifyIcon { Icon = _idleIcon, Visible = true, Text = "Dikte" };
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };
        BuildMenu(_tray.ContextMenuStrip);

        _hook = new KeyboardHook(OnKey);
        UpdateMouseHook();
        _watch.Tick += (_, _) => OnWatch();
        _watch.Start();
        _levelTick.Tick += (_, _) => OnLevelTick();

        _settingsWatcher = new FileSystemWatcher(Paths.Dir, Path.GetFileName(Paths.Settings))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _settingsWatcher.Changed += (_, _) => _ui.Post(_ => ReloadSettings(), null);
        _settingsWatcher.Created += (_, _) => _ui.Post(_ => ReloadSettings(), null);

        // A second launch of the exe asks this one to show its window.
        _showRequests = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => _ui.Post(_ => OpenWindow(), null), null, -1, false);

        Log.Write($"Started {Application.ProductVersion}, hotkey {_settings.Hotkey}, model {_settings.Model}");
        if (problem is not null) _overlay.ShowMessage("Settings file has a problem", problem, 8);
        else if (updatedFrom is not null) _overlay.ShowMessage($"Dikte updated to {Updater.CurrentText}", $"From {updatedFrom}", 3);

        _ = Updater.CleanUpAsync();
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ => _ui.Post(_ => _ = CheckForUpdatesAsync(manual: false), null), TaskScheduler.Default);

        // Rewrites the sign-in entry with this exe's path and flags (it may have moved or come from an older version).
        if (Autostart.Enabled) Autostart.Enabled = true;

        _ui.Post(_ =>
        {
            if (openWindow || KeyStore.Get() is null) OpenWindow();
        }, null);
    }

    // ---- Settings -------------------------------------------------------------------------------

    static Settings LoadSettings(out string? problem)
    {
        problem = null;
        try { return Settings.LoadOrCreate(Paths.Settings); }
        catch (Exception e) when (e is FormatException or JsonException or IOException)
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

    static ToggleKey? NewToggle(Settings s) =>
        string.IsNullOrWhiteSpace(s.ToggleHotkey) ? null : new ToggleKey(HotkeySpec.Parse(s.ToggleHotkey));

    /// <summary>The mouse hook sees every mouse move, so it runs only while a shortcut (or a capture) needs it.</summary>
    void UpdateMouseHook()
    {
        bool needed = _capture is not null || HotkeySpec.Parse(_settings.Hotkey).UsesMouse
            || (!string.IsNullOrWhiteSpace(_settings.ToggleHotkey) && HotkeySpec.Parse(_settings.ToggleHotkey).UsesMouse);
        if (needed && _mouseHook is null)
        {
            try { _mouseHook = new MouseHook(OnKey); }
            catch (InvalidOperationException e) { Log.Write(e.Message); }
        }
        else if (!needed && _mouseHook is not null)
        {
            _mouseHook.Dispose();
            _mouseHook = null;
        }
    }

    void Apply(Settings fresh)
    {
        bool hotkeyChanged = fresh.Hotkey != _settings.Hotkey || fresh.HandsFreeKey != _settings.HandsFreeKey;
        bool toggleChanged = fresh.ToggleHotkey != _settings.ToggleHotkey;
        bool updatesTurnedOn = fresh.AutoUpdate && !_settings.AutoUpdate;
        _settings = fresh;
        if (updatesTurnedOn) _ = CheckForUpdatesAsync(manual: false);
        if (hotkeyChanged && _recorder is null) _machine = NewMachine(fresh);
        if (toggleChanged) _toggle = NewToggle(fresh);
        UpdateMouseHook();
        _history = new History(Paths.History, fresh.HistorySize);
        PushState();
    }

    async void ReloadSettings()
    {
        // Editors save in several writes; one reload per burst, once the burst is over.
        if (DateTime.Now - _lastReload < TimeSpan.FromMilliseconds(800)) return;
        _lastReload = DateTime.Now;
        await Task.Delay(300);
        try
        {
            Apply(Settings.LoadOrCreate(Paths.Settings));
            _overlay.ShowMessage("Settings reloaded", $"Hotkey {Pretty(_settings.Hotkey)} · {_settings.Model}", 2.5);
            Log.Write($"Settings reloaded: hotkey {_settings.Hotkey}, model {_settings.Model}");
        }
        catch (Exception e) when (e is FormatException or JsonException or IOException)
        {
            _overlay.ShowMessage("Settings not applied", e.Message, 8);
        }
    }

    /// <summary>Merges a partial settings object from the window, checks it, saves it.</summary>
    void SaveFromWindow(JsonElement patch)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(_settings, Settings.Json)!.AsObject();
            foreach (var prop in patch.EnumerateObject()) node[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            var fresh = node.Deserialize<Settings>(Settings.Json)!;
            fresh.Validate();
            _lastReload = DateTime.Now; // our own write: no "reloaded" toast
            fresh.Save(Paths.Settings);
            Apply(fresh);
            _window?.Post(new { type = "toast", text = "Saved" });
        }
        catch (Exception e) when (e is FormatException or JsonException or IOException or InvalidOperationException)
        {
            _window?.Post(new { type = "toast", text = e.Message });
            PushState();
        }
    }

    // ---- Keyboard -------------------------------------------------------------------------------

    bool OnKey(int vk, bool isDown)
    {
        // The release of a key or button whose press was a captured shortcut.
        if (!isDown && _eatUps.Remove(vk)) return true;

        if (_capture is not null)
        {
            var outcome = _capture.Feed(vk, isDown);
            if (outcome != CaptureOutcome.Waiting)
            {
                if (isDown) _eatUps.Add(vk);
                var picked = _capture.Result;
                _ui.Post(_ => FinishCapture(outcome, picked), null);
            }
            return true; // nothing reaches the apps while a shortcut is being picked
        }

        if (_toggle is not null)
        {
            var t = _toggle.Feed(vk, isDown);
            if (t.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
            if (t.Command == HotkeyCommand.Toggle) _ui.Post(_ => ToggleDictation(), null);
            if (t.Swallow) return true;
        }

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
        _toggle?.Resync(Native.IsPhysicallyDown);
        if (_capture is not null && DateTime.Now - _captureStarted > TimeSpan.FromSeconds(12)) FinishCapture(CaptureOutcome.Cancelled, null);
        if (_recorder is not null || _pending > 0 || _capture is not null || _micTest is not null) _lastUse = DateTime.Now;
        else if (_staged is not null && DateTime.Now - _lastUse > TimeSpan.FromMinutes(1) && _window?.ContainsFocus != true) InstallUpdate();

        if (_recorder is null) return;
        var elapsed = DateTime.Now - _recordingStarted;
        // Show, sound and quieten only once a hold is clearly meant, so Ctrl+Win+Arrow stays silent.
        if (!_feedbackShown && elapsed > TimeSpan.FromMilliseconds(180))
        {
            _feedbackShown = true;
            var recorder = _recorder;
            _overlay.ShowListening(() => recorder.Level);
            if (_machine.IsLocked) _overlay.ShowHandsFree();
            if (_settings.Sounds) Sounds.Start();
            if (_settings.QuietOtherAudio) _ducker.Duck(_settings.QuietLevel);
            PushState();
        }
        if (_recorder.Duration.TotalMinutes >= _settings.MaxMinutes)
        {
            _machine.Reset();
            _ = StopAndSendAsync();
        }
    }

    void OnLevelTick()
    {
        if (_window is { Visible: true } && _recorder is not null && _feedbackShown) _window.Post(new { type = "level", v = _recorder.Level });
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
                    if (!_feedbackShown && _settings.QuietOtherAudio) _ducker.Duck(_settings.QuietLevel);
                    _feedbackShown = true;
                    PushState();
                }
                break;
            case HotkeyCommand.StopAndSend: _ = StopAndSendAsync(); break;
            case HotkeyCommand.Cancel: CancelRecording(); break;
        }
    }

    void ToggleDictation()
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
        StopMicTest();
        try
        {
            _recorder = new Recorder(_settings.Microphone);
            _recorder.Start();
            _recordingStarted = DateTime.Now;
            _feedbackShown = false;
            _errorMessage = _errorHint = null;
            _tray.Icon = _recordingIcon;
            _levelTick.Start();
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

    void EndRecording()
    {
        _levelTick.Stop();
        _ducker.Restore();
    }

    void CancelRecording()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;
        EndRecording();
        _ = recorder.StopAsync().ContinueWith(_ => recorder.Dispose(), TaskScheduler.Default);
        bool wasShown = _feedbackShown;
        UpdateIcon();
        if (wasShown) _overlay.ShowMessage("Cancelled", "", 1.2);
        else _overlay.HideNow();
        PushState();
    }

    async Task StopAndSendAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;
        EndRecording();
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
            PushState();
            return;
        }
        if (maxLevel < 0.015f)
        {
            Fail("No sound from the microphone", "Pick another microphone in Settings");
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
            _overlay.ShowMessage("Connect your OpenAI key first", "The recording is kept: retry it from the tray menu", 5);
            OpenWindow();
            return;
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
        PushState();
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
            int words = CountWords(text);
            _history.Add(text);
            _stats.Record(DateTime.Now, words, length.TotalSeconds, settings.Model);
            _failedAudio = null;

            if (_window is not null && Native.GetForegroundWindow() == _window.Handle)
            {
                // Dictated with Dikte's own window in front: show it there instead of pasting into the page.
                _window.Post(new { type = "result", text });
                _overlay.HideNow();
            }
            else
            {
                if (settings.AppendSpace && !char.IsWhiteSpace(text[^1])) text += " ";
                await Inserter.InsertAsync(text, settings);
                if (_recorder is null) _overlay.ShowDone($"{words} words");
            }
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
            Fail(e.Message, "The recording is kept: Retry from the tray menu");
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
            PushState();
        }
    }

    void CancelPending()
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
    }

    void Fail(string title, string hint)
    {
        _errorMessage = title;
        _errorHint = hint;
        _overlay.ShowMessage(title, hint, 6);
        if (_settings.Sounds) Sounds.Error();
        PushState();
    }

    void UpdateIcon() =>
        _tray.Icon = _recorder is not null ? _recordingIcon : _pending > 0 ? _workingIcon : _idleIcon;

    static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- Picking a shortcut ---------------------------------------------------------------------

    void StartCapture(string target)
    {
        if (_recorder is not null || target is not ("hotkey" or "toggleHotkey")) return;
        _capture = new KeyCapture();
        _captureTarget = target;
        _captureStarted = DateTime.Now;
        UpdateMouseHook();
        PushState();
    }

    void FinishCapture(CaptureOutcome outcome, string? result)
    {
        var target = _captureTarget;
        _capture = null;
        _captureTarget = null;
        UpdateMouseHook();
        if (outcome == CaptureOutcome.Done && target is not null && result is not null)
        {
            Log.Write($"Shortcut picked for {target}: {result}");
            SaveFromWindow(JsonSerializer.SerializeToElement(new Dictionary<string, string> { [target] = result }));
            return;
        }
        if (outcome == CaptureOutcome.Unusable) _window?.Post(new { type = "toast", text = "That key cannot be a shortcut. Try another." });
        PushState();
    }

    // ---- Window ---------------------------------------------------------------------------------

    void OpenWindow()
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainWindow();
            _window.Message += OnWindowMessage;
            _window.FormClosed += (_, _) =>
            {
                StopMicTest();
                if (_capture is not null) FinishCapture(CaptureOutcome.Cancelled, null);
                _window = null;
            };
        }
        _window.ShowAndFocus();
    }

    void OnWindowMessage(JsonElement m)
    {
        switch (m.GetProperty("type").GetString())
        {
            case "ready": PushState(); break;
            case "save": SaveFromWindow(m.GetProperty("settings")); break;
            case "setKey": _ = SetKeyAsync(m.GetProperty("key").GetString() ?? ""); break;
            case "toggleDictation": ToggleDictation(); break;
            case "copy": Clipboard.SetText(m.GetProperty("text").GetString() ?? ""); break;
            case "clearHistory": _history.Clear(); PushState(); break;
            case "autostart": Autostart.Enabled = m.GetProperty("on").GetBoolean(); PushState(); break;
            case "micTest": StartMicTest(); break;
            case "capture": StartCapture(m.GetProperty("target").GetString() ?? ""); break;
            case "captureCancel": FinishCapture(CaptureOutcome.Cancelled, null); break;
            case "openUrl": MainWindow.OpenUrl(m.GetProperty("url").GetString() ?? ""); break;
            case "openLog": Open(Paths.Log); break;
            case "openSettingsFile": Open(Paths.Settings); break;
            case "checkUpdates": _ = CheckForUpdatesAsync(manual: true); break;
        }
    }

    async Task SetKeyAsync(string key)
    {
        key = key.Trim();
        string? problem;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try { problem = await _api.CheckKeyAsync(_settings, key, cts.Token); }
            catch (OperationCanceledException) { problem = "The API did not answer in time."; }
        }
        if (problem is null)
        {
            KeyStore.Save(key);
            Log.Write("API key saved.");
        }
        _window?.Post(new { type = "keyResult", ok = problem is null, message = problem ?? "" });
        PushState();
    }

    void PushState()
    {
        if (_window is null || _window.IsDisposed) return;
        var key = KeyStore.Get();
        var stats = _stats.Summary(DateTime.Now);
        string status =
            _recorder is not null && _feedbackShown ? (_machine.IsLocked ? "handsfree" : "listening")
            : _pending > 0 ? "working"
            : _errorMessage is not null ? "error"
            : "idle";
        IReadOnlyList<string> devices;
        try { devices = Recorder.Devices(); }
        catch (Exception) { devices = Array.Empty<string>(); }

        _window.Post(new
        {
            type = "state",
            state = new
            {
                status,
                message = _errorMessage,
                hint = _errorHint,
                hasKey = key is not null,
                keyHint = key is { Length: > 8 } ? "…" + key[^4..] : "",
                settings = _settings,
                devices,
                languages = Languages.Known.Select(l => new[] { l.Code, l.Name }),
                history = _history.Items.Select(h => new { time = When(h.At), text = h.Text }),
                stats,
                autostart = Autostart.Enabled,
                capture = _captureTarget,
                update = new { version = Updater.CurrentText, status = _updateStatus, busy = _updating },
            },
        });
    }

    static string When(DateTime at) =>
        at.Date == DateTime.Today ? at.ToString("HH:mm", CultureInfo.InvariantCulture)
        : at.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : at.ToString("d MMM", CultureInfo.InvariantCulture);

    void StartMicTest()
    {
        if (_recorder is not null || _micTest is not null) return;
        try
        {
            var test = new Recorder(_settings.Microphone);
            test.Start();
            _micTest = test;
            var started = DateTime.Now;
            var tick = new System.Windows.Forms.Timer { Interval = 50 };
            tick.Tick += (_, _) =>
            {
                if (_micTest != test || DateTime.Now - started > TimeSpan.FromSeconds(5))
                {
                    tick.Dispose();
                    if (_micTest == test) StopMicTest();
                    _window?.Post(new { type = "micLevel", v = 0 });
                    return;
                }
                _window?.Post(new { type = "micLevel", v = test.Level });
            };
            tick.Start();
        }
        catch (Exception e)
        {
            Log.Write($"Microphone test failed: {e.Message}");
            _window?.Post(new { type = "toast", text = "That microphone could not be opened" });
        }
    }

    void StopMicTest()
    {
        var test = _micTest;
        _micTest = null;
        if (test is null) return;
        _ = test.StopAsync().ContinueWith(_ => test.Dispose(), TaskScheduler.Default);
    }

    // ---- Updates --------------------------------------------------------------------------------

    /// <summary>Finds and downloads a newer release; OnWatch installs it once Dikte has been idle a minute.</summary>
    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating || _staged is not null || (!manual && !_settings.AutoUpdate)) return;
        _updating = true;
        _updateStatus = "Checking…";
        PushState();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var release = await _updater.CheckAsync(cts.Token);
            if (release is null)
            {
                _updateStatus = "Up to date";
                if (manual) _window?.Post(new { type = "toast", text = "Dikte is up to date" });
                return;
            }
            var version = release.Version.ToString(3);
            _updateStatus = $"Downloading {version}…";
            PushState();
            _staged = await _updater.DownloadAsync(release, cts.Token);
            _updateStatus = $"{version} installs when you stop dictating";
            Log.Write($"Update {version} downloaded and verified.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                      or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _updateStatus = e is UnauthorizedAccessException ? "Cannot update: move Dikte.exe to a folder you can write to" : "Could not check for updates";
            Log.Write($"Update check failed: {e.Message}");
            if (manual) _window?.Post(new { type = "toast", text = _updateStatus });
        }
        finally
        {
            _updating = false;
            PushState();
        }
    }

    void InstallUpdate()
    {
        var staged = _staged;
        if (staged is null || _recorder is not null || _pending > 0) return;
        _staged = null;
        if (Updater.InstallAndRestart(staged, openWindow: _window is { Visible: true }))
        {
            ExitThread();
            return;
        }
        _updateStatus = "The update could not be installed";
        PushState();
    }

    // ---- Menu -----------------------------------------------------------------------------------

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var open = new ToolStripMenuItem("Open Dikte", null, (_, _) => OpenWindow());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(_recorder is null ? "Start hands-free dictation" : "Stop and send", null, (_, _) => ToggleDictation());
        if (_failedAudio is not null) menu.Items.Add("Retry last recording", null, (_, _) => RetryFailed());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_staged is not null ? "Restart to update" : "Check for updates", null, (_, _) =>
        {
            if (_staged is not null) InstallUpdate();
            else _ = CheckForUpdatesAsync(manual: true);
        });
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }

    void RetryFailed()
    {
        var wav = _failedAudio;
        if (wav is null) return;
        _errorMessage = _errorHint = null;
        _ = ProcessAsync(wav, TimeSpan.FromSeconds((wav.Length - 44) / (Recorder.SampleRate * 2.0)));
    }

    static void Open(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    static string Pretty(string hotkey) =>
        string.Join(" + ", hotkey.Split('+', StringSplitOptions.TrimEntries).Select(p => p.Length <= 2 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));

    protected override void ExitThreadCore()
    {
        _hook?.Dispose();
        _hook = null;
        _mouseHook?.Dispose();
        _mouseHook = null;
        _showRequests.Unregister(null);
        _watch.Stop();
        _levelTick.Stop();
        _updateTimer.Stop();
        _settingsWatcher.Dispose();
        _recorder?.Dispose();
        StopMicTest();
        _ducker.RestoreAndWait(TimeSpan.FromSeconds(2));
        _window?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        _http.Dispose();
        _updater.Dispose();
        base.ExitThreadCore();
    }
}

static class TrayIcons
{
    /// <summary>
    /// The tray icon for a state, from a multi-size .ico drawn for each size Windows uses (16 px at 100%
    /// up to 48 px), so it is never a big picture squeezed small.
    /// </summary>
    public static Icon Load(string state)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream($"tray-{state}.ico")
            ?? throw new InvalidOperationException($"Tray icon {state} is missing from the build.");
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
