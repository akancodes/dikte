namespace Dikte.Core;

public enum CaptureOutcome { Waiting, Done, Cancelled, Unusable }

/// <summary>
/// "Press the shortcut you want": collects what is held, and when everything is let go (or a mouse
/// button is pressed) names it the way settings write it. One modifier alone keeps its side ("rctrl");
/// in a combination either side counts ("ctrl+win").
/// </summary>
public sealed class KeyCapture
{
    static readonly (string Name, int[] Vks)[] Generic =
    {
        ("ctrl", new[] { HotkeySpec.VkLControl, HotkeySpec.VkRControl, HotkeySpec.VkControl }),
        ("shift", new[] { HotkeySpec.VkLShift, HotkeySpec.VkRShift, HotkeySpec.VkShift }),
        ("alt", new[] { HotkeySpec.VkLMenu, HotkeySpec.VkRMenu, HotkeySpec.VkMenu }),
        ("win", new[] { HotkeySpec.VkLWin, HotkeySpec.VkRWin }),
    };

    readonly HashSet<int> _held = new();
    readonly List<int> _chord = new();

    public string? Result { get; private set; }

    public CaptureOutcome Feed(int vk, bool isDown)
    {
        if (isDown)
        {
            if (vk == HotkeyMachine.VkEscape && _held.Count == 0) return CaptureOutcome.Cancelled;
            if (_held.Add(vk) && !_chord.Contains(vk)) _chord.Add(vk);
            // A mouse button completes at once, with whatever keys are held alongside it.
            if (KeyNames.IsMouse(vk)) return Finish();
            return CaptureOutcome.Waiting;
        }
        _held.Remove(vk);
        return _held.Count == 0 && _chord.Count > 0 ? Finish() : CaptureOutcome.Waiting;
    }

    CaptureOutcome Finish()
    {
        Result = Describe(_chord);
        return Result is null ? CaptureOutcome.Unusable : CaptureOutcome.Done;
    }

    public static string? Describe(IReadOnlyList<int> chord)
    {
        if (chord.Count == 1)
        {
            var single = KeyNames.Name(chord[0]);
            return single == "lwin" || single == "rwin" ? "win" : single;
        }
        var parts = new List<string>();
        foreach (var (name, vks) in Generic)
            if (chord.Any(vks.Contains)) parts.Add(name);
        foreach (var vk in chord)
        {
            if (Generic.Any(g => g.Vks.Contains(vk))) continue;
            var name = KeyNames.Name(vk);
            if (name is null) return null;
            parts.Add(name);
        }
        return string.Join("+", parts);
    }
}
