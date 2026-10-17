using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dikte.Core;

public sealed class Settings
{
    /// <summary>Hold to talk. Examples: "ctrl+win", "rctrl", "f8", "ctrl+shift+space".</summary>
    public string Hotkey { get; set; } = "ctrl+win";

    /// <summary>Tap once to start listening hands-free, tap again to send. Empty: not used. Example: "mouse5".</summary>
    public string ToggleHotkey { get; set; } = "";

    /// <summary>Pressed while holding the hotkey: keeps listening hands-free until the hotkey is pressed again.</summary>
    public string HandsFreeKey { get; set; } = "space";

    /// <summary>Any OpenAI-compatible server: https://api.openai.com/v1, https://api.groq.com/openai/v1, a local whisper server...</summary>
    public string ApiBase { get; set; } = "https://api.openai.com/v1";

    /// <summary>gpt-transcribe, gpt-4o-transcribe, gpt-4o-mini-transcribe, whisper-1...</summary>
    public string Model { get; set; } = "gpt-transcribe";

    /// <summary>
    /// ISO-639-1 codes of the languages you speak ("tr", "en"). None: the model detects. One: it is pinned.
    /// Several: the model is told you mix them, so it writes each word in the language it was spoken.
    /// </summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Names and terms the model should spell your way.</summary>
    public string Vocabulary { get; set; } = "Claude Code, Claude, commit, pull request, deploy, npm, TypeScript, Docker";

    /// <summary>"paste" puts the text on the clipboard and presses the paste shortcut; "type" types it key by key.</summary>
    public string InsertMode { get; set; } = "paste";

    /// <summary>"ctrl+v", "ctrl+shift+v" or "shift+insert".</summary>
    public string PasteShortcut { get; set; } = "ctrl+v";

    /// <summary>Put back what was on the clipboard after pasting.</summary>
    public bool RestoreClipboard { get; set; } = true;

    /// <summary>Add a space after the text, so consecutive dictations do not run together.</summary>
    public bool AppendSpace { get; set; } = true;

    /// <summary>Send the transcript through a chat model that removes filler words and fixes punctuation.</summary>
    public bool Cleanup { get; set; } = false;

    public string CleanupModel { get; set; } = "gpt-4.1-mini";

    /// <summary>Recording device name as Windows lists it; empty uses the default microphone.</summary>
    public string Microphone { get; set; } = "";

    public bool Sounds { get; set; } = true;

    /// <summary>Turn other apps (music, videos) down while recording, so the mic hears you and you hear yourself think.</summary>
    public bool QuietOtherAudio { get; set; } = true;

    /// <summary>What other apps are turned down to while recording: 0 mutes them, 0.15 leaves 15% of their volume.</summary>
    public double QuietLevel { get; set; } = 0.15;

    public int MaxMinutes { get; set; } = 10;

    public int HistorySize { get; set; } = 20;

    /// <summary>Installs new releases from GitHub on its own, while nothing is being dictated.</summary>
    public bool AutoUpdate { get; set; } = true;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads the file, or writes the defaults when it does not exist yet.</summary>
    public static Settings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new Settings();
            fresh.Save(path);
            return fresh;
        }
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
        settings.Validate();
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Throws FormatException with a message fit for the user.</summary>
    public void Validate()
    {
        HotkeySpec.Parse(Hotkey);
        if (!string.IsNullOrWhiteSpace(ToggleHotkey))
        {
            HotkeySpec.Parse(ToggleHotkey);
            if (string.Equals(ToggleHotkey.Replace(" ", ""), Hotkey.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                throw new FormatException("The tap shortcut must differ from the hold shortcut.");
        }
        if (!KeyNames.TryParse(HandsFreeKey, out _)) throw new FormatException($"Unknown handsFreeKey \"{HandsFreeKey}\".");
        if (InsertMode is not ("paste" or "type")) throw new FormatException("insertMode must be \"paste\" or \"type\".");
        if (PasteShortcut is not ("ctrl+v" or "ctrl+shift+v" or "shift+insert"))
            throw new FormatException("pasteShortcut must be \"ctrl+v\", \"ctrl+shift+v\" or \"shift+insert\".");
        if (!Uri.TryCreate(ApiBase, UriKind.Absolute, out _)) throw new FormatException($"apiBase \"{ApiBase}\" is not a URL.");
        if (string.IsNullOrWhiteSpace(Model)) throw new FormatException("model is empty.");
        if (Languages.Any(l => l.Length is < 2 or > 3 || !l.All(char.IsAsciiLetterLower)))
            throw new FormatException("languages must be ISO codes like \"tr\" and \"en\".");
        if (QuietLevel is < 0 or > 1) throw new FormatException("quietLevel must be between 0 and 1.");
        if (MaxMinutes is < 1 or > 12) throw new FormatException("maxMinutes must be between 1 and 12 (the API takes files up to 25 MB).");
    }
}
