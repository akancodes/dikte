using System.Text.Json;
using NAudio.CoreAudioApi;

namespace Dikte;

/// <summary>
/// Turns other apps down while recording and back up afterwards, through each app's own volume
/// slider in the Windows mixer, so system volume and Dikte's own sounds stay as they are.
///
/// What was changed is written to disk first: if Dikte dies mid-recording, the next start puts it back.
/// </summary>
sealed class Ducker
{
    static readonly string StatePath = Path.Combine(Paths.Dir, "ducked.json");
    readonly object _gate = new();
    Dictionary<string, float>? _saved;
    Task _work = Task.CompletedTask;

    public void Duck(double level)
    {
        lock (_gate) _work = _work.ContinueWith(_ => DuckNow((float)level), TaskScheduler.Default);
    }

    public void Restore()
    {
        lock (_gate) _work = _work.ContinueWith(_ => RestoreNow(), TaskScheduler.Default);
    }

    /// <summary>On quit: put everything back before the process goes.</summary>
    public void RestoreAndWait(TimeSpan timeout)
    {
        Task work;
        lock (_gate) work = _work = _work.ContinueWith(_ => RestoreNow(), TaskScheduler.Default);
        work.Wait(timeout);
    }

    /// <summary>Puts back what a crashed run left turned down.</summary>
    public static void RecoverFromCrash()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(StatePath));
            if (saved is not null) Apply(saved, fade: false);
            Log.Write($"Restored the volume of {saved?.Count ?? 0} apps left turned down by a previous run.");
        }
        catch (Exception e) { Log.Write($"Volume recovery failed: {e.Message}"); }
        try { File.Delete(StatePath); } catch (IOException) { }
    }

    void DuckNow(float level)
    {
        if (_saved is not null) return;
        try
        {
            var saved = new Dictionary<string, float>();
            var playing = new List<(AudioSessionControl session, float volume)>();
            int self = Environment.ProcessId;
            foreach (var session in Sessions())
            {
                if (session.GetProcessID == self || session.IsSystemSoundsSession) continue;
                if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                float volume = session.SimpleAudioVolume.Volume;
                if (volume <= volume * level + 0.001f) continue;
                saved[session.GetSessionInstanceIdentifier] = volume;
                playing.Add((session, volume));
            }
            if (saved.Count == 0) return;
            // On disk before touching anything, so a crash cannot leave the music quiet for good.
            File.WriteAllText(StatePath, JsonSerializer.Serialize(saved));
            _saved = saved;
            foreach (var (session, volume) in playing) session.SimpleAudioVolume.Volume = volume * level;
        }
        catch (Exception e) { Log.Write($"Turning other apps down failed: {e.Message}"); }
    }

    void RestoreNow()
    {
        var saved = _saved;
        _saved = null;
        if (saved is null) return;
        try { Apply(saved, fade: true); }
        catch (Exception e) { Log.Write($"Turning other apps back up failed: {e.Message}"); }
        try { File.Delete(StatePath); } catch (IOException) { }
    }

    static void Apply(Dictionary<string, float> saved, bool fade)
    {
        var sessions = Sessions().Where(s => saved.ContainsKey(s.GetSessionInstanceIdentifier)).ToList();
        var from = sessions.Select(s => s.SimpleAudioVolume.Volume).ToList();
        int steps = fade ? 8 : 1;
        for (int step = 1; step <= steps; step++)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                float to = saved[sessions[i].GetSessionInstanceIdentifier];
                sessions[i].SimpleAudioVolume.Volume = from[i] + (to - from[i]) * step / steps;
            }
            if (step < steps) Thread.Sleep(30);
        }
    }

    static IEnumerable<AudioSessionControl> Sessions()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++) yield return sessions[i];
        }
    }
}
