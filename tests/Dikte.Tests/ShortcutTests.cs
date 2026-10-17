using Dikte.Core;
using Xunit;

public class ShortcutTests
{
    const int LCtrl = HotkeySpec.VkLControl, RCtrl = HotkeySpec.VkRControl, LWin = HotkeySpec.VkLWin, LShift = HotkeySpec.VkLShift;
    const int Mouse4 = KeyNames.VkXButton1, Mouse5 = KeyNames.VkXButton2, Space = 0x20, Esc = 0x1B, F9 = 0x78, Oem1 = 0xBA;

    static (CaptureOutcome outcome, string? result) Capture(params (int vk, bool down)[] events)
    {
        var c = new KeyCapture();
        var outcome = CaptureOutcome.Waiting;
        foreach (var (vk, down) in events)
        {
            outcome = c.Feed(vk, down);
            if (outcome != CaptureOutcome.Waiting) break;
        }
        return (outcome, c.Result);
    }

    [Fact]
    public void Captures_a_mouse_button_on_press() =>
        Assert.Equal((CaptureOutcome.Done, "mouse4"), Capture((Mouse4, true)));

    [Fact]
    public void Captures_a_combination_when_everything_is_let_go()
    {
        Assert.Equal((CaptureOutcome.Done, "ctrl+win"), Capture((LCtrl, true), (LWin, true), (LWin, false), (LCtrl, false)));
        Assert.Equal((CaptureOutcome.Done, "ctrl+shift+space"), Capture((LShift, true), (LCtrl, true), (Space, true), (Space, false), (LCtrl, false), (LShift, false)));
    }

    [Fact]
    public void A_lone_modifier_keeps_its_side() =>
        Assert.Equal((CaptureOutcome.Done, "rctrl"), Capture((RCtrl, true), (RCtrl, true), (RCtrl, false)));

    [Fact]
    public void Modifiers_with_a_mouse_button() =>
        Assert.Equal((CaptureOutcome.Done, "ctrl+mouse5"), Capture((LCtrl, true), (Mouse5, true)));

    [Fact]
    public void Escape_alone_cancels() =>
        Assert.Equal(CaptureOutcome.Cancelled, Capture((Esc, true)).outcome);

    [Fact]
    public void Keys_without_a_name_are_refused() =>
        Assert.Equal(CaptureOutcome.Unusable, Capture((LCtrl, true), (Oem1, true), (Oem1, false), (LCtrl, false)).outcome);

    [Fact]
    public void Every_captured_name_parses_back()
    {
        foreach (var name in new[] { "mouse4", "mouse5", "mouse3", "ctrl+win", "rctrl", "ctrl+shift+space", "f9", "ctrl+mouse5" })
            Assert.Equal(name, HotkeySpec.Parse(name).Text);
        Assert.True(HotkeySpec.Parse("mouse4").UsesMouse);
        Assert.False(HotkeySpec.Parse("ctrl+win").UsesMouse);
    }

    [Fact]
    public void Holding_a_mouse_button_records_and_swallows_it()
    {
        var m = new HotkeyMachine(HotkeySpec.Parse("mouse4"), Space);
        var down = m.Feed(Mouse4, true);
        Assert.Equal(HotkeyCommand.Start, down.Command);
        Assert.True(down.Swallow);              // the browser must not go back a page
        Assert.False(down.SuppressMenu);
        var up = m.Feed(Mouse4, false);
        Assert.Equal(HotkeyCommand.StopAndSend, up.Command);
        Assert.True(up.Swallow);
    }

    [Fact]
    public void Toggle_fires_once_per_press_and_swallows_its_button()
    {
        var t = new ToggleKey(HotkeySpec.Parse("mouse5"));
        var first = t.Feed(Mouse5, true);
        Assert.Equal(HotkeyCommand.Toggle, first.Command);
        Assert.True(first.Swallow);
        Assert.True(t.Feed(Mouse5, false).Swallow);
        Assert.Equal(HotkeyCommand.None, t.Feed(LCtrl, true).Command);
        Assert.False(t.Feed(LCtrl, true).Swallow);
        t.Feed(LCtrl, false);
        Assert.Equal(HotkeyCommand.Toggle, t.Feed(Mouse5, true).Command);
    }

    [Fact]
    public void Toggle_on_a_combination_fires_when_it_completes()
    {
        var t = new ToggleKey(HotkeySpec.Parse("ctrl+f9"));
        Assert.Equal(HotkeyCommand.None, t.Feed(LCtrl, true).Command);
        var r = t.Feed(F9, true);
        Assert.Equal(HotkeyCommand.Toggle, r.Command);
        Assert.True(r.Swallow);
        Assert.Equal(HotkeyCommand.None, t.Feed(F9, true).Command); // auto-repeat
    }

    [Fact]
    public void Settings_refuse_the_same_shortcut_twice()
    {
        Assert.Throws<FormatException>(new Settings { Hotkey = "mouse4", ToggleHotkey = "mouse4" }.Validate);
        new Settings { Hotkey = "mouse4", ToggleHotkey = "mouse5" }.Validate();
        new Settings { Hotkey = "mouse4", ToggleHotkey = "" }.Validate();
        Assert.Throws<FormatException>(new Settings { ToggleHotkey = "mouse9" }.Validate);
    }
}
