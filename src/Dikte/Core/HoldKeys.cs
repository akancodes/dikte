namespace Dikte.Core;

/// <summary>What a recording is for: plain dictation, dictation written in another language, or an edit of the selection.</summary>
public enum DictationMode { Dictate, Translate, Edit }

/// <summary>
/// Several hold-to-talk shortcuts, one per mode, each with its own <see cref="HotkeyMachine"/>. Only one
/// records at a time: when two complete on the same key, the one with more keys wins (ctrl+mouse4 over
/// mouse4), and a shortcut pressed while another is recording does not start a second recording.
/// </summary>
public sealed class HoldKeys
{
    readonly List<(DictationMode Mode, HotkeySpec Spec, HotkeyMachine Machine)> _keys = new();

    public HoldKeys(IEnumerable<(DictationMode Mode, HotkeySpec Spec)> keys, int lockVk)
    {
        foreach (var (mode, spec) in keys) _keys.Add((mode, spec, new HotkeyMachine(spec, lockVk)));
        if (_keys.Count == 0) throw new ArgumentException("At least one hold shortcut is needed.", nameof(keys));
    }

    public bool IsRecording => _keys.Any(k => k.Machine.IsRecording);
    public bool IsLocked => _keys.Any(k => k.Machine.IsLocked);

    public HoldResult Feed(int vk, bool isDown)
    {
        var results = _keys.Select(k => k.Machine.Feed(vk, isDown)).ToArray();
        bool swallow = results.Any(r => r.Swallow), suppressMenu = results.Any(r => r.SuppressMenu);

        var starts = Enumerable.Range(0, _keys.Count).Where(i => results[i].Command == HotkeyCommand.Start).ToList();
        if (starts.Count > 0)
        {
            int winner = starts.MaxBy(i => _keys[i].Spec.Size);
            // Someone else still recording (hands-free, say) keeps the microphone.
            bool busy = Enumerable.Range(0, _keys.Count).Any(i => !starts.Contains(i) && _keys[i].Machine.IsRecording);
            foreach (int i in starts)
            {
                if (i == winner && !busy) continue;
                _keys[i].Machine.Reset();
                results[i] = results[i] with { Command = HotkeyCommand.None };
            }
        }

        var commands = new List<(HotkeyCommand, DictationMode)>();
        // Endings first, so a Cancel of one shortcut lands before the Start of the one that replaced it.
        for (int i = 0; i < _keys.Count; i++)
            if (results[i].Command is not (HotkeyCommand.None or HotkeyCommand.Start)) commands.Add((results[i].Command, _keys[i].Mode));
        for (int i = 0; i < _keys.Count; i++)
            if (results[i].Command == HotkeyCommand.Start) commands.Add((HotkeyCommand.Start, _keys[i].Mode));
        return new HoldResult(commands, swallow, suppressMenu);
    }

    /// <summary>See <see cref="HotkeyMachine.Resync"/>; returns StopAndSend when that ends a hold.</summary>
    public HotkeyCommand Resync(Func<int, bool> isPhysicallyDown)
    {
        var command = HotkeyCommand.None;
        foreach (var k in _keys)
            if (k.Machine.Resync(isPhysicallyDown) == HotkeyCommand.StopAndSend) command = HotkeyCommand.StopAndSend;
        return command;
    }

    /// <summary>Hands-free plain dictation started from the tray, the window or the tap shortcut.</summary>
    public void EnterLocked() => _keys[0].Machine.EnterLocked();

    public void Reset()
    {
        foreach (var k in _keys) k.Machine.Reset();
    }
}

public sealed record HoldResult(IReadOnlyList<(HotkeyCommand Command, DictationMode Mode)> Commands, bool Swallow, bool SuppressMenu);
