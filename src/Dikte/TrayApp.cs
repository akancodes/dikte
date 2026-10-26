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

    // Meeting notes: a call app's sound and the microphone, written down while the meeting goes on.
    readonly HttpClient _meetingHttp = new() { Timeout = TimeSpan.FromMinutes(5) }; // the notes of a long call take a while
    readonly Transcriber _meetingApi;
    readonly CallWatch _calls = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
    readonly System.Windows.Forms.Timer _callTick = new() { Interval = 2000 };
    readonly Dictionary<string, int> _callPids = new(StringComparer.OrdinalIgnoreCase);
    MeetingRecorder? _meeting;
    string? _meetingPhase; // "starting", "recording" or "writing"
    (string Kind, string Value)? _balloon; // what clicking the notification does: "offer" an app's call, or show a "ready" file
    string? _showMeeting;
    bool _lookingForCalls;
    string? _callLookError;

    Settings _settings;
    HoldKeys _holds;
    ToggleKey? _toggle, _repaste, _nextMode, _meetingKey;
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
    DictationMode _mode;
    Mode? _writeMode;
    Mode? _failedMode;
    string? _lastText;
    DateTime _lastTextAt;
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
        _meetingApi = new Transcriber(_meetingHttp);
        _settings = LoadSettings(out var problem);
        _holds = NewHolds(_settings);
        _toggle = NewTap(_settings.ToggleHotkey);
        _repaste = NewTap(_settings.RepasteHotkey);
        _nextMode = NewTap(_settings.NextModeHotkey);
        _meetingKey = NewTap(_settings.MeetingHotkey);
        _history = new History(Paths.History, _settings.HistorySize);

        _idleIcon = TrayIcons.Load("idle");
        _recordingIcon = TrayIcons.Load("listening");
        _workingIcon = TrayIcons.Load("working");

        _tray = new NotifyIcon { Icon = _idleIcon, Visible = true, Text = "Dikte" };
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };
        _tray.BalloonTipClicked += (_, _) => OnBalloonClicked();
        BuildMenu(_tray.ContextMenuStrip);

        _hook = new KeyboardHook(OnKey);
        UpdateMouseHook();
        _watch.Tick += (_, _) => OnWatch();
        _watch.Start();
        _levelTick.Tick += (_, _) => OnLevelTick();
        _callTick.Tick += (_, _) => LookForCalls();
        _callTick.Start();

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
            var defaults = new Settings();
            defaults.Normalize();
            return defaults;
        }
    }

    static HoldKeys NewHolds(Settings s)
    {
        KeyNames.TryParse(s.HandsFreeKey, out var lockVk);
        var keys = new List<(HoldTarget, HotkeySpec)> { (DictationMode.Dictate, HotkeySpec.Parse(s.Hotkey)) };
        if (!string.IsNullOrWhiteSpace(s.TranslateHotkey)) keys.Add((DictationMode.Translate, HotkeySpec.Parse(s.TranslateHotkey)));
        if (!string.IsNullOrWhiteSpace(s.EditHotkey)) keys.Add((DictationMode.Edit, HotkeySpec.Parse(s.EditHotkey)));
        foreach (var m in s.Modes.Where(m => m.Hotkey.Length > 0))
            keys.Add((new HoldTarget(DictationMode.Dictate, m.Id), HotkeySpec.Parse(m.Hotkey)));
        return new HoldKeys(keys, lockVk);
    }

    /// <summary>Every hold shortcut in one string, to tell whether the set changed.</summary>
    static string HoldShortcuts(Settings s) =>
        string.Join("|", new[] { s.Hotkey, s.HandsFreeKey, s.TranslateHotkey, s.EditHotkey }.Concat(s.Modes.Select(m => m.Id + "=" + m.Hotkey)));

    static ToggleKey? NewTap(string shortcut) =>
        string.IsNullOrWhiteSpace(shortcut) ? null : new ToggleKey(HotkeySpec.Parse(shortcut));

    static IEnumerable<string> Shortcuts(Settings s) =>
        new[] { s.Hotkey, s.ToggleHotkey, s.TranslateHotkey, s.EditHotkey, s.RepasteHotkey, s.NextModeHotkey, s.MeetingHotkey }
            .Concat(s.Modes.Select(m => m.Hotkey)).Where(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>The mouse hook sees every mouse move, so it runs only while a shortcut (or a capture) needs it.</summary>
    void UpdateMouseHook()
    {
        bool needed = _capture is not null || Shortcuts(_settings).Any(x => HotkeySpec.Parse(x).UsesMouse);
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
        bool hotkeyChanged = HoldShortcuts(fresh) != HoldShortcuts(_settings);
        bool toggleChanged = fresh.ToggleHotkey != _settings.ToggleHotkey;
        bool repasteChanged = fresh.RepasteHotkey != _settings.RepasteHotkey;
        bool nextModeChanged = fresh.NextModeHotkey != _settings.NextModeHotkey;
        bool meetingKeyChanged = fresh.MeetingHotkey != _settings.MeetingHotkey;
        bool updatesTurnedOn = fresh.AutoUpdate && !_settings.AutoUpdate;
        _settings = fresh;
        if (updatesTurnedOn) _ = CheckForUpdatesAsync(manual: false);
        if (hotkeyChanged && _recorder is null) _holds = NewHolds(fresh);
        if (toggleChanged) _toggle = NewTap(fresh.ToggleHotkey);
        if (repasteChanged) _repaste = NewTap(fresh.RepasteHotkey);
        if (nextModeChanged) _nextMode = NewTap(fresh.NextModeHotkey);
        if (meetingKeyChanged) _meetingKey = NewTap(fresh.MeetingHotkey);
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
    void SaveFromWindow(JsonElement patch, bool quiet = false)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(_settings, Settings.Json)!.AsObject();
            foreach (var prop in patch.EnumerateObject()) node[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            var fresh = node.Deserialize<Settings>(Settings.Json)!;
            fresh.Normalize();
            fresh.Validate();
            _lastReload = DateTime.Now; // our own write: no "reloaded" toast
            fresh.Save(Paths.Settings);
            Apply(fresh);
            if (!quiet) _window?.Post(new { type = "toast", text = "Saved" });
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

        if (_repaste is not null)
        {
            var r = _repaste.Feed(vk, isDown);
            if (r.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
            if (r.Command == HotkeyCommand.Toggle) _ui.Post(_ => _ = RepasteAsync(), null);
            if (r.Swallow) return true;
        }

        if (_nextMode is not null)
        {
            var n = _nextMode.Feed(vk, isDown);
            if (n.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
            if (n.Command == HotkeyCommand.Toggle) _ui.Post(_ => SetActiveMode(Modes.Next(_settings), announce: true), null);
            if (n.Swallow) return true;
        }

        if (_meetingKey is not null)
        {
            var k = _meetingKey.Feed(vk, isDown);
            if (k.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
            if (k.Command == HotkeyCommand.Toggle) _ui.Post(_ => ToggleMeeting(), null);
            if (k.Swallow) return true;
        }

        if (_toggle is not null)
        {
            var t = _toggle.Feed(vk, isDown);
            if (t.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
            if (t.Command == HotkeyCommand.Toggle) _ui.Post(_ => ToggleDictation(), null);
            if (t.Swallow) return true;
        }

        // Esc while a transcript is on its way cancels it, and must not reach the app (it would interrupt Claude Code).
        if (vk == HotkeyMachine.VkEscape && isDown && !_holds.IsRecording && _pending > 0)
        {
            _ui.Post(_ => CancelPending(), null);
            return true;
        }

        var result = _holds.Feed(vk, isDown);
        if (result.SuppressMenu) Native.Send(Native.Key(Native.VK_MENU_MASK, false), Native.Key(Native.VK_MENU_MASK, true));
        foreach (var (command, target) in result.Commands)
            _ui.Post(_ => Run(command, target), null); // never do slow work inside the hook
        return result.Swallow;
    }

    void OnWatch()
    {
        var command = _holds.Resync(Native.IsPhysicallyDown);
        if (command != HotkeyCommand.None) Run(command, _mode);
        _toggle?.Resync(Native.IsPhysicallyDown);
        _repaste?.Resync(Native.IsPhysicallyDown);
        _nextMode?.Resync(Native.IsPhysicallyDown);
        _meetingKey?.Resync(Native.IsPhysicallyDown);
        if (_capture is not null && DateTime.Now - _captureStarted > TimeSpan.FromSeconds(12)) FinishCapture(CaptureOutcome.Cancelled, null);
        if (_recorder is not null || _pending > 0 || _capture is not null || _micTest is not null || _meetingPhase is not null) _lastUse = DateTime.Now;
        else if (_staged is not null && DateTime.Now - _lastUse > TimeSpan.FromMinutes(1) && _window?.ContainsFocus != true) InstallUpdate();

        if (_recorder is null) return;
        var elapsed = DateTime.Now - _recordingStarted;
        // Show, sound and quieten only once a hold is clearly meant, so Ctrl+Win+Arrow stays silent.
        if (!_feedbackShown && elapsed > TimeSpan.FromMilliseconds(180))
        {
            _feedbackShown = true;
            var recorder = _recorder;
            ShowListening(recorder);
            if (_holds.IsLocked) _overlay.ShowHandsFree();
            if (_settings.Sounds) Sounds.Start();
            if (_settings.QuietOtherAudio) _ducker.Duck(_settings.QuietLevel, _meeting?.App);
            PushState();
        }
        if (_recorder.Duration.TotalMinutes >= _settings.MaxMinutes)
        {
            _holds.Reset();
            _ = StopAndSendAsync();
        }
    }

    void ShowListening(Recorder recorder)
    {
        switch (_mode)
        {
            case DictationMode.Translate:
                _overlay.ShowListening(() => recorder.Level, $"Listening · writing {_settings.TranslateTo}");
                break;
            case DictationMode.Edit:
                _overlay.ShowListening(() => recorder.Level, "Say what to change", "Release to apply to the selection · Esc");
                break;
            default:
                if (_writeMode is { } m) _overlay.ShowListening(() => recorder.Level, $"Listening · {m.Name}");
                else _overlay.ShowListening(() => recorder.Level);
                break;
        }
    }

    void OnLevelTick()
    {
        if (_window is not { Visible: true }) return;
        if (_recorder is not null && _feedbackShown) _window.Post(new { type = "level", v = _recorder.Level });
        if (_meeting is { } m && _meetingPhase == "recording") _window.Post(new { type = "meetingLevel", me = m.MyLevel, them = m.TheirLevel });
    }

    void Run(HotkeyCommand command, HoldTarget target)
    {
        switch (command)
        {
            case HotkeyCommand.Start: StartRecording(target); break;
            case HotkeyCommand.Lock:
                if (_recorder is not null)
                {
                    var recorder = _recorder;
                    ShowListening(recorder);
                    _overlay.ShowHandsFree();
                    if (!_feedbackShown && _settings.QuietOtherAudio) _ducker.Duck(_settings.QuietLevel, _meeting?.App);
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
            _holds.EnterLocked();
            StartRecording(DictationMode.Dictate);
        }
        else
        {
            _holds.Reset();
            _ = StopAndSendAsync();
        }
    }

    // ---- Recording ------------------------------------------------------------------------------

    void StartRecording(HoldTarget target)
    {
        if (_recorder is not null) return;
        StopMicTest();
        try
        {
            _mode = target.Kind;
            // Picked now, while the app being typed into is still the one in front.
            _writeMode = target.Kind == DictationMode.Dictate ? Modes.Resolve(_settings, target.ModeId, Native.ForegroundApp()) : null;
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
            _holds.Reset();
            Log.Write($"Microphone failed: {e}");
            Fail("Microphone not available", e.Message);
        }
    }

    void EndRecording()
    {
        if (_meeting is null) _levelTick.Stop();
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
        var mode = _mode;
        var writeMode = _writeMode;
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
        await ProcessAsync(wav, length, mode, writeMode);
    }

    // ---- Transcribing ---------------------------------------------------------------------------

    async Task ProcessAsync(byte[] wav, TimeSpan length, DictationMode mode = DictationMode.Dictate, Mode? writeMode = null)
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
        if (_recorder is null)
            _overlay.ShowWorking(mode switch { DictationMode.Translate => "Translating", DictationMode.Edit => "Editing", _ => writeMode is null ? "Transcribing" : $"Writing · {writeMode.Name}" });
        PushState();
        var clock = Stopwatch.StartNew();
        try
        {
            bool ownWindow = _window is not null && Native.GetForegroundWindow() == _window.Handle;
            // Copied first, while the selection is surely still the one the user meant.
            string? selection = mode == DictationMode.Edit && !ownWindow ? await Inserter.CopySelectionAsync() : null;

            var heard = await _api.TranscribeAsync(wav, settings, key, ct);
            var transcribeMs = clock.ElapsedMilliseconds;
            var spoken = mode != DictationMode.Edit && settings.VoiceCommands ? VoiceCommands.Apply(heard) : new Spoken(heard, false, false);
            var text = spoken.Text;
            if (text.Trim().Length > 0)
            {
                switch (mode)
                {
                    case DictationMode.Edit:
                        text = await _api.EditAsync(selection ?? "", text, settings, key, ct);
                        break;
                    case DictationMode.Translate:
                        try { text = await _api.TranslateAsync(text, settings, key, ct); }
                        catch (ApiException e) { Log.Write($"Translation skipped, writing the words as spoken: {e.Message}"); }
                        break;
                    default:
                        if (writeMode is not null)
                        {
                            try { text = await _api.RewriteAsync(text, writeMode, settings, key, ct); }
                            catch (ApiException e) { Log.Write($"{writeMode.Name} skipped, writing the words as heard: {e.Message}"); }
                        }
                        break;
                }
            }
            await previous;
            Log.Write($"{(writeMode is null ? mode.ToString() : $"{mode} ({writeMode.Id})")} {length.TotalSeconds:0.0}s: transcribed in {transcribeMs} ms (total {clock.ElapsedMilliseconds} ms), {text.Length} chars" +
                      (spoken.PressEnter ? ", then Enter" : "") + (spoken.DeleteLast ? ", delete last" : "") +
                      (mode == DictationMode.Edit ? $", selection {selection?.Length ?? 0} chars" : ""));

            if (spoken.DeleteLast)
            {
                await DeleteLastAsync();
                return;
            }
            if (text.Trim().Length == 0 && !spoken.PressEnter)
            {
                if (_recorder is null) _overlay.ShowMessage("Nothing to write", "No words were heard", 2);
                return;
            }
            int words = CountWords(text);
            if (words > 0)
            {
                _history.Add(text);
                _stats.Record(DateTime.Now, words, length.TotalSeconds, settings.Model);
            }
            _failedAudio = null;
            _failedMode = null;

            if (ownWindow)
            {
                // Dictated with Dikte's own window in front: show it there instead of pasting into the page.
                if (words > 0) _window!.Post(new { type = "result", text });
                _overlay.HideNow();
            }
            else
            {
                if (words > 0)
                {
                    // No trailing space before Enter, nor after an edit (it replaces exactly what was selected).
                    if (settings.AppendSpace && !spoken.PressEnter && mode != DictationMode.Edit && !char.IsWhiteSpace(text[^1])) text += " ";
                    await Inserter.InsertAsync(text, settings);
                    _lastText = text;
                    _lastTextAt = DateTime.Now;
                }
                if (spoken.PressEnter) await Inserter.PressEnterAsync();
                if (_recorder is null)
                    _overlay.ShowDone(words == 0 ? "Sent"
                        : mode == DictationMode.Edit ? (string.IsNullOrEmpty(selection) ? "Written" : "Edited")
                        : spoken.PressEnter ? $"{words} words · sent" : $"{words} words");
            }
        }
        catch (OperationCanceledException)
        {
            if (_recorder is null) _overlay.ShowMessage("Cancelled", "", 1.2);
        }
        catch (ApiException e)
        {
            _failedAudio = wav;
            _failedMode = writeMode;
            try { File.WriteAllBytes(Paths.FailedAudio, wav); } catch (IOException) { }
            Log.Write($"Transcription failed: {e.Message}");
            Fail(e.Message, "The recording is kept: Retry from the tray menu");
        }
        catch (Exception e)
        {
            _failedAudio = wav;
            _failedMode = writeMode;
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

    /// <summary>"delete that": takes the last dictation back out, if it went in a few minutes ago.</summary>
    async Task DeleteLastAsync()
    {
        var last = _lastText;
        if (last is null || DateTime.Now - _lastTextAt > TimeSpan.FromMinutes(10))
        {
            if (_recorder is null) _overlay.ShowMessage("Nothing to delete", "Only the last dictation can be taken back", 2);
            return;
        }
        _lastText = null;
        await Inserter.DeleteBackAsync(last.Length);
        if (_recorder is null) _overlay.ShowDone("Deleted");
    }

    /// <summary>The paste-again shortcut: the last dictation, into whatever is in front now.</summary>
    async Task RepasteAsync()
    {
        var text = _history.Items.Count > 0 ? _history.Items[0].Text : null;
        if (text is null)
        {
            _overlay.ShowMessage("Nothing to paste yet", "", 2);
            return;
        }
        if (_settings.AppendSpace && !char.IsWhiteSpace(text[^1])) text += " ";
        await Inserter.InsertAsync(text, _settings);
        _lastText = text;
        _lastTextAt = DateTime.Now;
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

    void UpdateIcon()
    {
        // Red while anything is recording, a meeting included: it is listening to the room.
        _tray.Icon = _recorder is not null || _meetingPhase is "starting" or "recording" ? _recordingIcon
            : _pending > 0 || _meetingPhase == "writing" ? _workingIcon : _idleIcon;
        _tray.Text = _meetingPhase switch
        {
            "recording" => "Dikte · taking meeting notes",
            "writing" => "Dikte · writing meeting notes",
            _ => "Dikte",
        };
    }

    static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- Picking a shortcut ---------------------------------------------------------------------

    void StartCapture(string target)
    {
        bool known = target is "hotkey" or "toggleHotkey" or "translateHotkey" or "editHotkey" or "repasteHotkey" or "nextModeHotkey" or "meetingHotkey"
            || (target.StartsWith("mode:", StringComparison.Ordinal) && _settings.Modes.Any(m => m.Id == target[5..]));
        if (_recorder is not null || !known) return;
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
            SaveFromWindow(target.StartsWith("mode:", StringComparison.Ordinal)
                ? ModesPatch(_settings, target[5..], m => m.Hotkey = result)
                : JsonSerializer.SerializeToElement(new Dictionary<string, string> { [target] = result }));
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
            case "ready":
                PushState();
                if (_showMeeting is { } show && _window?.Post(new { type = "showMeeting", file = Path.GetFileName(show) }) == true) _showMeeting = null;
                break;
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
            case "setMode": SetActiveMode(m.GetProperty("id").GetString() ?? Modes.Plain, announce: false); break;
            case "listApps": _window?.Post(new { type = "apps", apps = OpenApps() }); break;
            case "startMeeting": StartMeeting(); break;
            case "stopMeeting": _ = StopMeetingAsync(discard: false); break;
            case "discardMeeting": _ = StopMeetingAsync(discard: true); break;
            case "meetingWith": if (_meeting is not null) _meeting.With = m.GetProperty("with").GetString() ?? ""; break;
            case "listMeetings": PostMeetings(); break;
            case "openMeeting": PostMeeting(m.GetProperty("file").GetString() ?? ""); break;
            case "rewriteMeeting": _ = RewriteMeetingAsync(m.GetProperty("file").GetString() ?? ""); break;
            case "deleteMeeting": DeleteMeeting(m.GetProperty("file").GetString() ?? ""); break;
            case "revealMeeting": RevealMeeting(m.TryGetProperty("file", out var file) ? file.GetString() : null); break;
            case "pickMeetingsFolder": PickMeetingsFolder(); break;
        }
    }

    // ---- Modes ----------------------------------------------------------------------------------

    /// <summary>The menu, the window and the next-mode shortcut all switch the active mode through here.</summary>
    void SetActiveMode(string id, bool announce)
    {
        if (id != Modes.Plain && _settings.Modes.All(m => m.Id != id)) return;
        if (id != _settings.ActiveMode)
        {
            SaveFromWindow(JsonSerializer.SerializeToElement(new { activeMode = id }, Settings.Json), quiet: true);
            Log.Write($"Active mode: {id}");
        }
        if (announce && _recorder is null)
            _overlay.ShowMessage($"Mode: {Modes.NameOf(_settings, id)}", ActiveModeHint(), 1.8);
    }

    /// <summary>Says when an app rule will still pick another mode, so a switch that seems ignored is explained.</summary>
    string ActiveModeHint()
    {
        var app = Native.ForegroundApp();
        var byApp = app is null ? null : _settings.Modes.FirstOrDefault(m => m.Apps.Any(a => Modes.Normalize(a) == Modes.Normalize(app)));
        return byApp is null ? "" : $"{byApp.Name} is still used in {app}";
    }

    /// <summary>A save patch that changes one mode and keeps the rest of the list.</summary>
    static JsonElement ModesPatch(Settings s, string id, Action<Mode> change)
    {
        var modes = JsonSerializer.Deserialize<List<Mode>>(JsonSerializer.Serialize(s.Modes, Settings.Json), Settings.Json)!;
        var mode = modes.FirstOrDefault(m => m.Id == id);
        if (mode is not null) change(mode);
        return JsonSerializer.SerializeToElement(new { modes }, Settings.Json);
    }

    /// <summary>Apps with a window, by process name, for picking which ones a mode is used in.</summary>
    static IReadOnlyList<string> OpenApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.ProcessName is not ("Dikte" or "ApplicationFrameHost" or "TextInputHost" or "SystemSettings"))
                        names.Add(p.ProcessName);
                }
                catch (InvalidOperationException) { }
            }
        }
        return names.ToList();
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
            _recorder is not null && _feedbackShown ? (_holds.IsLocked ? "handsfree" : "listening")
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
                codingApps = Modes.CodingApps,
                update = new { version = Updater.CurrentText, status = _updateStatus, busy = _updating },
                meeting = MeetingState(),
                call = _calls.InCall.FirstOrDefault(),
                meetingsFolder = Paths.Meetings(_settings),
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

    // ---- Meeting notes --------------------------------------------------------------------------

    /// <summary>Every two seconds: which call apps hold a microphone, so a call can be offered, shown and ended.</summary>
    void LookForCalls()
    {
        if (_lookingForCalls || _settings.MeetingApps.Count == 0) return;
        _lookingForCalls = true;
        var apps = _settings.MeetingApps.ToList();
        Task.Run(() =>
        {
            try { return (Found: Calls.UsingMicrophone(out var problem), Error: problem); }
            catch (Exception e) { return (Found: new List<(string Name, int Pid)>(), Error: (string?)e.Message); }
        }).ContinueWith(t => _ui.Post(_ =>
        {
            _lookingForCalls = false;
            var (found, error) = t.Result;
            // Logged once, not every two seconds.
            if (error != _callLookError && error is not null) Log.Write($"Looking for calls failed: {error}");
            _callLookError = error;
            // A microphone that could not be read may be the one the call is on: its calls are not taken as over.
            var inCall = error is null ? new List<string>() : _calls.InCall.ToList();
            foreach (var (name, pid) in found)
            {
                if (apps.FirstOrDefault(a => MeetingNotes.IsApp(name, a)) is not { } app) continue;
                inCall.Add(app);
                _callPids[app] = pid;
            }
            foreach (var (app, started) in _calls.Observe(inCall, DateTime.Now))
            {
                if (started) OnCallStarted(app);
                else OnCallEnded(app);
            }
        }, null), TaskScheduler.Default);
    }

    void OnCallStarted(string app)
    {
        Log.Write($"{app} call noticed.");
        if (_meetingPhase is null && _settings.MeetingOffer && KeyStore.Get() is not null)
        {
            _balloon = ("offer", app);
            _tray.ShowBalloonTip(20_000, $"{app} call", "Click to take meeting notes", ToolTipIcon.None);
        }
        PushState();
    }

    void OnCallEnded(string app)
    {
        Log.Write($"{app} call over.");
        if (_balloon is ("offer", var offered) && offered == app) _balloon = null;
        // The call these notes were for is over: write them.
        if (_meeting?.App == app && _meetingPhase == "recording")
        {
            if (_recorder is null) _overlay.ShowMessage($"{app} call ended", "Writing the meeting notes", 3);
            _ = StopMeetingAsync(discard: false);
        }
        PushState();
    }

    void OnBalloonClicked()
    {
        var balloon = _balloon;
        _balloon = null;
        switch (balloon)
        {
            case ("offer", var app): StartMeeting(app); break;
            case ("ready", var file): ShowMeeting(file); break;
        }
    }

    void ToggleMeeting()
    {
        if (_meetingPhase == "recording") _ = StopMeetingAsync(discard: false);
        else if (_meetingPhase is null) StartMeeting();
    }

    /// <summary>Starts notes for <paramref name="app"/>; with none, for the app in a call, else a call app that is open, else the microphone alone.</summary>
    async void StartMeeting(string? app = null)
    {
        if (_meetingPhase is not null) return;
        if (KeyStore.Get() is null)
        {
            _overlay.ShowMessage("Connect your OpenAI key first", "Meeting notes need it to write anything down", 4);
            OpenWindow();
            return;
        }
        _balloon = null;
        _meetingPhase = "starting";
        UpdateIcon();
        PushState();
        try
        {
            app ??= _calls.InCall.FirstOrDefault() ?? _settings.MeetingApps.FirstOrDefault(a => Calls.RootProcess(a, null) is not null) ?? "";
            int? pid = null;
            if (app.Length > 0)
                pid = Calls.RootProcess(app, _calls.InCall.Contains(app) && _callPids.TryGetValue(app, out var callPid) ? callPid : null);
            bool inCallAtStart = _calls.InCall.Contains(app);
            var folder = Paths.Meetings(_settings);
            var with = await Task.Run(() => LastWith(folder, app));
            var meeting = await MeetingRecorder.StartAsync(app, pid, with, folder, _api, () => _settings);
            meeting.Changed += () => _ui.Post(_ => PushState(), null);
            _meeting = meeting;
            _meetingPhase = "recording";
            _levelTick.Start();
            if (_settings.Sounds) Sounds.Start();
            if (_recorder is null)
                _overlay.ShowMessage("Taking meeting notes", meeting.HearsApp ? $"{app} and your microphone" : meeting.Problem ?? "Your microphone", 3);
            // The call ended while the app's sound was being opened.
            if (inCallAtStart && !_calls.InCall.Contains(app)) _ = StopMeetingAsync(discard: false);
        }
        catch (Exception e)
        {
            _meetingPhase = null;
            Log.Write($"Meeting notes did not start: {e}");
            Fail("Meeting notes could not start", e.Message);
        }
        UpdateIcon();
        PushState();
    }

    /// <summary>Stops listening and writes the notes, or with <paramref name="discard"/> throws the meeting away.</summary>
    async Task StopMeetingAsync(bool discard)
    {
        var m = _meeting;
        if (m is null || _meetingPhase != "recording") return;
        _meetingPhase = "writing";
        if (_recorder is null) _levelTick.Stop();
        UpdateIcon();
        PushState();
        if (!discard && _recorder is null) _overlay.ShowMessage("Writing the meeting notes", "They open when they are ready", 60);
        string? written = null;
        try
        {
            var doc = await m.StopAsync(discard);
            if (doc is not null)
            {
                _stats.RecordSpend(DateTime.Now, m.SentSeconds, _settings.Model);
                written = m.FilePath; // until the notes give it its title
                written = await MeetingWriter.WriteAsync(m.FilePath, doc, _meetingApi, _settings, CancellationToken.None);
                Log.Write($"Meeting notes written ({doc.Lines.Count} lines).");
                if (_settings.Sounds) Sounds.Stop();
                if (_recorder is null) _overlay.ShowDone("Meeting notes ready");
                if (_window is { Visible: true }) ShowMeeting(written);
                else
                {
                    _balloon = ("ready", written);
                    _tray.ShowBalloonTip(20_000, "Meeting notes ready", $"{doc.Title} · click to read them", ToolTipIcon.None);
                }
            }
            else if (_recorder is null) _overlay.ShowMessage("Meeting notes discarded", "", 1.5);
        }
        catch (Exception e)
        {
            Log.Write($"Meeting notes not written: {e}");
            Fail("Meeting notes not written", $"{e.Message} The transcript is saved: Meetings → Write the notes again.");
        }
        finally
        {
            m.Dispose();
            _meeting = null;
            _meetingPhase = null;
            if (_recorder is null) _levelTick.Stop();
            UpdateIcon();
            PushState();
            // The file the page may be showing got its title for a name, or is gone.
            _window?.Post(new { type = "meetingsChanged", from = Path.GetFileName(m.FilePath), to = written is null ? null : Path.GetFileName(written) });
        }
    }

    object MeetingState()
    {
        var m = _meeting;
        var me = _settings.YourName.Length > 0 ? _settings.YourName : "Me";
        return new
        {
            phase = _meetingPhase ?? "idle",
            app = m?.App ?? "",
            with = m?.With ?? "",
            seconds = m is null ? 0 : (int)m.Elapsed.TotalSeconds,
            hearsApp = m?.HearsApp ?? false,
            problem = m?.Problem,
            lines = m is null ? Array.Empty<object>()
                : m.Recent(6).Select(l => (object)new { t = MeetingFile.Clock(l.At), who = l.Speaker, text = l.Text, mine = l.Speaker == me }).ToArray(),
        };
    }

    /// <summary>Who was on the last call in this app, to fill in the next one.</summary>
    static string LastWith(string folder, string app)
    {
        if (app.Length == 0) return "";
        try { return MeetingWriter.List(folder).Select(f => f.Doc).FirstOrDefault(d => d.App == app && d.With.Length > 0)?.With ?? ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>A file name from the page, as a path inside the notes folder; null when it is not a meeting there.</summary>
    string? MeetingPath(string? file)
    {
        var name = Path.GetFileName(file ?? "");
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(Paths.Meetings(_settings), name);
        return File.Exists(path) ? path : null;
    }

    async void PostMeetings()
    {
        var folder = Paths.Meetings(_settings);
        List<(string File, MeetingDoc Doc)> list;
        // Off the UI thread: the folder may be a synced one that fetches files as they are read.
        try { list = await Task.Run(() => MeetingWriter.List(folder)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { list = new(); }
        _window?.Post(new
        {
            type = "meetings",
            items = list.Select(x => new
            {
                file = Path.GetFileName(x.File),
                title = x.Doc.Title,
                when = WhenMeeting(x.Doc.Started),
                minutes = (int)Math.Round(x.Doc.Minutes),
                app = x.Doc.App,
                with = x.Doc.With,
                status = x.Doc.Status,
                pending = x.Doc.Pending,
                summary = FirstSentence(x.Doc.Notes),
                recording = _meeting is not null && string.Equals(x.File, _meeting.FilePath, StringComparison.OrdinalIgnoreCase),
            }),
        });
    }

    void PostMeeting(string file)
    {
        var path = MeetingPath(file);
        MeetingDoc? doc = null;
        try { if (path is not null) doc = MeetingFile.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (doc is null)
        {
            _window?.Post(new { type = "meetingGone", file = Path.GetFileName(file) });
            PostMeetings();
            return;
        }
        _window?.Post(new
        {
            type = "meeting",
            file = Path.GetFileName(path),
            title = doc.Title,
            when = WhenMeeting(doc.Started),
            minutes = (int)Math.Round(doc.Minutes),
            app = doc.App,
            with = doc.With,
            status = doc.Status,
            pending = doc.Pending,
            notes = doc.Notes,
            lines = doc.Lines.Select(l => new { t = MeetingFile.Clock(l.At), who = l.Speaker, text = l.Text, mine = l.Speaker == doc.Me }),
        });
    }

    void ShowMeeting(string path)
    {
        _showMeeting = path;
        OpenWindow();
        if (_window?.Post(new { type = "showMeeting", file = Path.GetFileName(path) }) == true) _showMeeting = null;
    }

    async Task RewriteMeetingAsync(string file)
    {
        var path = MeetingPath(file);
        if (path is null || (_meeting is not null && string.Equals(path, _meeting.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            // Always answered, so the page's button does not wait forever.
            PostMeeting(file);
            return;
        }
        try
        {
            var written = await MeetingWriter.RewriteAsync(path, _api, _meetingApi, _settings, CancellationToken.None);
            PostMeeting(Path.GetFileName(written));
            _window?.Post(new { type = "toast", text = "Notes written" });
        }
        catch (Exception e) when (e is ApiException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            Log.Write($"Writing the notes again failed: {e.Message}");
            _window?.Post(new { type = "toast", text = e.Message });
            PostMeeting(file);
        }
        PostMeetings();
    }

    void DeleteMeeting(string file)
    {
        var path = MeetingPath(file);
        if (path is null || (_meeting is not null && string.Equals(path, _meeting.FilePath, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            // To the Recycle Bin: a meeting deleted by mistake can be put back.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            var pending = MeetingWriter.PendingDir(path);
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            _window?.Post(new { type = "toast", text = "Moved to the Recycle Bin" });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _window?.Post(new { type = "toast", text = e.Message });
        }
        PostMeetings();
    }

    void RevealMeeting(string? file)
    {
        var folder = Paths.Meetings(_settings);
        try
        {
            Directory.CreateDirectory(folder);
            var path = MeetingPath(file);
            Process.Start(new ProcessStartInfo("explorer.exe", path is null ? $"\"{folder}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _window?.Post(new { type = "toast", text = e.Message });
        }
    }

    void PickMeetingsFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where Dikte saves meeting notes",
            UseDescriptionForTitle = true,
            InitialDirectory = Paths.Meetings(_settings),
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(_window) == DialogResult.OK)
        {
            SaveFromWindow(JsonSerializer.SerializeToElement(new { meetingsFolder = dialog.SelectedPath }, Settings.Json));
            PostMeetings();
        }
    }

    static string WhenMeeting(DateTime at) =>
        (at.Date == DateTime.Today ? "Today" : at.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : at.ToString("ddd d MMM", CultureInfo.InvariantCulture))
        + at.ToString(" · HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The notes' opening sentence, for the list: the summary starts them.</summary>
    static string FirstSentence(string notes)
    {
        var first = notes.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('-') && !l.StartsWith('_')) ?? "";
        int dot = first.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 20) first = first[..(dot + 1)];
        return first.Length > 180 ? first[..180].TrimEnd() + "…" : first;
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
        if (staged is null || _recorder is not null || _pending > 0 || _meetingPhase is not null) return;
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
        if (_meetingPhase is null)
            menu.Items.Add(_calls.InCall.FirstOrDefault() is { } call ? $"Take meeting notes ({call} call)" : "Take meeting notes", null, (_, _) => StartMeeting());
        else if (_meetingPhase == "recording")
            menu.Items.Add("Stop and write meeting notes", null, (_, _) => _ = StopMeetingAsync(discard: false));
        var modes = new ToolStripMenuItem($"Mode: {Modes.NameOf(_settings, _settings.ActiveMode)}");
        foreach (var (id, name) in new[] { (Modes.Plain, Modes.NameOf(_settings, Modes.Plain)) }.Concat(_settings.Modes.Select(m => (m.Id, m.Name))))
        {
            var item = new ToolStripMenuItem(name, null, (_, _) => SetActiveMode(id, announce: false)) { Checked = id == _settings.ActiveMode };
            modes.DropDownItems.Add(item);
        }
        menu.Items.Add(modes);
        if (_failedAudio is not null) menu.Items.Add("Retry last recording", null, (_, _) => RetryFailed());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_staged is not null ? "Restart to update" : "Check for updates", null, (_, _) =>
        {
            if (_staged is not null) InstallUpdate();
            else _ = CheckForUpdatesAsync(manual: true);
        });
        menu.Items.Add("Quit", null, (_, _) =>
        {
            if (_meetingPhase is not null && MessageBox.Show(
                    "Dikte is taking meeting notes. Quit anyway?\n\nWhat was said so far is kept, and the notes can be written later from the Meetings tab.",
                    "Dikte", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            ExitThread();
        });
    }

    void RetryFailed()
    {
        var wav = _failedAudio;
        if (wav is null) return;
        _errorMessage = _errorHint = null;
        _ = ProcessAsync(wav, TimeSpan.FromSeconds((wav.Length - 44) / (Recorder.SampleRate * 2.0)), DictationMode.Dictate, _failedMode);
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
        _callTick.Stop();
        _settingsWatcher.Dispose();
        // A meeting still going keeps what was said on disk; its notes can be written from the Meetings tab.
        _meeting?.Abandon();
        _meeting?.Dispose();
        _recorder?.Dispose();
        StopMicTest();
        _ducker.RestoreAndWait(TimeSpan.FromSeconds(2));
        _window?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        _http.Dispose();
        _meetingHttp.Dispose();
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
