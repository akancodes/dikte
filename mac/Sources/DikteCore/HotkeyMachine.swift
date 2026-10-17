import Foundation

public enum HotkeyCommand: Equatable { case none, start, lock, stopAndSend, cancel, toggle }

public struct HotkeyResult: Equatable {
    public let command: HotkeyCommand
    public let swallow: Bool

    public init(_ command: HotkeyCommand, swallow: Bool) {
        self.command = command
        self.swallow = swallow
    }

    public static let pass = HotkeyResult(.none, swallow: false)
}

/// Push-to-talk state machine fed with raw key events from the event tap.
///
/// Holding the hotkey records; releasing it sends. Pressing the lock key while holding switches to
/// hands-free, and the next press of the hotkey sends. Escape cancels. Any other key pressed while
/// holding means the user was typing a shortcut that happens to start with the hotkey (fn+F1,
/// fn+←...), so the recording is cancelled and the key goes through untouched.
public final class HotkeyMachine {
    enum State { case idle, holding, locked }

    let spec: HotkeySpec
    let lockKey: Int
    var down = Set<Int>()
    var swallowed = Set<Int>()
    var state = State.idle

    public init(spec: HotkeySpec, lockKey: Int) {
        self.spec = spec
        self.lockKey = lockKey
    }

    public var isRecording: Bool { state != .idle }
    public var isLocked: Bool { state == .locked }

    @discardableResult public func feed(_ code: Int, isDown: Bool) -> HotkeyResult {
        isDown ? keyDown(code) : keyUp(code)
    }

    func keyDown(_ code: Int) -> HotkeyResult {
        let wasCombo = spec.isDown(down)
        let repeated = !down.insert(code).inserted
        let nowCombo = spec.isDown(down)
        let hotkeyKey = spec.contains(code)

        switch state {
        case .idle:
            if !wasCombo && nowCombo {
                state = .holding
                return result(.start, code, swallow: spec.isPlainKey(code))
            }
            if hotkeyKey && nowCombo && spec.isPlainKey(code) { return result(.none, code, swallow: true) }
            return .pass

        case .holding:
            if hotkeyKey || repeated {
                return result(.none, code, swallow: swallowed.contains(code) || spec.isPlainKey(code))
            }
            if code == lockKey {
                state = .locked
                return result(.lock, code, swallow: true)
            }
            state = .idle
            if code == Key.escape { return result(.cancel, code, swallow: true) }
            return HotkeyResult(.cancel, swallow: false)

        case .locked:
            if code == Key.escape && !repeated {
                state = .idle
                return result(.cancel, code, swallow: true)
            }
            if !wasCombo && nowCombo {
                state = .idle
                return result(.stopAndSend, code, swallow: spec.isPlainKey(code))
            }
            if repeated && swallowed.contains(code) { return HotkeyResult(.none, swallow: true) }
            // Hands-free: the user may keep working while it listens.
            return .pass
        }
    }

    func keyUp(_ code: Int) -> HotkeyResult {
        let wasCombo = spec.isDown(down)
        down.remove(code)
        let nowCombo = spec.isDown(down)
        let swallow = swallowed.remove(code) != nil

        if state == .holding && wasCombo && !nowCombo {
            state = .idle
            return HotkeyResult(.stopAndSend, swallow: swallow)
        }
        return HotkeyResult(.none, swallow: swallow)
    }

    func result(_ command: HotkeyCommand, _ code: Int, swallow: Bool) -> HotkeyResult {
        if swallow { swallowed.insert(code) }
        return HotkeyResult(command, swallow: swallow)
    }

    /// Drops keys the tap thinks are down but physically are not. Key-ups get lost when the screen
    /// locks or the tap is switched off mid-hold. Returns stopAndSend when that ends a hold.
    public func resync(isPhysicallyDown: (Int) -> Bool) -> HotkeyCommand {
        let wasCombo = spec.isDown(down)
        down = down.filter(isPhysicallyDown)
        swallowed = swallowed.filter(down.contains)
        if state == .holding && wasCombo && !spec.isDown(down) {
            state = .idle
            return .stopAndSend
        }
        return .none
    }

    /// Starts a hands-free recording without the keyboard (menu bar, window).
    public func enterLocked() { state = .locked }

    public func reset() { state = .idle }
}

/// The tap-to-start, tap-to-send key: each fresh press is one toggle. Its own keys (a mouse button,
/// F9...) are swallowed so the app underneath never sees them.
public final class ToggleKey {
    let spec: HotkeySpec
    var down = Set<Int>()
    var swallowed = Set<Int>()

    public init(spec: HotkeySpec) { self.spec = spec }

    @discardableResult public func feed(_ code: Int, isDown: Bool) -> HotkeyResult {
        if !isDown {
            down.remove(code)
            return HotkeyResult(.none, swallow: swallowed.remove(code) != nil)
        }
        let was = spec.isDown(down)
        down.insert(code)
        let now = spec.isDown(down)
        let swallow = spec.isPlainKey(code) && now
        if swallow { swallowed.insert(code) }
        return HotkeyResult(!was && now ? .toggle : .none, swallow: swallow)
    }

    public func resync(isPhysicallyDown: (Int) -> Bool) {
        down = down.filter(isPhysicallyDown)
        swallowed = swallowed.filter(down.contains)
    }
}

public enum CaptureOutcome: Equatable { case waiting, done, cancelled, unusable }

/// "Press the shortcut you want": collects what is held, and when everything is let go (or a mouse
/// button is pressed) names it the way settings write it. One modifier alone keeps its side ("rcmd");
/// in a combination either side counts ("ctrl+opt").
public final class KeyCapture {
    static let generic: [(name: String, codes: [Int])] = [
        ("fn", [Key.fn]),
        ("ctrl", [Key.lctrl, Key.rctrl]),
        ("opt", [Key.lopt, Key.ropt]),
        ("shift", [Key.lshift, Key.rshift]),
        ("cmd", [Key.lcmd, Key.rcmd]),
    ]

    var held = Set<Int>()
    var chord: [Int] = []
    public private(set) var result: String?

    public init() {}

    @discardableResult public func feed(_ code: Int, isDown: Bool) -> CaptureOutcome {
        if isDown {
            if code == Key.escape && held.isEmpty { return .cancelled }
            if held.insert(code).inserted && !chord.contains(code) { chord.append(code) }
            // A mouse button completes at once, with whatever keys are held alongside it.
            return Key.isMouse(code) ? finish() : .waiting
        }
        held.remove(code)
        return held.isEmpty && !chord.isEmpty ? finish() : .waiting
    }

    func finish() -> CaptureOutcome {
        result = KeyCapture.describe(chord)
        return result == nil ? .unusable : .done
    }

    public static func describe(_ chord: [Int]) -> String? {
        if chord.count == 1 { return KeyNames.name(chord[0]) }
        var parts: [String] = []
        for group in generic where chord.contains(where: group.codes.contains) { parts.append(group.name) }
        for code in chord where !generic.contains(where: { $0.codes.contains(code) }) {
            guard let name = KeyNames.name(code) else { return nil }
            parts.append(name)
        }
        return parts.joined(separator: "+")
    }
}
