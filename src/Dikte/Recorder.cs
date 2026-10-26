using Dikte.Core;
using NAudio.Wave;

namespace Dikte;

/// <summary>Records 16 kHz mono 16-bit audio, the format speech models are trained on, so uploads stay small.</summary>
sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;

    readonly WaveInEvent _wave;
    readonly MemoryStream _pcm = new();
    readonly object _lock = new();
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Exception? _error;

    public Recorder(string microphone)
    {
        _wave = new WaveInEvent
        {
            DeviceNumber = FindDevice(microphone),
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
            NumberOfBuffers = 4,
        };
        _wave.DataAvailable += OnData;
        _wave.RecordingStopped += (_, e) =>
        {
            _error = e.Exception;
            _stopped.TrySetResult();
        };
    }

    /// <summary>Loudness of the latest buffer, 0..1, for the level meter.</summary>
    public float Level { get; private set; }

    /// <summary>Loudest buffer so far: tells silence (a muted mic, a cough) from speech.</summary>
    public float MaxLevel { get; private set; }

    public TimeSpan Duration
    {
        get { lock (_lock) return TimeSpan.FromSeconds(_pcm.Length / (SampleRate * 2.0)); }
    }

    public void Start() => _wave.StartRecording();

    void OnData(object? sender, WaveInEventArgs e)
    {
        var chunk = e.Buffer.AsSpan(0, e.BytesRecorded);
        lock (_lock) _pcm.Write(chunk);
        float peak = Wav.Peak(chunk);
        Level = peak;
        if (peak > MaxLevel) MaxLevel = peak;
    }

    /// <summary>Stops and returns the recording as a WAV file.</summary>
    public async Task<byte[]> StopAsync()
    {
        _wave.StopRecording();
        await Task.WhenAny(_stopped.Task, Task.Delay(2000));
        if (_error is not null) throw new InvalidOperationException($"The microphone stopped: {_error.Message}");
        lock (_lock) return Wav.FromPcm16(_pcm.GetBuffer().AsSpan(0, (int)_pcm.Length), SampleRate, 1);
    }

    public void Dispose()
    {
        _wave.DataAvailable -= OnData;
        _wave.Dispose();
    }

    public static IReadOnlyList<string> Devices()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++) names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }

    /// <summary>The waveIn device number for a microphone name; -1 is the Windows default.</summary>
    public static int FindDevice(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1; // WAVE_MAPPER: the Windows default
        var devices = Devices();
        for (int i = 0; i < devices.Count; i++)
            if (string.Equals(devices[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        // Windows cuts device names to 31 characters; match on the start.
        for (int i = 0; i < devices.Count; i++)
            if (name.StartsWith(devices[i], StringComparison.OrdinalIgnoreCase) || devices[i].StartsWith(name, StringComparison.OrdinalIgnoreCase)) return i;
        Log.Write($"Microphone \"{name}\" not found; using the default.");
        return -1;
    }
}
