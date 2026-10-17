namespace Dikte.Core;

/// <summary>
/// The tap-to-start, tap-to-send key: each fresh press is one Toggle. Its own keys
/// (a mouse button, F9...) are swallowed so the app underneath never sees them.
/// </summary>
public sealed class ToggleKey(HotkeySpec spec)
{
    readonly HashSet<int> _down = new();
    readonly HashSet<int> _swallowed = new();

    public HotkeyResult Feed(int vk, bool isDown)
    {
        if (!isDown)
        {
            _down.Remove(vk);
            return new HotkeyResult(HotkeyCommand.None, _swallowed.Remove(vk), false);
        }
        bool was = spec.IsDown(_down);
        _down.Add(vk);
        bool now = spec.IsDown(_down);
        bool swallow = spec.IsPlainKey(vk) && now;
        if (swallow) _swallowed.Add(vk);
        if (!was && now) return new HotkeyResult(HotkeyCommand.Toggle, swallow, spec.NeedsMenuSuppression);
        return new HotkeyResult(HotkeyCommand.None, swallow, false);
    }

    /// <summary>Swallowed keys are kept: their state cannot be read (see HotkeyMachine.Resync).</summary>
    public void Resync(Func<int, bool> isPhysicallyDown)
    {
        _down.RemoveWhere(vk => !_swallowed.Contains(vk) && !isPhysicallyDown(vk));
        _swallowed.RemoveWhere(vk => !_down.Contains(vk));
    }
}
