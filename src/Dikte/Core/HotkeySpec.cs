namespace Dikte.Core;

/// <summary>
/// A hotkey such as "ctrl+win" or "rctrl" or "f8". Each part is a slot that any of
/// several virtual-key codes can fill: "ctrl" is satisfied by either Ctrl key.
/// </summary>
public sealed class HotkeySpec
{
    public const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    public const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3;
    public const int VkLMenu = 0xA4, VkRMenu = 0xA5, VkLWin = 0x5B, VkRWin = 0x5C;

    static readonly Dictionary<string, int[]> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = new[] { VkLControl, VkRControl, VkControl },
        ["lctrl"] = new[] { VkLControl },
        ["rctrl"] = new[] { VkRControl },
        ["win"] = new[] { VkLWin, VkRWin },
        ["lwin"] = new[] { VkLWin },
        ["rwin"] = new[] { VkRWin },
        ["alt"] = new[] { VkLMenu, VkRMenu, VkMenu },
        ["lalt"] = new[] { VkLMenu },
        ["ralt"] = new[] { VkRMenu },
        ["shift"] = new[] { VkLShift, VkRShift, VkShift },
        ["lshift"] = new[] { VkLShift },
        ["rshift"] = new[] { VkRShift },
    };

    static readonly HashSet<int> ModifierVks = new()
    {
        VkShift, VkControl, VkMenu, VkLShift, VkRShift, VkLControl, VkRControl, VkLMenu, VkRMenu, VkLWin, VkRWin,
    };

    static readonly HashSet<int> MenuOpeners = new() { VkLWin, VkRWin, VkLMenu, VkRMenu, VkMenu };

    readonly List<int[]> _slots;

    HotkeySpec(List<int[]> slots, string text)
    {
        _slots = slots;
        Text = text;
        NeedsMenuSuppression = slots.Any(s => s.Any(MenuOpeners.Contains));
    }

    public string Text { get; }

    /// <summary>Releasing Win or Alt alone opens the Start menu or a menu bar; a dummy key prevents it.</summary>
    public bool NeedsMenuSuppression { get; }

    public static HotkeySpec Parse(string text)
    {
        var slots = new List<int[]>();
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Modifiers.TryGetValue(raw, out var vks)) slots.Add(vks);
            else if (KeyNames.TryParse(raw, out var vk)) slots.Add(new[] { vk });
            else throw new FormatException($"Unknown key \"{raw}\" in hotkey \"{text}\".");
        }
        if (slots.Count == 0) throw new FormatException("The hotkey is empty.");
        return new HotkeySpec(slots, text);
    }

    public bool IsDown(IReadOnlySet<int> down) => _slots.All(slot => slot.Any(down.Contains));

    public bool Contains(int vk) => _slots.Any(slot => slot.Contains(vk));

    /// <summary>How many keys make it up: ctrl+mouse4 (2) is more specific than mouse4 (1).</summary>
    public int Size => _slots.Count;

    /// <summary>A non-modifier key of the hotkey (F8, CapsLock...), whose events must not reach the app.</summary>
    public bool IsPlainKey(int vk) => Contains(vk) && !ModifierVks.Contains(vk);

    public IEnumerable<int> AllKeys => _slots.SelectMany(s => s).Distinct();

    public bool UsesMouse => AllKeys.Any(KeyNames.IsMouse);

    public override string ToString() => Text;
}

public static class KeyNames
{
    /// <summary>Mouse buttons are virtual keys too, which lets GetAsyncKeyState check them like any key.</summary>
    public const int VkMButton = 0x04, VkXButton1 = 0x05, VkXButton2 = 0x06;

    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["enter"] = 0x0D,
        ["capslock"] = 0x14, ["pause"] = 0x13, ["scrolllock"] = 0x91, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["apps"] = 0x5D, ["menu"] = 0x5D,
        ["mouse3"] = VkMButton, ["middle"] = VkMButton, ["mouse4"] = VkXButton1, ["mouse5"] = VkXButton2,
    };

    /// <summary>Canonical names, for writing a captured key back into settings.</summary>
    static readonly Dictionary<int, string> Names = new()
    {
        [0x20] = "space", [0x09] = "tab", [0x0D] = "enter", [0x14] = "capslock", [0x13] = "pause", [0x91] = "scrolllock",
        [0x2D] = "insert", [0x24] = "home", [0x23] = "end", [0x21] = "pageup", [0x22] = "pagedown", [0x5D] = "apps",
        [VkMButton] = "mouse3", [VkXButton1] = "mouse4", [VkXButton2] = "mouse5",
        [HotkeySpec.VkLControl] = "lctrl", [HotkeySpec.VkRControl] = "rctrl", [HotkeySpec.VkControl] = "ctrl",
        [HotkeySpec.VkLMenu] = "lalt", [HotkeySpec.VkRMenu] = "ralt", [HotkeySpec.VkMenu] = "alt",
        [HotkeySpec.VkLShift] = "lshift", [HotkeySpec.VkRShift] = "rshift", [HotkeySpec.VkShift] = "shift",
        [HotkeySpec.VkLWin] = "lwin", [HotkeySpec.VkRWin] = "rwin",
    };

    public static bool IsMouse(int vk) => vk is VkMButton or VkXButton1 or VkXButton2;

    public static string? Name(int vk)
    {
        if (Names.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x70 and <= 0x87) return "f" + (vk - 0x70 + 1);
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString().ToLowerInvariant();
        return null;
    }

    public static bool TryParse(string name, out int vk)
    {
        if (Named.TryGetValue(name, out vk)) return true;
        if (name.Length >= 2 && (name[0] == 'f' || name[0] == 'F') && int.TryParse(name[1..], out var n) && n is >= 1 and <= 24)
        {
            vk = 0x70 + n - 1;
            return true;
        }
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            vk = char.ToUpperInvariant(name[0]);
            return true;
        }
        vk = 0;
        return false;
    }
}
