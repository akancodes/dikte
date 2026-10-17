using System.Runtime.InteropServices;

namespace Dikte;

/// <summary>Return true to swallow the key or button.</summary>
delegate bool KeyHandler(int vk, bool isDown);

/// <summary>
/// A global low-level keyboard hook. It runs on the thread that installed it (the UI thread,
/// which pumps messages) and must answer fast, so handlers only decide and post work.
/// </summary>
sealed class KeyboardHook : IDisposable
{
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
                if ((down || up) && Handle(_handler, (int)info.vkCode, down)) return 1;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    internal static bool Handle(KeyHandler handler, int vk, bool down)
    {
        try { return handler(vk, down); }
        catch (Exception e)
        {
            Log.Write($"Hook handler failed: {e}");
            return false;
        }
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

/// <summary>
/// A global low-level mouse hook for the middle and side buttons, reported as their virtual keys
/// (0x04 middle, 0x05 back / "mouse 4", 0x06 forward / "mouse 5"). Every mouse move passes through
/// a low-level mouse hook, so it is installed only while a shortcut uses the mouse.
/// </summary>
sealed class MouseHook : IDisposable
{
    readonly KeyHandler _handler;
    readonly Native.LowLevelKeyboardProc _proc; // same signature as the keyboard one
    IntPtr _hook;

    public MouseHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback;
        _hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"Could not install the mouse hook (error {Marshal.GetLastWin32Error()}).");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP or Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                int vk = msg is Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP ? Core.KeyNames.VkMButton
                    : (info.mouseData >> 16) == 1 ? Core.KeyNames.VkXButton1 : Core.KeyNames.VkXButton2;
                bool down = msg is Native.WM_XBUTTONDOWN or Native.WM_MBUTTONDOWN;
                if (KeyboardHook.Handle(_handler, vk, down)) return 1;
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
