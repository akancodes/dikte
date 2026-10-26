using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Dikte.Core;
using NAudio.Wave;

namespace Dikte;

/// <summary>
/// One meeting being recorded: your microphone and the call app's sound as two streams, each cut at its pauses
/// and transcribed while the meeting goes on, so the notes can be written moments after it ends. Which stream a
/// line came from says who spoke. The transcript so far is saved every few seconds. Audio stays in memory; a piece
/// the API refuses goes to the pending folder, where "Write the notes again" picks it up.
/// </summary>
sealed class MeetingRecorder : IDisposable
{
    readonly Transcriber _api;
    readonly Func<Settings> _settings;
    readonly WaveInEvent _mic;
    ProcessAudio? _app;
    readonly Segmenter _mine = new(), _theirs = new();
    readonly long _origin = Stopwatch.GetTimestamp();
    readonly Channel<(Utterance Piece, bool Mine)> _queue = Channel.CreateUnbounded<(Utterance, bool)>();
    readonly Task[] _workers;
    readonly CancellationTokenSource _cancel = new();
    readonly TaskCompletionSource _micStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly System.Threading.Timer _saver;
    readonly object _gate = new();
    readonly List<MeetingLine> _lines = new();
    double _sentSeconds, _theirsAt = -1;
    double _micAnchor;
    long _micSamples = -1;
    int _kept;
    bool _dirty;
    volatile bool _finishing, _discarding;
    volatile string? _halted; // why nothing more is sent to the API: the rest goes straight to the pending folder

    /// <summary>The call app ("Discord"), or empty when only the microphone listens (a meeting in the room).</summary>
    public string App { get; }
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>Who is on the call, as typed in the window: names the other side in the transcript and the notes.</summary>
    public string With { get; set; }
    /// <summary>The notes file, written while recording.</summary>
    public string FilePath { get; }
    /// <summary>Something that does not stop the meeting but leaves a side unheard or untranscribed.</summary>
    public string? Problem { get; private set; }
    /// <summary>A line was added, or a problem appeared. Raised on a worker thread.</summary>
    public event Action? Changed;

