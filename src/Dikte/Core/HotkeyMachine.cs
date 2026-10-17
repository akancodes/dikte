namespace Dikte.Core;

public enum HotkeyCommand { None, Start, Lock, StopAndSend, Cancel, Toggle }

public readonly record struct HotkeyResult(HotkeyCommand Command, bool Swallow, bool SuppressMenu)
{
    public static readonly HotkeyResult Pass = new(HotkeyCommand.None, false, false);
}

/// <summary>
/// Push-to-talk state machine fed with raw key events from a low-level keyboard hook.
///
/// Holding the hotkey records; releasing it sends. Pressing the lock key while holding
/// switches to hands-free, and the next press of the hotkey sends. Escape cancels.
/// Any other key pressed while holding means the user was typing a shortcut that
/// happens to start with the hotkey (Ctrl+Win+D, Ctrl+Win+Left...), so the recording
/// is cancelled and the key goes through untouched.
/// </summary>
public sealed class HotkeyMachine
{
    public const int VkEscape = 0x1B;

    enum State { Idle, Holding, Locked }

    readonly HotkeySpec _spec;
    readonly int _lockVk;
    readonly HashSet<int> _down = new();
    readonly HashSet<int> _swallowed = new();
    State _state = State.Idle;

    public HotkeyMachine(HotkeySpec spec, int lockVk)
    {
        _spec = spec;
        _lockVk = lockVk;
    }

    public bool IsRecording => _state != State.Idle;
    public bool IsLocked => _state == State.Locked;

    public HotkeyResult Feed(int vk, bool isDown)
    {
        return isDown ? KeyDown(vk) : KeyUp(vk);
    }

    HotkeyResult KeyDown(int vk)
    {
        bool wasCombo = _spec.IsDown(_down);
        bool repeat = !_down.Add(vk);
        bool nowCombo = _spec.IsDown(_down);
        bool hotkeyKey = _spec.Contains(vk);

        switch (_state)
        {
            case State.Idle:
                if (!wasCombo && nowCombo)
                {
                    _state = State.Holding;
                    return Result(HotkeyCommand.Start, vk, swallow: _spec.IsPlainKey(vk), suppressMenu: _spec.NeedsMenuSuppression);
                }
                if (hotkeyKey && nowCombo && _spec.IsPlainKey(vk))
                    return Result(HotkeyCommand.None, vk, swallow: true);
                return HotkeyResult.Pass;

            case State.Holding:
                if (hotkeyKey || repeat)
                    return Result(HotkeyCommand.None, vk, swallow: _swallowed.Contains(vk) || _spec.IsPlainKey(vk));
                if (vk == _lockVk)
                {
                    _state = State.Locked;
                    return Result(HotkeyCommand.Lock, vk, swallow: true);
                }
                _state = State.Idle;
                if (vk == VkEscape)
                    return Result(HotkeyCommand.Cancel, vk, swallow: true);
                return new HotkeyResult(HotkeyCommand.Cancel, false, false);

            case State.Locked:
                if (vk == VkEscape && !repeat)
                {
                    _state = State.Idle;
                    return Result(HotkeyCommand.Cancel, vk, swallow: true);
                }
                if (!wasCombo && nowCombo)
                {
                    _state = State.Idle;
                    return Result(HotkeyCommand.StopAndSend, vk, swallow: _spec.IsPlainKey(vk), suppressMenu: _spec.NeedsMenuSuppression);
                }
                if (repeat && _swallowed.Contains(vk))
                    return new HotkeyResult(HotkeyCommand.None, true, false);
                // Hands-free: the user may keep working while it listens.
                return HotkeyResult.Pass;
        }
        return HotkeyResult.Pass;
    }

    HotkeyResult KeyUp(int vk)
    {
        bool wasCombo = _spec.IsDown(_down);
        _down.Remove(vk);
        bool nowCombo = _spec.IsDown(_down);
        bool swallow = _swallowed.Remove(vk);

        if (_state == State.Holding && wasCombo && !nowCombo)
        {
            _state = State.Idle;
            return new HotkeyResult(HotkeyCommand.StopAndSend, swallow, false);
        }
        return new HotkeyResult(HotkeyCommand.None, swallow, false);
    }

    HotkeyResult Result(HotkeyCommand command, int vk, bool swallow, bool suppressMenu = false)
    {
        if (swallow) _swallowed.Add(vk);
        return new HotkeyResult(command, swallow, suppressMenu);
    }

    /// <summary>
    /// Drops keys the hook thinks are down but physically are not. Key-ups get lost
    /// when the session locks (Win+L) or a UAC prompt takes the desktop mid-hold.
    /// Returns StopAndSend when that ends a hold.
    /// </summary>
    public HotkeyCommand Resync(Func<int, bool> isPhysicallyDown)
    {
        bool wasCombo = _spec.IsDown(_down);
        _down.RemoveWhere(vk => !isPhysicallyDown(vk));
        _swallowed.RemoveWhere(vk => !_down.Contains(vk));
        if (_state == State.Holding && wasCombo && !_spec.IsDown(_down))
        {
            _state = State.Idle;
            return HotkeyCommand.StopAndSend;
        }
        return HotkeyCommand.None;
    }

    /// <summary>Starts a hands-free recording without the keyboard (tray menu).</summary>
    public void EnterLocked() => _state = State.Locked;

    public void Reset() => _state = State.Idle;
}
