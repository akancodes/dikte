using System.Runtime.InteropServices;

namespace Dikte;

/// <summary>
/// A global low-level keyboard hook. It runs on the thread that installed it (the UI thread,
/// which pumps messages) and must answer fast, so handlers only decide and post work.
/// </summary>
sealed class KeyboardHook : IDisposable
{
    /// <summary>Return true to swallow the key.</summary>
    public delegate bool KeyHandler(int vk, bool isDown);

    readonly KeyHandler _handler;
    readonly Native.LowLevelKeyboardProc _proc; // kept alive: the OS holds only a raw pointer to it
    IntPtr _hook;

    public KeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback;
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"Could not install the keyboard hook (error {Marshal.GetLastWin32Error()}).");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != Native.OwnInputTag)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                if (down || up)
                {
                    bool swallow;
                    try { swallow = _handler((int)info.vkCode, down); }
                    catch (Exception e) { Log.Write($"Hook handler failed: {e}"); swallow = false; }
                    if (swallow) return 1;
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
