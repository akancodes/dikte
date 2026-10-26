using System.Buffers.Binary;

namespace Dikte.Core;

/// <summary>A stretch of speech cut out of a longer recording: where it sits on the meeting's clock, and its 16 kHz audio.</summary>
public sealed record Utterance(double Start, double End, byte[] Pcm)
{
    public double Seconds => End - Start;
}

/// <summary>
/// Cuts a live 16 kHz mono 16-bit stream into utterances at the pauses, so a long call reaches the speech model
/// in pieces that each say when they were spoken, and silence is never sent (or paid for).
///
/// A frame counts as speech when it is clearly louder than the noise floor: the quietest moment of the last
/// eight seconds. Talk always has quiet moments between words and breaths; a fan or a hiss never does, so it
/// becomes the floor instead of reading as talking.
/// </summary>
public sealed class Segmenter
{
    public const int SampleRate = 16000;
    const int FrameSamples = 320; // 20 ms
    const int FrameBytes = FrameSamples * 2;
    const double FrameSeconds = FrameSamples / (double)SampleRate;
    const int PadFrames = 12; // 240 ms kept before the first word and after the last
    const float MinRms = 0.006f;

    readonly double _pause, _maxSeconds, _minSpeech;
    readonly Queue<byte[]> _before = new();
    readonly byte[] _carry = new byte[FrameBytes];
    int _carried;
    double _clock;
    readonly Queue<float> _blocks = new(); // the quietest frame of each 100 ms, for the last 8 s
    float _blockMin = float.MaxValue;
    int _blockFrames;
    float _noise = 0.002f;

    List<(byte[] Pcm, bool Voiced, float Rms)>? _current;
    double _currentStart;
    int _voiced, _silentRun;

    /// <param name="pause">Silence that ends an utterance.</param>
    /// <param name="maxSeconds">Longer speech is cut at its quietest moment near this length.</param>
    /// <param name="minSpeech">Utterances with less speech than this (a click, a cough) are dropped.</param>
    public Segmenter(double pause = 0.9, double maxSeconds = 30, double minSpeech = 0.3)
    {
        _pause = pause;
        _maxSeconds = maxSeconds;
        _minSpeech = minSpeech;
    }

    /// <summary>Loudness of the latest frame, 0..1, for a level meter.</summary>
    public float Level { get; private set; }

    /// <summary>Feeds audio that started playing at <paramref name="at"/> seconds on the meeting's clock.</summary>
    public IReadOnlyList<Utterance> Feed(ReadOnlySpan<byte> pcm, double at)
    {
        var done = new List<Utterance>();
        // Nothing arrived for a while (an app that plays nothing sends nothing): that time was silence.
        if (at > _clock + 0.25) Gap(at - _clock, done);
        while (pcm.Length > 0)
        {
            int take = Math.Min(FrameBytes - _carried, pcm.Length);
            pcm[..take].CopyTo(_carry.AsSpan(_carried));
            _carried += take;
            pcm = pcm[take..];
            if (_carried < FrameBytes) break;
            _carried = 0;
            Frame(_carry.ToArray(), done);
        }
        return done;
    }

    /// <summary>The end of the recording: the utterance still open, if it holds speech.</summary>
    public IReadOnlyList<Utterance> Flush()
    {
        var done = new List<Utterance>();
        if (_current is not null) Close(done, _current.Count);
        return done;
    }

    void Gap(double seconds, List<Utterance> done)
    {
        _before.Clear();
        for (int i = 0; i < Math.Min(80, (int)(seconds * 10)); i++) Floor(0);
        if (_current is not null)
        {
            if (seconds + _silentRun * FrameSeconds >= _pause) Close(done, _current.Count - Math.Max(0, _silentRun - PadFrames));
            else
            {
                // A short gap inside speech: kept as silence, so the words around it stay apart.
                int frames = (int)(seconds / FrameSeconds);
                for (int i = 0; i < frames; i++) _current.Add((new byte[FrameBytes], false, 0));
                _silentRun += frames;
            }
        }
        _clock += seconds;
    }

