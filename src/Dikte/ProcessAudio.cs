using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dikte;

/// <summary>
/// Records what one app plays, with its child processes, and nothing else on the speakers: Windows' process
/// loopback (Windows 10 build 20348 and Windows 11). For a call app that is the other people's voices.
/// Delivers 16 kHz mono 16-bit PCM with the time it was played on a Stopwatch-based clock.
/// </summary>
sealed class ProcessAudio : IDisposable
{
    const int Rate = 16000;
    const uint StreamLoopback = 0x00020000, StreamEventCallback = 0x00040000, StreamAutoConvertPcm = 0x80000000, StreamSrcDefaultQuality = 0x08000000;
    const uint BufferSilent = 0x2;

    readonly AudioInterop.IAudioClient _client;
    readonly AudioInterop.IAudioCaptureClient _capture;
    readonly AutoResetEvent _ready = new(false);
    readonly Thread _thread;
    readonly long _origin;
    readonly int _channels, _rate;
    volatile bool _stopping;

    /// <summary>A buffer of 16 kHz mono PCM and the seconds since <c>origin</c> it started playing at.</summary>
    public event Action<byte[], double>? Data;

    /// <summary>The capture broke off (the app quit, the audio service restarted): the reason.</summary>
    public event Action<string>? Failed;

    ProcessAudio(AudioInterop.IAudioClient client, long origin)
    {
        _client = client;
        _origin = origin;
        // The audio engine converts to the asked format; should it refuse 16 kHz mono, 48 kHz stereo is converted here.
        (_rate, _channels) = (Rate, 1);
        int hr = Initialize(Rate, 1);
        if (hr < 0)
        {
            (_rate, _channels) = (48000, 2);
            hr = Initialize(48000, 2);
        }
        Check(hr, "Initialize");
        Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
        Check(_client.GetService(typeof(AudioInterop.IAudioCaptureClient).GUID, out var capture), "GetService");
        _capture = (AudioInterop.IAudioCaptureClient)capture;
        Check(_client.Start(), "Start");
        _thread = new Thread(Run) { IsBackground = true, Name = "Dikte app audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Starts recording what <paramref name="processId"/> and its children play.</summary>
    /// <param name="origin">The <see cref="Stopwatch.GetTimestamp"/> the clock counts from.</param>
    public static Task<ProcessAudio> StartAsync(int processId, long origin) => Task.Run(async () =>
    {
        // On a thread-pool (multithreaded apartment) thread, as process loopback wants.
        var client = await AudioInterop.ActivateProcessLoopbackAsync(processId).ConfigureAwait(false);
        return new ProcessAudio(client, origin);
    });

    int Initialize(int rate, int channels)
    {
        var format = new AudioInterop.WaveFormatEx
        {
            wFormatTag = 1, // PCM
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)rate,
            wBitsPerSample = 16,
            nBlockAlign = (ushort)(channels * 2),
            nAvgBytesPerSec = (uint)(rate * channels * 2),
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioInterop.WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, ptr, false);
            // 200 ms of buffer: the thread may be late now and then without losing sound.
            return _client.Initialize(0, StreamLoopback | StreamEventCallback | StreamAutoConvertPcm | StreamSrcDefaultQuality, 2_000_000, 0, ptr, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    void Run()
    {
        int blockAlign = _channels * 2;
        double ticksPer100ns = Stopwatch.Frequency / 1e7;
        try
        {
            while (!_stopping)
            {
                _ready.WaitOne(100);
                while (!_stopping)
                {
                    Check(_capture.GetNextPacketSize(out uint next), "GetNextPacketSize");
                    if (next == 0) break;
                    Check(_capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc), "GetBuffer");
                    var raw = new byte[frames * blockAlign];
                    if ((flags & BufferSilent) == 0 && frames > 0) Marshal.Copy(data, raw, 0, raw.Length);
                    Check(_capture.ReleaseBuffer(frames), "ReleaseBuffer");
                    if (frames == 0) continue;
                    var pcm = _rate == Rate ? raw : Downsample(raw);
                    // The packet's own timestamp (100 ns units of the performance counter) puts it on the clock.
                    double at = qpc != 0
                        ? (qpc * ticksPer100ns - _origin) / Stopwatch.Frequency
                        : (Stopwatch.GetTimestamp() - _origin) / (double)Stopwatch.Frequency - frames / (double)_rate;
                    Data?.Invoke(pcm, at);
                }
            }
        }
        catch (Exception e)
        {
            // An exception escaping this thread would end the whole app.
            if (_stopping) return;
            Log.Write($"App audio capture ended: {e}");
            try { Failed?.Invoke(e.Message); } catch (Exception inner) { Log.Write($"App audio failure report failed: {inner.Message}"); }
        }
    }

    /// <summary>48 kHz stereo to 16 kHz mono: both channels and three samples averaged into one.</summary>
    static byte[] Downsample(byte[] raw)
    {
        int frames = raw.Length / 4, outFrames = frames / 3;
        var pcm = new byte[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            int sum = 0;
            for (int j = 0; j < 3; j++)
            {
                int o = (i * 3 + j) * 4;
                sum += BitConverter.ToInt16(raw, o) + BitConverter.ToInt16(raw, o + 2);
            }
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(sum / 6));
        }
        return pcm;
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"App audio: {what} failed (0x{hr:X8}).", hr);
    }

    public void Dispose()
    {
        _stopping = true;
        _ready.Set();
        _thread.Join(1000);
        try { _client.Stop(); } catch (COMException) { }
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        _ready.Dispose();
    }
}

/// <summary>The bits of Core Audio that NAudio 2.2 does not wrap: activating a process loopback stream.</summary>
static class AudioInterop
{
    const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
    const ushort VtBlob = 65;
    const int ActivationTypeProcessLoopback = 1, IncludeTargetProcessTree = 0;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a BLOB: the shape ActivateAudioInterfaceAsync takes its parameters in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BlobVariant
    {
        public ushort vt, reserved1, reserved2, reserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>Marks the completion handler as callable from any thread, which activation requires.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int hr, out object client);
                if (hr < 0) Result.TrySetException(new COMException($"App audio could not be opened (0x{hr:X8}).", hr));
                else Result.TrySetResult((IAudioClient)client);
            }
            catch (Exception e) { Result.TrySetException(e); }
        }
    }

    public static async Task<IAudioClient> ActivateProcessLoopbackAsync(int processId)
    {
        var parameters = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        try
        {
            Marshal.StructureToPtr(new ActivationParams
            {
                ActivationType = ActivationTypeProcessLoopback,
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = IncludeTargetProcessTree,
            }, parameters, false);
            Marshal.StructureToPtr(new BlobVariant { vt = VtBlob, cbSize = (uint)Marshal.SizeOf<ActivationParams>(), pBlobData = parameters }, variant, false);
            var done = new Completion();
            ActivateAudioInterfaceAsync(ProcessLoopbackDevice, typeof(IAudioClient).GUID, variant, done, out var operation);
            try
            {
                var finished = await Task.WhenAny(done.Result.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                if (finished != done.Result.Task) throw new TimeoutException("App audio did not open in time.");
                return await done.Result.Task.ConfigureAwait(false);
            }
            finally { Marshal.ReleaseComObject(operation); }
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }
}
