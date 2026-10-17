import Foundation

/// A settings value the user has to fix; the message is shown to them as is.
public struct ConfigError: LocalizedError, Equatable {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// macOS virtual key codes (kVK_*), plus mouse buttons as codes 1000 + button number, so the hotkey
/// logic treats a side button like any key.
public enum Key {
    public static let escape = 53, space = 49, tab = 48, enter = 36
    public static let lcmd = 55, rcmd = 54, lshift = 56, rshift = 60, lopt = 58, ropt = 61, lctrl = 59, rctrl = 62
    /// The fn / 🌐 key.
    public static let fn = 63
    public static let mouse3 = 1002, mouse4 = 1003, mouse5 = 1004

    public static let modifiers: Set<Int> = [lcmd, rcmd, lshift, rshift, lopt, ropt, lctrl, rctrl, fn]

    public static func mouse(button: Int) -> Int { 1000 + button }
    public static func isMouse(_ code: Int) -> Bool { code >= 1000 }
}

/// A hotkey such as "fn", "ctrl+opt", "rcmd" or "f8". Each part is a slot that any of several key codes
/// can fill: "cmd" is satisfied by either Command key.
public final class HotkeySpec {
    static let modifierSlots: [String: [Int]] = [
        "fn": [Key.fn], "globe": [Key.fn],
        "ctrl": [Key.lctrl, Key.rctrl], "control": [Key.lctrl, Key.rctrl],
        "lctrl": [Key.lctrl], "rctrl": [Key.rctrl],
        "opt": [Key.lopt, Key.ropt], "option": [Key.lopt, Key.ropt], "alt": [Key.lopt, Key.ropt],
        "lopt": [Key.lopt], "lalt": [Key.lopt], "ropt": [Key.ropt], "ralt": [Key.ropt],
        "cmd": [Key.lcmd, Key.rcmd], "command": [Key.lcmd, Key.rcmd],
        "lcmd": [Key.lcmd], "rcmd": [Key.rcmd],
        "shift": [Key.lshift, Key.rshift], "lshift": [Key.lshift], "rshift": [Key.rshift],
    ]

    let slots: [[Int]]
    public let text: String

    init(slots: [[Int]], text: String) {
        self.slots = slots
        self.text = text
    }

    public static func parse(_ text: String) throws -> HotkeySpec {
        var slots: [[Int]] = []
        for part in text.split(separator: "+") {
            let raw = part.trimmingCharacters(in: .whitespaces).lowercased()
            if raw.isEmpty { continue }
            if let codes = modifierSlots[raw] { slots.append(codes) }
            else if let code = KeyNames.parse(raw) { slots.append([code]) }
            else { throw ConfigError("Unknown key \"\(raw)\" in shortcut \"\(text)\".") }
        }
        if slots.isEmpty { throw ConfigError("The shortcut is empty.") }
        return HotkeySpec(slots: slots, text: text)
    }

    public func isDown(_ down: Set<Int>) -> Bool { slots.allSatisfy { $0.contains(where: down.contains) } }

    public func contains(_ code: Int) -> Bool { slots.contains { $0.contains(code) } }

    /// A non-modifier key of the hotkey (F8, a mouse button...), whose events must not reach the app.
    public func isPlainKey(_ code: Int) -> Bool { contains(code) && !Key.modifiers.contains(code) }

    public var allKeys: Set<Int> { Set(slots.joined()) }

    public var usesMouse: Bool { allKeys.contains(where: Key.isMouse) }

    public var usesFn: Bool { allKeys.contains(Key.fn) }
}

public enum KeyNames {
    static let letters: [String: Int] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9, "b": 11, "q": 12, "w": 13,
        "e": 14, "r": 15, "y": 16, "t": 17, "o": 31, "u": 32, "i": 34, "p": 35, "l": 37, "j": 38, "k": 40, "n": 45, "m": 46,
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "9": 25, "7": 26, "8": 28, "0": 29,
    ]

    /// F1...F20, which Apple numbered in no particular order.
    static let functionKeys = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90]

    static let named: [String: Int] = [
        "space": Key.space, "esc": Key.escape, "escape": Key.escape, "tab": Key.tab, "enter": Key.enter, "return": Key.enter,
        "home": 115, "end": 119, "pageup": 116, "pagedown": 121,
        "mouse3": Key.mouse3, "middle": Key.mouse3, "mouse4": Key.mouse4, "mouse5": Key.mouse5,
    ]

    /// Canonical names, for writing a captured key back into settings.
    static let names: [Int: String] = {
        var names: [Int: String] = [
            Key.space: "space", Key.tab: "tab", Key.enter: "enter", 115: "home", 119: "end", 116: "pageup", 121: "pagedown",
            Key.mouse3: "mouse3", Key.mouse4: "mouse4", Key.mouse5: "mouse5",
            Key.lctrl: "lctrl", Key.rctrl: "rctrl", Key.lopt: "lopt", Key.ropt: "ropt",
            Key.lshift: "lshift", Key.rshift: "rshift", Key.lcmd: "lcmd", Key.rcmd: "rcmd", Key.fn: "fn",
        ]
        for (name, code) in letters { names[code] = name }
        for (i, code) in functionKeys.enumerated() { names[code] = "f\(i + 1)" }
        return names
    }()

    public static func name(_ code: Int) -> String? { names[code] }

    public static func parse(_ name: String) -> Int? {
        let name = name.lowercased()
        if let code = named[name] ?? letters[name] { return code }
        if name.count >= 2, name.hasPrefix("f"), let n = Int(name.dropFirst()), (1...functionKeys.count).contains(n) {
            return functionKeys[n - 1]
        }
        return nil
    }
}
