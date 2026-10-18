import Foundation

/// Everything in settings.json. The keys match the Windows app's, except that macOS always pastes
/// with ⌘V, so there is no pasteShortcut.
public struct Settings: Codable, Equatable {
    /// Hold to talk. Examples: "fn", "rcmd", "ctrl+opt", "f8", "mouse4".
    public var hotkey = "fn"
    /// Tap once to start listening hands-free, tap again to send. Empty: not used. Example: "mouse5".
    public var toggleHotkey = ""
    /// Pressed while holding the hotkey: keeps listening hands-free until the hotkey is pressed again.
    public var handsFreeKey = "space"
    /// Any OpenAI-compatible server: https://api.openai.com/v1, https://api.groq.com/openai/v1, a local whisper server...
    public var apiBase = "https://api.openai.com/v1"
    /// gpt-transcribe, gpt-4o-transcribe, gpt-4o-mini-transcribe, whisper-1...
    public var model = "gpt-transcribe"
    /// ISO-639-1 codes of the languages you speak. None: detected. One: pinned. Several: mixed freely.
    public var languages: [String] = []
    /// Names and terms the model should spell your way.
    public var vocabulary = "Claude Code, Claude, commit, pull request, deploy, npm, TypeScript, Docker"
    /// "paste" puts the text on the clipboard and presses ⌘V; "type" types it key by key.
    public var insertMode = "paste"
    /// Put back what was on the clipboard after pasting.
    public var restoreClipboard = true
    /// Add a space after the text, so consecutive dictations do not run together.
    public var appendSpace = true
    /// Before 0.7 the only mode: read once to pick the first activeMode, then unused.
    public var cleanup = false
    /// The ways of writing what was said; see Mode.
    public var modes = Modes.defaults()
    /// The mode used when no shortcut or app picks one: a mode id, or "plain" for the words as heard.
    public var activeMode = Modes.plain
    /// Tap to switch the active mode to the next one. Empty: none.
    public var nextModeHotkey = ""
    public var cleanupModel = "gpt-4.1-mini"
    /// Input device name as macOS lists it; empty uses the system default.
    public var microphone = ""
    public var sounds = true
    /// Turn the Mac's output down while recording, so the mic hears you and you hear yourself think.
    public var quietOtherAudio = true
    /// What the output is turned down to while recording: 0 mutes it, 0.15 leaves 15% of the volume.
    public var quietLevel = 0.15
    public var maxMinutes = 10
    public var historySize = 20

    public init() {}

