using Dikte.Core;
using Xunit;

public class HotkeyMachineTests
{
    const int LCtrl = HotkeySpec.VkLControl, RCtrl = HotkeySpec.VkRControl, LWin = HotkeySpec.VkLWin;
    const int Space = 0x20, Esc = 0x1B, D = 0x44, F8 = 0x77, A = 0x41;

    static HotkeyMachine CtrlWin() => new(HotkeySpec.Parse("ctrl+win"), Space);

    [Fact]
    public void Hold_then_release_records_and_sends()
    {
        var m = CtrlWin();
        Assert.Equal(HotkeyCommand.None, m.Feed(LCtrl, true).Command);
        var start = m.Feed(LWin, true);
        Assert.Equal(HotkeyCommand.Start, start.Command);
        Assert.True(start.SuppressMenu);
        Assert.False(start.Swallow);
        Assert.True(m.IsRecording);
        Assert.Equal(HotkeyCommand.None, m.Feed(LWin, true).Command); // auto-repeat
        Assert.Equal(HotkeyCommand.StopAndSend, m.Feed(LWin, false).Command);
        Assert.False(m.IsRecording);
        Assert.Equal(HotkeyCommand.None, m.Feed(LCtrl, false).Command);
    }

    [Fact]
    public void Either_order_and_either_side_starts()
    {
        var m = CtrlWin();
        m.Feed(LWin, true);
        Assert.Equal(HotkeyCommand.Start, m.Feed(RCtrl, true).Command);
        Assert.Equal(HotkeyCommand.StopAndSend, m.Feed(RCtrl, false).Command);
    }

    [Fact]
    public void A_shortcut_that_starts_with_the_hotkey_cancels_and_passes_through()
    {
        var m = CtrlWin();
        m.Feed(LCtrl, true);
        m.Feed(LWin, true);
        var r = m.Feed(D, true);
        Assert.Equal(HotkeyCommand.Cancel, r.Command);
        Assert.False(r.Swallow);
        Assert.False(m.IsRecording);
        // Still holding Ctrl+Win: no restart until the combo is released and pressed again.
        Assert.Equal(HotkeyCommand.None, m.Feed(D, false).Command);
        Assert.Equal(HotkeyCommand.None, m.Feed(LWin, false).Command);
        Assert.Equal(HotkeyCommand.Start, m.Feed(LWin, true).Command);
    }

    [Fact]
    public void Escape_cancels_and_is_swallowed_both_ways()
    {
        var m = CtrlWin();
        m.Feed(LCtrl, true);
        m.Feed(LWin, true);
        var down = m.Feed(Esc, true);
        Assert.Equal(HotkeyCommand.Cancel, down.Command);
        Assert.True(down.Swallow);
        Assert.True(m.Feed(Esc, false).Swallow);
        Assert.Equal(HotkeyCommand.None, m.Feed(LWin, false).Command);
    }

    [Fact]
    public void Space_locks_hands_free_and_the_next_press_sends()
    {
        var m = CtrlWin();
        m.Feed(LCtrl, true);
        m.Feed(LWin, true);
        var lockDown = m.Feed(Space, true);
        Assert.Equal(HotkeyCommand.Lock, lockDown.Command);
        Assert.True(lockDown.Swallow);
        Assert.True(m.Feed(Space, false).Swallow);
        Assert.Equal(HotkeyCommand.None, m.Feed(LWin, false).Command);
        Assert.Equal(HotkeyCommand.None, m.Feed(LCtrl, false).Command);
        Assert.True(m.IsRecording);
        Assert.True(m.IsLocked);

        // Typing while it listens goes through.
        Assert.Equal(HotkeyResult.Pass, m.Feed(A, true));
        Assert.Equal(HotkeyResult.Pass, m.Feed(A, false));

        m.Feed(LCtrl, true);
        var stop = m.Feed(LWin, true);
        Assert.Equal(HotkeyCommand.StopAndSend, stop.Command);
        Assert.True(stop.SuppressMenu);
        Assert.False(m.IsRecording);
        Assert.Equal(HotkeyCommand.None, m.Feed(LWin, false).Command);
        Assert.Equal(HotkeyCommand.None, m.Feed(LCtrl, false).Command);
    }

    [Fact]
    public void Escape_cancels_hands_free()
    {
        var m = CtrlWin();
        m.Feed(LCtrl, true);
        m.Feed(LWin, true);
        m.Feed(Space, true);
        m.Feed(Space, false);
        m.Feed(LWin, false);
        m.Feed(LCtrl, false);
        Assert.Equal(HotkeyCommand.Cancel, m.Feed(Esc, true).Command);
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void A_plain_key_hotkey_is_swallowed()
    {
        var m = new HotkeyMachine(HotkeySpec.Parse("f8"), Space);
        var down = m.Feed(F8, true);
        Assert.Equal(HotkeyCommand.Start, down.Command);
        Assert.True(down.Swallow);
        Assert.False(down.SuppressMenu);
        Assert.True(m.Feed(F8, true).Swallow); // auto-repeat
        var up = m.Feed(F8, false);
        Assert.Equal(HotkeyCommand.StopAndSend, up.Command);
        Assert.True(up.Swallow);
    }

    [Fact]
    public void Lost_key_up_is_recovered_by_resync()
    {
        var m = CtrlWin();
        m.Feed(LCtrl, true);
        m.Feed(LWin, true);
        // Win+L: the key-ups never arrive.
        Assert.Equal(HotkeyCommand.StopAndSend, m.Resync(_ => false));
        Assert.False(m.IsRecording);
        m.Feed(LCtrl, true);
        Assert.Equal(HotkeyCommand.Start, m.Feed(LWin, true).Command);
    }

    [Fact]
    public void Resync_keeps_a_real_hold()
    {
        var m = CtrlWin();
        m.Feed(LCtrl, true);
        m.Feed(LWin, true);
        Assert.Equal(HotkeyCommand.None, m.Resync(vk => vk is LCtrl or LWin));
        Assert.True(m.IsRecording);
    }

    [Fact]
    public void Tray_start_is_stopped_by_the_hotkey()
    {
        var m = CtrlWin();
        m.EnterLocked();
        m.Feed(LCtrl, true);
        Assert.Equal(HotkeyCommand.StopAndSend, m.Feed(LWin, true).Command);
    }

    [Theory]
    [InlineData("ctrl+win")]
    [InlineData("rctrl")]
    [InlineData("F8")]
    [InlineData("ctrl + shift + space")]
    [InlineData("ralt+f12")]
    public void Parses_hotkeys(string text) => HotkeySpec.Parse(text);

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+banana")]
    [InlineData("f25")]
    public void Rejects_bad_hotkeys(string text) => Assert.Throws<FormatException>(() => HotkeySpec.Parse(text));
}
