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

    /// <summary>Hold to dictate in <see cref="TranslateTo"/> whatever language is spoken. Empty: none.</summary>
    public string TranslateHotkey { get; set; } = "";

    /// <summary>Hold, say what to do with the selected text ("make it shorter"), let go: the selection is replaced.</summary>
    public string EditHotkey { get; set; } = "";

    /// <summary>Tap to paste the last dictation again. Empty: none.</summary>
    public string RepasteHotkey { get; set; } = "";

    public string TranslateTo { get; set; } = "English";

    /// <summary>"new line", "send" at the end, "delete that" alone (and their Turkish twins) act instead of being written.</summary>
    public bool VoiceCommands { get; set; } = true;

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

    /// <summary>Before 0.7 the only mode: read once to pick the first <see cref="ActiveMode"/>, then unused.</summary>
    public bool Cleanup { get; set; } = false;

    /// <summary>The ways of writing what was said; see <see cref="Mode"/>.</summary>
    public List<Mode> Modes { get; set; } = Core.Modes.Defaults();

    /// <summary>The mode used when no shortcut or app picks one: a mode id, or "plain" for the words as heard.</summary>
    public string? ActiveMode { get; set; }

    /// <summary>Tap to switch the active mode to the next one. Empty: none.</summary>
    public string NextModeHotkey { get; set; } = "";

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

    /// <summary>
    /// Call apps meeting notes listen to, by process name ("Discord"); a process whose name starts with one counts
    /// too (DiscordPTB, DiscordCanary). A call is noticed when one of them starts using a microphone.
    /// </summary>
    public List<string> MeetingApps { get; set; } = new() { "Discord" };

    /// <summary>When a call starts in one of <see cref="MeetingApps"/>, offer to take notes.</summary>
    public bool MeetingOffer { get; set; } = true;

    /// <summary>Tap to start meeting notes, tap again to stop and write them. Empty: none.</summary>
    public string MeetingHotkey { get; set; } = "";

    /// <summary>Where meeting notes are saved, one Markdown file each. Empty: "Dikte Meetings" in Documents.</summary>
    public string MeetingsFolder { get; set; } = "";

    /// <summary>Chat model that writes the notes from the transcript.</summary>
    public string MeetingModel { get; set; } = "gpt-6-luna";

    /// <summary>Your name in meeting transcripts. Empty: "Me".</summary>
    public string YourName { get; set; } = "";

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
            fresh.Normalize();
            fresh.Save(path);
            return fresh;
        }
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
        settings.Normalize();
        settings.Validate();
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// Fills in what an older or hand-edited file leaves out: the first active mode comes from the old
    /// clean-up switch, and an active mode that no longer exists falls back to the words as heard.
    /// </summary>
    public void Normalize()
    {
        Modes ??= new();
        foreach (var m in Modes)
        {
            m.Apps ??= new();
            m.Name = m.Name?.Trim() ?? "";
            m.Instruction = m.Instruction?.Trim() ?? "";
            m.Model = m.Model?.Trim() ?? "";
            m.Hotkey = m.Hotkey?.Trim() ?? "";
            m.Apps = m.Apps.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(m.Id)) m.Id = Core.Modes.NewId(m.Name, Modes.Select(x => x.Id));
        }
        MeetingApps = (MeetingApps ?? new()).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MeetingHotkey = MeetingHotkey?.Trim() ?? "";
        MeetingsFolder = MeetingsFolder?.Trim() ?? "";
        MeetingModel = MeetingModel?.Trim() ?? "";
        YourName = YourName?.Trim() ?? "";
        ActiveMode ??= Cleanup && Modes.Any(m => m.Id == "clean") ? "clean" : Core.Modes.Plain;
        if (ActiveMode != Core.Modes.Plain && Modes.All(m => m.Id != ActiveMode)) ActiveMode = Core.Modes.Plain;
    }

    /// <summary>Throws FormatException with a message fit for the user.</summary>
    public void Validate()
    {
        HotkeySpec.Parse(Hotkey);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Hotkey.Replace(" ", "")] = "Hold to talk" };
        var ids = new HashSet<string>();
        foreach (var m in Modes)
        {
            if (m.Id == Core.Modes.Plain) throw new FormatException($"\"{Core.Modes.Plain}\" cannot be a mode's id.");
            if (!ids.Add(m.Id)) throw new FormatException($"Two modes share the id \"{m.Id}\".");
            if (m.Name.Length == 0) throw new FormatException("Every mode needs a name.");
            if (m.Instruction.Length == 0) throw new FormatException($"Tell the {m.Name} mode what to do with your words.");
        }
        var shortcuts = new List<(string, string)> { (ToggleHotkey, "Tap to talk"), (TranslateHotkey, $"Write in {TranslateTo}"), (EditHotkey, "Edit the selection"), (RepasteHotkey, "Paste again"), (NextModeHotkey, "Next mode"), (MeetingHotkey, "Meeting notes") };
        shortcuts.AddRange(Modes.Select(m => (m.Hotkey, $"Talk in {m.Name}")));
        foreach (var (shortcut, name) in shortcuts)
        {
            if (string.IsNullOrWhiteSpace(shortcut)) continue;
            HotkeySpec.Parse(shortcut);
            if (!taken.TryAdd(shortcut.Replace(" ", ""), name))
                throw new FormatException($"{name} and {taken[shortcut.Replace(" ", "")]} cannot share a shortcut.");
        }
        if (string.IsNullOrWhiteSpace(TranslateTo)) throw new FormatException("translateTo is empty.");
        if (!KeyNames.TryParse(HandsFreeKey, out _)) throw new FormatException($"Unknown handsFreeKey \"{HandsFreeKey}\".");
        if (InsertMode is not ("paste" or "type")) throw new FormatException("insertMode must be \"paste\" or \"type\".");
        if (PasteShortcut is not ("ctrl+v" or "ctrl+shift+v" or "shift+insert"))
            throw new FormatException("pasteShortcut must be \"ctrl+v\", \"ctrl+shift+v\" or \"shift+insert\".");
        if (!Uri.TryCreate(ApiBase, UriKind.Absolute, out _)) throw new FormatException($"apiBase \"{ApiBase}\" is not a URL.");
        if (string.IsNullOrWhiteSpace(Model)) throw new FormatException("model is empty.");
        if (Languages.Any(l => l.Length is < 2 or > 3 || !l.All(char.IsAsciiLetterLower)))
            throw new FormatException("languages must be ISO codes like \"tr\" and \"en\".");
        if (QuietLevel is < 0 or > 1) throw new FormatException("quietLevel must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(MeetingModel)) throw new FormatException("meetingModel is empty.");
        if (MeetingApps.Any(a => a.Contains('\\') || a.Contains('/') || a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new FormatException("meetingApps are process names like \"Discord\", without a folder or .exe.");
        if (MaxMinutes is < 1 or > 12) throw new FormatException("maxMinutes must be between 1 and 12 (the API takes files up to 25 MB).");
    }
}