    /// Every key is optional: a file with only "hotkey" in it keeps the defaults for the rest.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func value<T: Decodable>(_ key: CodingKeys, _ fallback: T) throws -> T {
            try c.decodeIfPresent(T.self, forKey: key) ?? fallback
        }
        let d = Settings()
        hotkey = try value(.hotkey, d.hotkey)
        toggleHotkey = try value(.toggleHotkey, d.toggleHotkey)
        handsFreeKey = try value(.handsFreeKey, d.handsFreeKey)
        apiBase = try value(.apiBase, d.apiBase)
        model = try value(.model, d.model)
        languages = try value(.languages, d.languages)
        vocabulary = try value(.vocabulary, d.vocabulary)
        insertMode = try value(.insertMode, d.insertMode)
        restoreClipboard = try value(.restoreClipboard, d.restoreClipboard)
        appendSpace = try value(.appendSpace, d.appendSpace)
        cleanup = try value(.cleanup, d.cleanup)
        modes = try value(.modes, d.modes)
        nextModeHotkey = try value(.nextModeHotkey, d.nextModeHotkey)
        // A file from before modes: the old clean-up switch picks the first active mode.
        activeMode = try c.decodeIfPresent(String.self, forKey: .activeMode)
            ?? (cleanup && modes.contains { $0.id == "clean" } ? "clean" : Modes.plain)
        normalize()
        cleanupModel = try value(.cleanupModel, d.cleanupModel)
        microphone = try value(.microphone, d.microphone)
        sounds = try value(.sounds, d.sounds)
        quietOtherAudio = try value(.quietOtherAudio, d.quietOtherAudio)
        quietLevel = try value(.quietLevel, d.quietLevel)
        maxMinutes = try value(.maxMinutes, d.maxMinutes)
        historySize = try value(.historySize, d.historySize)
    }

    /// Tidies what a hand-edited file may hold: spaces, missing ids, an active mode that no longer exists.
    mutating func normalize() {
        for i in modes.indices {
            modes[i].name = modes[i].name.trimmingCharacters(in: .whitespacesAndNewlines)
            modes[i].instruction = modes[i].instruction.trimmingCharacters(in: .whitespacesAndNewlines)
            modes[i].model = modes[i].model.trimmingCharacters(in: .whitespaces)
            modes[i].hotkey = modes[i].hotkey.trimmingCharacters(in: .whitespaces)
            var seen = Set<String>()
            modes[i].apps = modes[i].apps.map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
            if modes[i].id.trimmingCharacters(in: .whitespaces).isEmpty {
                modes[i].id = Modes.newId(modes[i].name, taken: modes.map(\.id))
            }
        }
        if activeMode != Modes.plain && !modes.contains(where: { $0.id == activeMode }) { activeMode = Modes.plain }
    }

    /// Reads the file, or writes the defaults when it does not exist yet. Comments and trailing commas are fine.
    public static func loadOrCreate(_ url: URL) throws -> Settings {
        guard FileManager.default.fileExists(atPath: url.path) else {
            let fresh = Settings()
            try fresh.save(url)
            return fresh
        }
        let settings = try decode(Data(contentsOf: url))
        try settings.validate()
        return settings
    }

    public static func decode(_ data: Data) throws -> Settings {
        let text = String(decoding: data, as: UTF8.self)
        do {
            return try JSONDecoder().decode(Settings.self, from: Data(JSONC.strip(text).utf8))
        } catch let DecodingError.typeMismatch(_, context), let DecodingError.valueNotFound(_, context) {
            let key = context.codingPath.first?.stringValue ?? "a value"
            throw ConfigError("\(key) has the wrong kind of value in settings.json.")
        } catch is DecodingError {
            throw ConfigError("settings.json is not valid JSON.")
        }
    }

    public func save(_ url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try encoded().write(to: url, options: .atomic)
    }

    public func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return try encoder.encode(self)
    }

    /// The settings as a JSON object, for the window and for merging a partial update into.
    public func jsonObject() -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: encoded())) as? [String: Any] ?? [:]
    }

    /// These settings with the window's changes merged in, checked.
    public func merging(_ patch: [String: Any]) throws -> Settings {
        var object = jsonObject()
        for (key, value) in patch { object[key] = value }
        let merged = try Settings.decode(JSONSerialization.data(withJSONObject: object))
        try merged.validate()
        return merged
    }

    /// Throws ConfigError with a message fit for the user.
    public func validate() throws {
        _ = try HotkeySpec.parse(hotkey)
        func plain(_ s: String) -> String { s.replacingOccurrences(of: " ", with: "").lowercased() }
        var ids = Set<String>()
        for m in modes {
            if m.id == Modes.plain { throw ConfigError("\"\(Modes.plain)\" cannot be a mode's id.") }
            if !ids.insert(m.id).inserted { throw ConfigError("Two modes share the id \"\(m.id)\".") }
            if m.name.isEmpty { throw ConfigError("Every mode needs a name.") }
            if m.instruction.isEmpty { throw ConfigError("Tell the \(m.name) mode what to do with your words.") }
        }
        var taken = [plain(hotkey): "Hold to talk"]
        let shortcuts = [(toggleHotkey, "Tap to talk"), (nextModeHotkey, "Next mode")] + modes.map { ($0.hotkey, "Talk in \($0.name)") }
        for (shortcut, name) in shortcuts where !shortcut.trimmingCharacters(in: .whitespaces).isEmpty {
            _ = try HotkeySpec.parse(shortcut)
            if let other = taken[plain(shortcut)] { throw ConfigError("\(name) and \(other) cannot share a shortcut.") }
            taken[plain(shortcut)] = name
        }
        if KeyNames.parse(handsFreeKey) == nil { throw ConfigError("Unknown handsFreeKey \"\(handsFreeKey)\".") }
        if !["paste", "type"].contains(insertMode) { throw ConfigError("insertMode must be \"paste\" or \"type\".") }
        guard let url = URL(string: apiBase), let scheme = url.scheme, ["http", "https"].contains(scheme), url.host != nil else {
            throw ConfigError("apiBase \"\(apiBase)\" is not a URL.")
        }
        if model.trimmingCharacters(in: .whitespaces).isEmpty { throw ConfigError("model is empty.") }
        if languages.contains(where: { !(2...3).contains($0.count) || !$0.allSatisfy({ ("a"..."z").contains($0) }) }) {
            throw ConfigError("languages must be ISO codes like \"tr\" and \"en\".")
        }
        if !(0...1).contains(quietLevel) { throw ConfigError("quietLevel must be between 0 and 1.") }
        if !(1...12).contains(maxMinutes) { throw ConfigError("maxMinutes must be between 1 and 12 (the API takes files up to 25 MB).") }
    }
}

/// JSON with comments and trailing commas, as people write it by hand, turned into plain JSON.
public enum JSONC {
    public static func strip(_ text: String) -> String {
        let chars = Array(text.unicodeScalars)
        var out = String.UnicodeScalarView()
        var i = 0
        var inString = false
        // Index in `out` of a comma that may turn out to be trailing.
        var pendingComma: String.UnicodeScalarView.Index?

        while i < chars.count {
            let c = chars[i]
            if inString {
                out.append(c)
                if c == "\\", i + 1 < chars.count {
                    out.append(chars[i + 1])
                    i += 2
                    continue
                }
                if c == "\"" { inString = false }
                i += 1
                continue
            }
            if c == "/", i + 1 < chars.count, chars[i + 1] == "/" {
                while i < chars.count, chars[i] != "\n" { i += 1 }
                continue
            }
            if c == "/", i + 1 < chars.count, chars[i + 1] == "*" {
                i += 2
                while i + 1 < chars.count, !(chars[i] == "*" && chars[i + 1] == "/") { i += 1 }
                i += 2
                continue
            }
            if c == "}" || c == "]", let comma = pendingComma {
                out.remove(at: comma)
            }
            if c == "," {
                pendingComma = out.endIndex
            } else if !CharacterSet.whitespacesAndNewlines.contains(c) {
                pendingComma = nil
            }
            if c == "\"" { inString = true }
            out.append(c)
            i += 1
        }
        return String(out)
    }
}