    MeetingRecorder(string app, string with, string folder, Transcriber api, Func<Settings> settings)
    {
        App = app;
        With = with;
        _api = api;
        _settings = settings;
        Directory.CreateDirectory(folder);
        FilePath = MeetingWriter.FreePath(folder, MeetingFile.FileName(Started, app.Length > 0 ? $"{app} call" : "Meeting"));
        _mic = new WaveInEvent
        {
            DeviceNumber = Recorder.FindDevice(settings().Microphone),
            WaveFormat = new WaveFormat(Segmenter.SampleRate, 16, 1),
            BufferMilliseconds = 100,
            NumberOfBuffers = 4,
        };
        _mic.DataAvailable += (_, e) => OnMic(e.Buffer.AsSpan(0, e.BytesRecorded));
        _mic.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && !_finishing) Report($"The microphone stopped: {e.Exception.Message}");
            _micStopped.TrySetResult();
        };
        _workers = Enumerable.Range(0, 2).Select(_ => Task.Run(WorkAsync)).ToArray();
        _saver = new System.Threading.Timer(_ => SaveIfChanged(), null, 10_000, 10_000);
    }

    /// <summary>Starts listening to the microphone and, when <paramref name="processId"/> is set, to that app's sound.</summary>
    public static async Task<MeetingRecorder> StartAsync(string app, int? processId, string with, string folder, Transcriber api, Func<Settings> settings)
    {
        var m = new MeetingRecorder(app, with, folder, api, settings);
        try
        {
            m._mic.StartRecording();
        }
        catch (Exception e)
        {
            m.Dispose();
            throw new InvalidOperationException($"The microphone could not be opened: {e.Message}", e);
        }
        if (processId is { } pid)
        {
            try
            {
                var audio = await ProcessAudio.StartAsync(pid, m._origin);
                audio.Data += (pcm, at) =>
                {
                    m._theirsAt = m.Elapsed.TotalSeconds;
                    foreach (var piece in m._theirs.Feed(pcm, at)) m.Enqueue(piece, mine: false);
                };
                audio.Failed += reason => m.Report($"{app}'s sound stopped: {reason}");
                m._app = audio;
            }
            catch (Exception e)
            {
                Log.Write($"App audio for {app} ({pid}) failed: {e}");
                m.Problem = $"{app}'s sound cannot be recorded ({e.Message}); only your microphone is heard.";
            }
        }
        else if (app.Length > 0) m.Problem = $"{app} is not running; only your microphone is heard.";
        m.Save("recording");
        Log.Write($"Meeting notes started: {(app.Length > 0 ? app : "microphone only")}{(m._app is null ? "" : " + app audio")}");
        return m;
    }

    public TimeSpan Elapsed => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _origin) / (double)Stopwatch.Frequency);

    public float MyLevel => _mine.Level;

    /// <summary>The app sends nothing while nobody talks, so its level drops to zero once sound stops arriving.</summary>
    public float TheirLevel => Elapsed.TotalSeconds - _theirsAt < 0.3 ? _theirs.Level : 0;

    public bool HearsApp => _app is not null;

    /// <summary>Seconds of audio sent to the speech model: what the meeting cost.</summary>
    public double SentSeconds { get { lock (_gate) return _sentSeconds; } }

    /// <summary>The latest lines, for the window.</summary>
    public IReadOnlyList<TranscriptLine> Recent(int count) => Doc("recording").Lines.TakeLast(count).ToList();

    /// <summary>
    /// The microphone's clock is the audio itself: samples counted from its first buffer. When the callbacks run more
    /// than a second ahead of that, the sound card dropped audio, and the count starts again from now.
    /// </summary>
    void OnMic(ReadOnlySpan<byte> pcm)
    {
        double byCallback = Elapsed.TotalSeconds - pcm.Length / (Segmenter.SampleRate * 2.0);
        double bySamples = _micAnchor + _micSamples / (double)Segmenter.SampleRate;
        if (_micSamples < 0 || byCallback - bySamples > 1.0)
        {
            _micAnchor = byCallback;
            _micSamples = 0;
            bySamples = byCallback;
        }
        _micSamples += pcm.Length / 2;
        foreach (var piece in _mine.Feed(pcm, bySamples)) Enqueue(piece, mine: true);
    }

    void Enqueue(Utterance piece, bool mine) => _queue.Writer.TryWrite((piece, mine));

    void Report(string problem)
    {
        Problem = problem;
        Log.Write(problem);
        Changed?.Invoke();
    }

    async Task WorkAsync()
    {
        await foreach (var (piece, mine) in _queue.Reader.ReadAllAsync())
        {
            if (_discarding) continue;
            try
            {
                var text = _halted is null ? await TranscribeAsync(piece) : null;
                lock (_gate)
                {
                    if (text is not null) _sentSeconds += piece.Seconds;
                    if (text is not null && !MeetingNotes.IsNoise(text)) _lines.Add(new MeetingLine(piece.Start, piece.End, mine, text));
                    _dirty = true;
                }
                if (text is null) Keep(piece, mine);
                Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Quitting mid-meeting: kept for later. Discarding: dropped.
                if (!_discarding) Keep(piece, mine);
            }
            catch (Exception e) { Log.Write($"Meeting piece failed: {e}"); }
        }
    }

    /// <summary>The text, or null when the API would not take it.</summary>
    async Task<string?> TranscribeAsync(Utterance piece)
    {
        var wav = Wav.FromPcm16(piece.Pcm, Segmenter.SampleRate, 1);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var key = KeyStore.Get() ?? throw new ApiException("No OpenAI key is set.");
                return await _api.TranscribeAsync(wav, _settings(), key, _cancel.Token, Context());
            }
            catch (ApiException e) when (e.Status == 400)
            {
                // This piece only (too short, unreadable): asking again gets the same answer.
                Log.Write($"Meeting piece at {piece.Start:0.0}s refused: {e.Message}");
                return "";
            }
            catch (ApiException e)
            {
                // A refused key or an empty account will not mend in a few seconds; a dropped connection may, while
                // the meeting goes on. At the end there is no time to wait: the rest is kept for "Write the notes again".
                if (!e.Transient || _finishing)
                {
                    if (_halted is null) Halt(e.Message);
                    return null;
                }
                if (attempt >= 4)
                {
                    Log.Write($"Meeting piece at {piece.Start:0.0}s not transcribed: {e.Message}");
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt * attempt), _cancel.Token);
            }
        }
    }

    void Halt(string reason)
    {
        _halted = reason;
        Report($"Transcribing stopped: {reason} What is said is kept; write the notes again once that is fixed.");
    }

    /// <summary>A piece not transcribed: into the pending folder as a WAV, named by when and whose it is.</summary>
    void Keep(Utterance piece, bool mine)
    {
        try
        {
            var dir = MeetingWriter.PendingDir(FilePath);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, MeetingWriter.PendingName(piece.Start, mine)), Wav.FromPcm16(piece.Pcm, Segmenter.SampleRate, 1));
            Interlocked.Increment(ref _kept);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Meeting piece not kept: {e.Message}"); }
    }

    /// <summary>The last words said, so a piece reads on from them.</summary>
    string Context()
    {
        lock (_gate)
            return string.Join(" ", _lines.OrderBy(l => l.Start).TakeLast(4).Select(l => l.Text));
    }

    MeetingDoc Doc(string status)
    {
        var s = _settings();
        var me = s.YourName.Length > 0 ? s.YourName : "Me";
        var them = MeetingNotes.TheirName(With, App);
        List<MeetingLine> lines;
        lock (_gate) lines = MeetingNotes.Clean(_lines);
        return new MeetingDoc
        {
            Title = App.Length > 0 ? $"{App} call" : "Meeting",
            Started = Started,
            Minutes = Elapsed.TotalMinutes,
            App = App,
            Me = me,
            With = With.Trim(),
            Status = status,
            Pending = _kept,
            Lines = lines.Select(l => new TranscriptLine(l.Start, l.Mine ? me : them, l.Text)).ToList(),
        };
    }

    void SaveIfChanged()
    {
        bool dirty;
        lock (_gate)
        {
            dirty = _dirty;
            _dirty = false;
        }
        if (dirty && !_finishing) Save("recording");
    }

    void Save(string status)
    {
        try { File.WriteAllText(FilePath, MeetingFile.Format(Doc(status))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Meeting file not saved: {e.Message}"); }
    }

    /// <summary>Stops the microphone and the app's sound, and cuts what was still being said into the queue.</summary>
    async Task StopListeningAsync()
    {
        _finishing = true;
        _saver.Change(Timeout.Infinite, Timeout.Infinite);
        try { _mic.StopRecording(); } catch (Exception e) when (e is InvalidOperationException or NAudio.MmException) { }
        await Task.WhenAny(_micStopped.Task, Task.Delay(2000)).ConfigureAwait(false);
        _app?.Dispose();
        _app = null;
        foreach (var piece in _mine.Flush()) Enqueue(piece, mine: true);
        foreach (var piece in _theirs.Flush()) Enqueue(piece, mine: false);
        _queue.Writer.TryComplete();
    }

    /// <summary>
    /// Stops listening and waits for the last pieces to be transcribed; returns the meeting for its notes. With
    /// <paramref name="discard"/> nothing more is sent, the file is deleted, and null comes back.
    /// </summary>
    public async Task<MeetingDoc?> StopAsync(bool discard)
    {
        if (discard)
        {
            _discarding = true;
            _cancel.Cancel();
        }
        await StopListeningAsync();
        await Task.WhenAll(_workers);
        if (discard)
        {
            try
            {
                File.Delete(FilePath);
                var pending = MeetingWriter.PendingDir(FilePath);
                if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Discarded meeting not fully removed: {e.Message}"); }
            Log.Write("Meeting notes discarded.");
            return null;
        }
        var doc = Doc("writing");
        Log.Write($"Meeting notes stopped after {Elapsed.TotalMinutes:0.0} min: {doc.Lines.Count} lines, {_kept} pieces kept for later, {SentSeconds:0}s sent");
        return doc;
    }

    /// <summary>
    /// Dikte is quitting mid-meeting: nothing is sent any more, what was not transcribed yet goes to the pending folder,
    /// and the transcript so far is saved. The notes can be written later from the Meetings tab.
    /// </summary>
    public void Abandon()
    {
        _halted ??= "Dikte quit.";
        _cancel.Cancel();
        StopListeningAsync().Wait(TimeSpan.FromSeconds(4));
        Task.WaitAll(_workers, TimeSpan.FromSeconds(3));
        while (_queue.Reader.TryRead(out var left)) Keep(left.Piece, left.Mine);
        Save("recording");
        Log.Write($"Quit during meeting notes: transcript saved, {_kept} pieces kept for later.");
    }

    public void Dispose()
    {
        _finishing = true;
        _saver.Dispose();
        _cancel.Cancel();
        _queue.Writer.TryComplete();
        try { _mic.StopRecording(); } catch (Exception e) when (e is InvalidOperationException or NAudio.MmException) { }
        _mic.Dispose();
        _app?.Dispose();
        _app = null;
        _cancel.Dispose();
    }
}

/// <summary>Turns a finished (or reopened) meeting into notes and saves the file under the meeting's title.</summary>
static class MeetingWriter
{
    /// <summary>
    /// Writes the notes and saves the file as "date title.md". Returns the file's path; on an API failure the transcript
    /// is saved without notes and the error is rethrown.
    /// </summary>
    public static async Task<string> WriteAsync(string path, MeetingDoc doc, Transcriber api, Settings s, CancellationToken ct)
    {
        doc.Pending = PendingCount(path);
        try
        {
            var key = KeyStore.Get() ?? throw new ApiException("No OpenAI key is set.");
            if (doc.Lines.Count == 0)
            {
                doc.Notes = doc.Pending > 0 ? "" : "_Nothing was said that could be written down._";
                doc.Status = doc.Pending > 0 ? "failed" : "done";
            }
            else
            {
                var (title, notes) = MeetingNotes.Split(await api.MeetingNotesAsync(doc, s, key, ct));
                if (title.Length > 0) doc.Title = title;
                doc.Notes = notes;
                doc.Status = "done";
            }
        }
        catch (Exception e) when (e is ApiException or OperationCanceledException)
        {
            doc.Status = "failed";
            Save(path, doc);
            throw;
        }
        return Save(path, doc);
    }

    /// <summary>Reads a notes file back, transcribes what was kept waiting, and writes the notes again.</summary>
    public static async Task<string> RewriteAsync(string path, Transcriber speech, Transcriber notes, Settings s, CancellationToken ct)
    {
        var doc = MeetingFile.Parse(await File.ReadAllTextAsync(path, ct)) ?? throw new InvalidDataException("This is not one of Dikte's meeting files.");
        var pending = PendingDir(path);
        if (Directory.Exists(pending))
        {
            var key = KeyStore.Get() ?? throw new ApiException("No OpenAI key is set.");
            var them = MeetingNotes.TheirName(doc.With, doc.App);
            foreach (var file in Directory.GetFiles(pending, "*.wav").Order())
            {
                var (start, mine) = ParsePending(file);
                var text = await speech.TranscribeAsync(await File.ReadAllBytesAsync(file, ct), s, key, ct);
                if (!MeetingNotes.IsNoise(text)) doc.Lines.Add(new TranscriptLine(start, mine ? doc.Me : them, text));
                // Saved after each piece, so a failure halfway loses nothing and the next try starts where this one stopped.
                doc.Lines = doc.Lines.OrderBy(l => l.At).ToList();
                File.Delete(file);
                doc.Pending = PendingCount(path);
                await File.WriteAllTextAsync(path, MeetingFile.Format(doc), ct);
            }
        }
        return await WriteAsync(path, doc, notes, s, ct);
    }

    /// <summary>Saves under the title, renaming the file (and its pending pieces) when the title changed.</summary>
    static string Save(string path, MeetingDoc doc)
    {
        var folder = Path.GetDirectoryName(path)!;
        var wanted = MeetingFile.FileName(doc.Started, doc.Title);
        var target = string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase) ? path : FreePath(folder, wanted);
        File.WriteAllText(target, MeetingFile.Format(doc));
        if (target == path) return target;

        var oldPending = PendingDir(path);
        try
        {
            if (Directory.Exists(oldPending)) Directory.Move(oldPending, PendingDir(target));
        }
        catch (IOException e) { Log.Write($"Pending pieces not moved: {e.Message}"); }
        // The new file is written; the old name going away is a nicety.
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Old meeting file not removed: {e.Message}"); }
        return target;
    }

    /// <summary>Audio the API did not take, kept in Dikte's own folder (not the notes folder, which may be synced).</summary>
    public static string PendingDir(string meetingPath) => Path.Combine(Paths.Pending, Path.GetFileNameWithoutExtension(meetingPath));

    static int PendingCount(string meetingPath)
    {
        var dir = PendingDir(meetingPath);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.wav").Length : 0;
    }

    public static string PendingName(double start, bool mine) =>
        $"{start.ToString("00000.00", CultureInfo.InvariantCulture)}-{(mine ? "me" : "them")}.wav";

    static (double Start, bool Mine) ParsePending(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        int dash = name.LastIndexOf('-');
        double.TryParse(name[..Math.Max(0, dash)], NumberStyles.Float, CultureInfo.InvariantCulture, out var start);
        return (start, name.EndsWith("-me", StringComparison.Ordinal));
    }

    /// <summary>The path in <paramref name="folder"/> for <paramref name="name"/>, with " (2)" and on when it is taken.</summary>
    public static string FreePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        return path;
    }

    /// <summary>
    /// The meetings in a folder, newest first, from each file's opening (the header and the summary): a folder of
    /// long transcripts, or one shared with other notes, is not read in full.
    /// </summary>
    public static List<(string File, MeetingDoc Doc)> List(string folder)
    {
        var found = new List<(string, MeetingDoc)>();
        if (!Directory.Exists(folder)) return found;
        var head = new char[4096];
        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            try
            {
                using var reader = new StreamReader(file);
                int read = reader.ReadBlock(head, 0, head.Length);
                if (MeetingFile.Parse(new string(head, 0, read)) is { } doc) found.Add((file, doc));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.OrderByDescending(f => f.Item2.Started).ToList();
    }
}
