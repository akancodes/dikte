using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Dikte.Core;
using NAudio.Wave;

namespace Dikte;

/// <summary>
/// One meeting being recorded: your microphone and the call app's sound as two streams, each cut at its pauses
/// and transcribed while the meeting goes on, so the notes can be written moments after it ends. Which stream a
/// line came from says who spoke. The transcript so far is saved every few seconds; audio stays in memory.
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
    readonly List<(Utterance Piece, bool Mine)> _failed = new();
    double _sentSeconds, _theirsAt = -1;
    bool _dirty;
    volatile bool _finishing;

    /// <summary>The call app ("Discord"), or empty when only the microphone listens (a meeting in the room).</summary>
    public string App { get; }
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>Who is on the call, as typed in the window: names the other side in the transcript and the notes.</summary>
    public string With { get; set; }
    /// <summary>The notes file, written while recording.</summary>
    public string FilePath { get; }
    /// <summary>Something that does not stop the meeting but leaves a side unheard.</summary>
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
        _mic.DataAvailable += (_, e) =>
        {
            double at = Elapsed.TotalSeconds - e.BytesRecorded / (Segmenter.SampleRate * 2.0);
            foreach (var piece in _mine.Feed(e.Buffer.AsSpan(0, e.BytesRecorded), at)) Enqueue(piece, mine: true);
        };
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

    public int LineCount { get { lock (_gate) return _lines.Count; } }

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
            if (_cancel.IsCancellationRequested) continue;
            try
            {
                var text = await TranscribeAsync(piece);
                lock (_gate)
                {
                    _sentSeconds += piece.Seconds;
                    if (text is null) _failed.Add((piece, mine));
                    else if (!MeetingNotes.IsNoise(text)) _lines.Add(new MeetingLine(piece.Start, piece.End, mine, text));
                    _dirty = true;
                }
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Write($"Meeting piece failed: {e}"); }
        }
    }

    /// <summary>The text, or null when the API kept refusing: the piece is then kept for "Write notes again".</summary>
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
            catch (ApiException e)
            {
                // While the meeting goes on there is time to wait out a dropped connection; at the end there is not.
                if (attempt >= (_finishing ? 2 : 4))
                {
                    Log.Write($"Meeting piece at {piece.Start:0.0}s not transcribed: {e.Message}");
                    return null;
                }
                await Task.Delay(TimeSpan.FromSeconds(_finishing ? 1 : 2 * attempt * attempt), _cancel.Token);
            }
        }
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

    /// <summary>
    /// Stops listening and waits for the last pieces to be transcribed. Returns the meeting and the pieces the API
    /// refused; with <paramref name="discard"/> nothing more is sent and the file is deleted.
    /// </summary>
    public async Task<(MeetingDoc Doc, List<(Utterance Piece, bool Mine)> Failed)?> StopAsync(bool discard)
    {
        _finishing = true;
        _saver.Change(Timeout.Infinite, Timeout.Infinite);
        if (discard) _cancel.Cancel();
        _mic.StopRecording();
        await Task.WhenAny(_micStopped.Task, Task.Delay(2000));
        _app?.Dispose();
        _app = null;
        if (!discard)
        {
            foreach (var piece in _mine.Flush()) Enqueue(piece, mine: true);
            foreach (var piece in _theirs.Flush()) Enqueue(piece, mine: false);
        }
        _queue.Writer.TryComplete();
        await Task.WhenAll(_workers);
        if (discard)
        {
            try { File.Delete(FilePath); } catch (IOException) { }
            Log.Write("Meeting notes discarded.");
            return null;
        }
        List<(Utterance, bool)> failed;
        lock (_gate) failed = _failed.ToList();
        var doc = Doc("writing");
        Log.Write($"Meeting notes stopped after {Elapsed.TotalMinutes:0.0} min: {doc.Lines.Count} lines, {failed.Count} pieces not transcribed, {SentSeconds:0}s sent");
        return (doc, failed);
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
    /// Writes the notes and saves the file as "date title.md", leaving pieces the API refused on disk for a later try.
    /// Returns the file's path; on an API failure the transcript is saved without notes and the error is rethrown.
    /// </summary>
    public static async Task<string> WriteAsync(string path, MeetingDoc doc, IReadOnlyList<(Utterance Piece, bool Mine)> failed,
        Transcriber api, Settings s, CancellationToken ct)
    {
        doc.Pending = failed.Count;
        var key = KeyStore.Get();
        try
        {
            if (key is null) throw new ApiException("No OpenAI key is set.");
            if (doc.Lines.Count == 0)
            {
                doc.Notes = failed.Count > 0 ? "" : "_Nothing was said that could be written down._";
                doc.Status = failed.Count > 0 ? "failed" : "done";
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
            path = Save(path, doc, failed);
            throw;
        }
        return Save(path, doc, failed);
    }

    /// <summary>Reads a notes file back, transcribes what was left waiting, and writes the notes again.</summary>
    public static async Task<string> RewriteAsync(string path, Transcriber api, Settings s, CancellationToken ct)
    {
        var doc = MeetingFile.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("This is not one of Dikte's meeting files.");
        var pending = PendingDir(path);
        var failed = new List<(Utterance, bool)>();
        if (Directory.Exists(pending))
        {
            var key = KeyStore.Get() ?? throw new ApiException("No OpenAI key is set.");
            var them = MeetingNotes.TheirName(doc.With, doc.App);
            foreach (var file in Directory.GetFiles(pending, "*.wav").Order())
            {
                var (start, mine) = ParsePending(file);
                var wav = await File.ReadAllBytesAsync(file, ct);
                var piece = new Utterance(start, start + (wav.Length - 44) / (Segmenter.SampleRate * 2.0), wav[44..]);
                try
                {
                    var text = await api.TranscribeAsync(wav, s, key, ct);
                    if (!MeetingNotes.IsNoise(text)) doc.Lines.Add(new TranscriptLine(start, mine ? doc.Me : them, text));
                }
                catch (ApiException) { failed.Add((piece, mine)); }
            }
            doc.Lines = doc.Lines.OrderBy(l => l.At).ToList();
        }
        return await WriteAsync(path, doc, failed, api, s, ct);
    }

    /// <summary>Saves under the title (renaming the file if it changed) and keeps the waiting pieces beside its name.</summary>
    static string Save(string path, MeetingDoc doc, IReadOnlyList<(Utterance Piece, bool Mine)> failed)
    {
        var folder = Path.GetDirectoryName(path)!;
        var wanted = MeetingFile.FileName(doc.Started, doc.Title);
        var target = string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase) ? path : FreePath(folder, wanted);
        File.WriteAllText(target, MeetingFile.Format(doc));
        if (target != path) File.Delete(path);

        var oldPending = PendingDir(path);
        if (Directory.Exists(oldPending)) Directory.Delete(oldPending, recursive: true);
        if (failed.Count > 0)
        {
            var dir = PendingDir(target);
            Directory.CreateDirectory(dir);
            foreach (var (piece, mine) in failed)
                File.WriteAllBytes(Path.Combine(dir, $"{piece.Start.ToString("0000.00", CultureInfo.InvariantCulture)}-{(mine ? "me" : "them")}.wav"),
                    Wav.FromPcm16(piece.Pcm, Segmenter.SampleRate, 1));
        }
        return target;
    }

    /// <summary>Audio the API refused, kept in Dikte's own folder (not the notes folder, which may be synced).</summary>
    public static string PendingDir(string meetingPath) => Path.Combine(Paths.Pending, Path.GetFileNameWithoutExtension(meetingPath));

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

    /// <summary>The meetings in a folder, newest first.</summary>
    public static List<(string File, MeetingDoc Doc)> List(string folder)
    {
        var found = new List<(string, MeetingDoc)>();
        if (!Directory.Exists(folder)) return found;
        foreach (var file in Directory.EnumerateFiles(folder, "*.md"))
        {
            try
            {
                if (MeetingFile.Parse(File.ReadAllText(file)) is { } doc) found.Add((file, doc));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found.OrderByDescending(f => f.Item2.Started).ToList();
    }
}
