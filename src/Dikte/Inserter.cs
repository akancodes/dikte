using System.Collections.Specialized;
using Dikte.Core;

namespace Dikte;

/// <summary>Puts text where the user is typing: the focused window of whatever app is in front.</summary>
static class Inserter
{
    const ushort VK_BACK = 0x08, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_INSERT = 0x2D, VK_V = 0x56;

    static readonly int[] HeldModifiers =
    {
        HotkeySpec.VkLControl, HotkeySpec.VkRControl, HotkeySpec.VkLShift, HotkeySpec.VkRShift,
        HotkeySpec.VkLMenu, HotkeySpec.VkRMenu, HotkeySpec.VkLWin, HotkeySpec.VkRWin,
    };

    public static async Task InsertAsync(string text, Settings s)
    {
        await HandsOffAsync();
        if (s.InsertMode == "type") Type(text);
        else await PasteAsync(text, s);
    }

    /// <summary>Ctrl+V pressed while Win is still down is Win+Ctrl+V; waits for the hands to come off the hotkey.</summary>
    static async Task HandsOffAsync()
    {
        for (int i = 0; i < 60 && HeldModifiers.Any(Native.IsPhysicallyDown); i++) await Task.Delay(25);
    }

    /// <summary>
    /// The text selected in the app in front, or null. Copies with Ctrl+Insert, not Ctrl+C: in a terminal with
    /// nothing selected Ctrl+C would interrupt the running program. The clipboard is given back afterwards.
    /// </summary>
    public static async Task<string?> CopySelectionAsync()
    {
        await HandsOffAsync();
        var saved = SavedClipboard.Take();
        try
        {
            Clipboard.Clear();
            Native.Send(Native.Key(VK_CONTROL, false), Native.Key(VK_INSERT, false), Native.Key(VK_INSERT, true), Native.Key(VK_CONTROL, true));
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(25);
                if (Clipboard.ContainsText()) return Clipboard.GetText();
            }
            return null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Log.Write($"Copying the selection failed: {e.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (saved is not null) saved.Restore();
                else Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }
    }

    /// <summary>"send": Enter after the text, once the app has taken the paste.</summary>
    public static async Task PressEnterAsync()
    {
        await HandsOffAsync();
        await Task.Delay(120);
        Native.Send(Native.Key(VK_RETURN, false), Native.Key(VK_RETURN, true));
    }

    /// <summary>"delete that": Backspace once per character of the last dictation.</summary>
    public static async Task DeleteBackAsync(int count)
    {
        await HandsOffAsync();
        var inputs = Enumerable.Range(0, count).SelectMany(_ => new[] { Native.Key(VK_BACK, false), Native.Key(VK_BACK, true) });
        foreach (var chunk in inputs.Chunk(200)) Native.Send(chunk);
    }

    static void Type(string text)
    {
        // A newline would be an Enter, which sends a half-finished prompt in a terminal.
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(Native.Unicode(c, up: false));
            inputs.Add(Native.Unicode(c, up: true));
        }
        // Large batches overflow some apps' input queues; send in slices.
        foreach (var chunk in inputs.Chunk(200)) Native.Send(chunk);
    }

    static async Task PasteAsync(string text, Settings s)
    {
        var saved = s.RestoreClipboard ? SavedClipboard.Take() : null;

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        // Keep dictations out of Win+V history and away from clipboard sync.
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);

        Native.Send(s.PasteShortcut switch
        {
            "shift+insert" => new[]
            {
                Native.Key(VK_SHIFT, false), Native.Key(VK_INSERT, false), Native.Key(VK_INSERT, true), Native.Key(VK_SHIFT, true),
            },
            "ctrl+shift+v" => new[]
            {
                Native.Key(VK_CONTROL, false), Native.Key(VK_SHIFT, false), Native.Key(VK_V, false),
                Native.Key(VK_V, true), Native.Key(VK_SHIFT, true), Native.Key(VK_CONTROL, true),
            },
            _ => new[] { Native.Key(VK_CONTROL, false), Native.Key(VK_V, false), Native.Key(VK_V, true), Native.Key(VK_CONTROL, true) },
        });

        if (saved is null) return;
        // The target reads the clipboard when it handles the keystroke, which can lag in a busy terminal.
        await Task.Delay(700);
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == text) saved.Restore();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Log.Write($"Clipboard restore skipped: {e.Message}");
        }
    }

    /// <summary>The common clipboard contents worth giving back: text, an image, or copied files.</summary>
    sealed class SavedClipboard
    {
        string? _text;
        Image? _image;
        StringCollection? _files;

        public static SavedClipboard? Take()
        {
            try
            {
                var saved = new SavedClipboard();
                if (Clipboard.ContainsFileDropList()) saved._files = Clipboard.GetFileDropList();
                else if (Clipboard.ContainsImage()) saved._image = Clipboard.GetImage();
                else if (Clipboard.ContainsText()) saved._text = Clipboard.GetText();
                else return null;
                return saved;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ThreadStateException)
            {
                return null;
            }
        }

        public void Restore()
        {
            if (_files is not null) Clipboard.SetFileDropList(_files);
            else if (_image is not null) Clipboard.SetImage(_image);
            else if (_text is not null) Clipboard.SetText(_text);
        }
    }
}
