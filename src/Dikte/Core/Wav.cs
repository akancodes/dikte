using System.Buffers.Binary;

namespace Dikte.Core;

public static class Wav
{
    /// <summary>Wraps 16-bit little-endian PCM in a RIFF/WAVE header.</summary>
    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        int byteRate = sampleRate * channels * 2;
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }

    /// <summary>Peak absolute sample of a 16-bit PCM buffer, 0..1.</summary>
    public static float Peak(ReadOnlySpan<byte> pcm)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int v = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]));
            if (v > peak) peak = v;
        }
        return peak / 32768f;
    }

    /// <summary>A short sine blip sequence (frequencies in Hz, each tone in ms) for feedback sounds.</summary>
    public static byte[] Tones(int sampleRate, float volume, params (double hz, int ms)[] tones)
    {
        var samples = new List<short>();
        foreach (var (hz, ms) in tones)
        {
            int n = sampleRate * ms / 1000;
            int fade = Math.Min(n / 4, sampleRate / 200);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)Math.Max(1, fade));
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / sampleRate) * env * volume * short.MaxValue));
            }
        }
        var pcm = new byte[samples.Count * 2];
        for (int i = 0; i < samples.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
        return FromPcm16(pcm, sampleRate, 1);
    }
}