    void Frame(byte[] frame, List<Utterance> done)
    {
        float rms = Rms(frame);
        Level = Wav.Peak(frame);
        bool voiced = rms > Math.Max(MinRms, _noise * 3);
        _blockMin = Math.Min(_blockMin, rms);
        if (++_blockFrames == 5) Floor(_blockMin);

        if (_current is null)
        {
            if (voiced)
            {
                _current = _before.Select(b => (b, false, 0f)).ToList();
                _currentStart = _clock - _before.Count * FrameSeconds;
                _before.Clear();
                _current.Add((frame, true, rms));
                _voiced = 1;
                _silentRun = 0;
            }
            else
            {
                _before.Enqueue(frame);
                if (_before.Count > PadFrames) _before.Dequeue();
            }
        }
        else
        {
            _current.Add((frame, voiced, rms));
            if (voiced)
            {
                _voiced++;
                _silentRun = 0;
            }
            else _silentRun++;

            if (_silentRun * FrameSeconds >= _pause) Close(done, _current.Count - (_silentRun - PadFrames));
            else if (_current.Count * FrameSeconds >= _maxSeconds) Split(done);
        }
        _clock += FrameSeconds;
    }

    /// <summary>Takes in the quietest frame of a 100 ms block and works out the floor again.</summary>
    void Floor(float quietest)
    {
        _blocks.Enqueue(quietest);
        if (_blocks.Count > 80) _blocks.Dequeue();
        _blockMin = float.MaxValue;
        _blockFrames = 0;
        // Until eight seconds have been heard the floor stays low: a stream that starts with talking is not noise.
        float floor = _blocks.Min();
        _noise = Math.Max(0.0005f, _blocks.Count < 80 ? Math.Min(0.002f, floor) : floor);
    }

    /// <summary>Ends the open utterance after its first <paramref name="keep"/> frames.</summary>
    void Close(List<Utterance> done, int keep)
    {
        var frames = _current!;
        _current = null;
        keep = Math.Clamp(keep, 0, frames.Count);
        if (_voiced * FrameSeconds >= _minSpeech && keep > 0) done.Add(Make(frames, 0, keep, _currentStart));
        _voiced = _silentRun = 0;
    }

    /// <summary>Too long to wait for a pause: cut at the quietest 100 ms of the last three seconds and carry on.</summary>
    void Split(List<Utterance> done)
    {
        var frames = _current!;
        int from = Math.Max(1, frames.Count - 150), cut = frames.Count;
        float quietest = float.MaxValue;
        for (int i = from; i + 5 <= frames.Count; i++)
        {
            float sum = 0;
            for (int j = i; j < i + 5; j++) sum += frames[j].Rms;
            if (sum < quietest)
            {
                quietest = sum;
                cut = i + 2;
            }
        }
        int voicedBefore = frames.Take(cut).Count(f => f.Voiced);
        if (voicedBefore * FrameSeconds >= _minSpeech) done.Add(Make(frames, 0, cut, _currentStart));
        _currentStart += cut * FrameSeconds;
        _current = frames.Skip(cut).ToList();
        _voiced = _current.Count(f => f.Voiced);
    }

    static Utterance Make(List<(byte[] Pcm, bool Voiced, float Rms)> frames, int from, int count, double start)
    {
        var pcm = new byte[count * FrameBytes];
        for (int i = 0; i < count; i++) frames[from + i].Pcm.CopyTo(pcm, i * FrameBytes);
        return new Utterance(start, start + count * FrameSeconds, pcm);
    }

    static float Rms(ReadOnlySpan<byte> frame)
    {
        double sum = 0;
        for (int i = 0; i + 1 < frame.Length; i += 2)
        {
            double v = BinaryPrimitives.ReadInt16LittleEndian(frame[i..]) / 32768.0;
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / (frame.Length / 2));
    }
}
