using Dikte.Core;
using Xunit;

public class VoiceCommandsTests
{
    [Theory]
    [InlineData("Fix the failing test. Send.", "Fix the failing test.", true)]
    [InlineData("Fix the failing test, send.", "Fix the failing test", true)]
    [InlineData("Testleri düzelt. Gönder.", "Testleri düzelt.", true)]
    [InlineData("Send.", "", true)]
    [InlineData("Can you send", "Can you send", false)]           // part of the sentence, not a command
    [InlineData("I will send it tomorrow.", "I will send it tomorrow.", false)]
    [InlineData("Hello there.", "Hello there.", false)]
    public void Send_at_the_end_presses_enter(string heard, string text, bool enter)
    {
        var s = VoiceCommands.Apply(heard);
        Assert.Equal(text, s.Text);
        Assert.Equal(enter, s.PressEnter);
        Assert.False(s.DeleteLast);
    }

    [Theory]
    [InlineData("First point. New line. Second point.", "First point.\nSecond point.")]
    [InlineData("first point, new line, second point", "first point\nsecond point")]
    [InlineData("Birinci madde. Yeni satır. İkinci madde.", "Birinci madde.\nİkinci madde.")]
    [InlineData("Intro. New paragraph. Body.", "Intro.\n\nBody.")]
    public void Line_breaks_are_spoken(string heard, string text) => Assert.Equal(text, VoiceCommands.Apply(heard).Text);

    [Theory]
    [InlineData("Delete that.")]
    [InlineData("scratch that")]
    [InlineData("Sil.")]
    [InlineData("Geri al")]
    public void Delete_that_alone_takes_the_last_one_back(string heard)
    {
        var s = VoiceCommands.Apply(heard);
        Assert.True(s.DeleteLast);
        Assert.Equal("", s.Text);
    }

    [Fact]
    public void Delete_inside_a_sentence_is_just_words()
    {
        var s = VoiceCommands.Apply("Please delete that file.");
        Assert.False(s.DeleteLast);
        Assert.Equal("Please delete that file.", s.Text);
    }

    [Fact]
    public void Line_break_and_send_together()
    {
        var s = VoiceCommands.Apply("Title. New line. Body text. Send.");
        Assert.Equal("Title.\nBody text.", s.Text);
        Assert.True(s.PressEnter);
    }
}

public class HoldKeysTests
{
    const int Mouse4 = KeyNames.VkXButton1, LCtrl = HotkeySpec.VkLControl, Space = 0x20;

    static HoldKeys Keys() => new(new[]
    {
        (DictationMode.Dictate, HotkeySpec.Parse("mouse4")),
        (DictationMode.Translate, HotkeySpec.Parse("ctrl+mouse4")),
        (DictationMode.Edit, HotkeySpec.Parse("f9")),
    }, Space);

    [Fact]
    public void Each_shortcut_starts_its_own_mode()
    {
        var k = Keys();
        var r = k.Feed(0x78, true); // F9
        Assert.Equal((HotkeyCommand.Start, DictationMode.Edit), Assert.Single(r.Commands));
        Assert.True(r.Swallow);
        Assert.Equal((HotkeyCommand.StopAndSend, DictationMode.Edit), Assert.Single(k.Feed(0x78, false).Commands));
    }

    [Fact]
    public void The_more_specific_shortcut_wins()
    {
        var k = Keys();
        k.Feed(LCtrl, true);
        var r = k.Feed(Mouse4, true);
        Assert.Equal((HotkeyCommand.Start, DictationMode.Translate), Assert.Single(r.Commands));
        Assert.True(r.Swallow);
        var up = k.Feed(Mouse4, false);
        Assert.Equal((HotkeyCommand.StopAndSend, DictationMode.Translate), Assert.Single(up.Commands));
        Assert.True(up.Swallow);
        Assert.False(k.IsRecording);
    }

    [Fact]
    public void Plain_shortcut_alone_dictates()
    {
        var k = Keys();
        Assert.Equal((HotkeyCommand.Start, DictationMode.Dictate), Assert.Single(k.Feed(Mouse4, true).Commands));
    }

    [Fact]
    public void Adding_ctrl_while_holding_switches_to_the_bigger_shortcut()
    {
        var k = Keys();
        k.Feed(Mouse4, true);
        var r = k.Feed(LCtrl, true);
        Assert.Equal(new[] { (HotkeyCommand.Cancel, DictationMode.Dictate), (HotkeyCommand.Start, DictationMode.Translate) }, r.Commands);
    }

    [Fact]
    public void A_hands_free_recording_keeps_the_microphone()
    {
        var k = Keys();
        k.Feed(Mouse4, true);
        k.Feed(Space, true); // lock: hands-free
        k.Feed(Space, false);
        k.Feed(Mouse4, false);
        Assert.True(k.IsLocked);
        Assert.Empty(k.Feed(0x78, true).Commands); // F9 does not start an edit on top
        k.Feed(0x78, false);
        Assert.Equal((HotkeyCommand.StopAndSend, DictationMode.Dictate), Assert.Single(k.Feed(Mouse4, true).Commands));
    }
}

public class VoiceToolSettingsTests
{
    [Fact]
    public void Every_shortcut_must_be_different()
    {
        new Settings { Hotkey = "mouse4", ToggleHotkey = "mouse5", TranslateHotkey = "ctrl+mouse4", EditHotkey = "f9", RepasteHotkey = "alt+shift+v" }.Validate();
        Assert.Throws<FormatException>(new Settings { Hotkey = "mouse4", EditHotkey = "mouse4" }.Validate);
        Assert.Throws<FormatException>(new Settings { ToggleHotkey = "f9", RepasteHotkey = "f9" }.Validate);
        Assert.Throws<FormatException>(new Settings { TranslateTo = " " }.Validate);
    }
}
