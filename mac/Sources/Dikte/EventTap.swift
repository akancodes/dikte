import AppKit
import CoreGraphics
import DikteCore

/// Decides, for every key and mouse-button event, whether Dikte acts on it and whether the app in
/// front still gets it. Called on the event-tap thread; the main thread changes it under the same lock.
final class KeyRouter: @unchecked Sendable {
    private let lock = NSLock()
    private var holds: HoldKeys
    private var toggle: ToggleKey?
    private var nextMode: ToggleKey?
    private var capture: KeyCapture?
    private var eatUps = Set<Int>()
    private var pending = 0

    /// All four are called on the tap thread and must hop to the main thread themselves.
    /// The mode is the id of the mode whose own shortcut it was, nil for the main shortcut.
    var onCommand: (HotkeyCommand, String?) -> Void = { _, _ in }
    var onNextMode: () -> Void = {}
    var onCaptured: (CaptureOutcome, String?) -> Void = { _, _ in }
    var onCancelPending: () -> Void = {}

    init(holds: HoldKeys, toggle: ToggleKey?, nextMode: ToggleKey?) {
        self.holds = holds
        self.toggle = toggle
        self.nextMode = nextMode
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    /// Returns true when the event must not reach the app.
    func handle(_ code: Int, isDown: Bool) -> Bool {
        var commands: [(command: HotkeyCommand, mode: String?)] = []
        var nextModeTapped = false
        var cancelPending = false
        var captured: (CaptureOutcome, String?)?
        let swallow: Bool = locked {
            // The release of a key or button whose press was a captured shortcut.
            if !isDown && eatUps.remove(code) != nil { return true }

            if let capture {
                let outcome = capture.feed(code, isDown: isDown)
                if outcome != .waiting {
                    if isDown { eatUps.insert(code) }
                    self.capture = nil
                    captured = (outcome, capture.result)
                }
                return true // nothing reaches the apps while a shortcut is being picked
            }

            if let nextMode {
                let n = nextMode.feed(code, isDown: isDown)
                if n.command == .toggle { nextModeTapped = true }
                if n.swallow { return true }
            }

            if let toggle {
                let t = toggle.feed(code, isDown: isDown)
                if t.command == .toggle { commands.append((.toggle, nil)) }
                if t.swallow { return true }
            }

            // Esc while a transcript is on its way cancels it, and must not reach the app (it would interrupt Claude Code).
            if code == Key.escape && isDown && !holds.isRecording && pending > 0 {
                cancelPending = true
                return true
            }

            let r = holds.feed(code, isDown: isDown)
            commands += r.commands
            return r.swallow
        }
        if let captured { onCaptured(captured.0, captured.1) }
        if cancelPending { onCancelPending() }
        if nextModeTapped { onNextMode() }
        for c in commands { onCommand(c.command, c.mode) }
        return swallow
    }

    func setHolds(_ h: HoldKeys) { locked { holds = h } }
    func setToggle(_ t: ToggleKey?) { locked { toggle = t } }
    func setNextMode(_ t: ToggleKey?) { locked { nextMode = t } }
    func setPending(_ n: Int) { locked { pending = n } }
    func startCapture() { locked { capture = KeyCapture() } }
    func cancelCapture() { locked { capture = nil } }
    func enterLocked() { locked { holds.enterLocked() } }
    func reset() { locked { holds.reset() } }
    var isRecording: Bool { locked { holds.isRecording } }
    var isLocked: Bool { locked { holds.isLocked } }
    var hasPending: Bool { locked { pending > 0 } }

    func resync(_ isPhysicallyDown: (Int) -> Bool) -> HotkeyCommand {
        locked {
            toggle?.resync(isPhysicallyDown: isPhysicallyDown)
            nextMode?.resync(isPhysicallyDown: isPhysicallyDown)
            return holds.resync(isPhysicallyDown: isPhysicallyDown)
        }
    }
}

/// A session-wide CGEvent tap on its own thread, so a busy main thread never delays typing.
/// It needs Accessibility permission; without it start() returns false.
final class EventTap {
    /// Set on the events Dikte posts itself (the ⌘V paste), so the tap lets them through untouched.
    static let syntheticMark: Int64 = 0x4449_4B54

    private var tap: CFMachPort?
    private let handler: (Int, Bool) -> Bool
    private let onReset: () -> Void

    init(handler: @escaping (Int, Bool) -> Bool, onReset: @escaping () -> Void) {
        self.handler = handler
        self.onReset = onReset
    }

    var isRunning: Bool { tap != nil }

    func start() -> Bool {
        if tap != nil { return true }
        guard AXIsProcessTrusted() else { return false }
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged, .otherMouseDown, .otherMouseUp]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << CGEventMask($1.rawValue)) }
        guard let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                           eventsOfInterest: mask, callback: eventTapCallback,
                                           userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return false }
        tap = port
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, port, 0)
        let thread = Thread {
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
            CGEvent.tapEnable(tap: port, enable: true)
            CFRunLoopRun()
        }
        thread.name = "Dikte event tap"
        thread.qualityOfService = .userInteractive
        thread.start()
        return true
    }

    fileprivate func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        let pass = Unmanaged.passUnretained(event)
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            // macOS switches a slow tap off; key-ups may have been lost meanwhile.
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            onReset()
            return pass
        }
        if event.getIntegerValueField(.eventSourceUserData) == EventTap.syntheticMark { return pass }

        let code: Int
        let isDown: Bool
        switch type {
        case .keyDown, .keyUp:
            code = Int(event.getIntegerValueField(.keyboardEventKeycode))
            isDown = type == .keyDown
        case .flagsChanged:
            code = Int(event.getIntegerValueField(.keyboardEventKeycode))
            guard let down = EventTap.modifierDown(code, event.flags) else { return pass }
            isDown = down
        case .otherMouseDown, .otherMouseUp:
            code = Key.mouse(button: Int(event.getIntegerValueField(.mouseEventButtonNumber)))
            isDown = type == .otherMouseDown
        default:
            return pass
        }
        return handler(code, isDown) ? nil : pass
    }

    /// Left/right device bits of the modifier flags (NX_DEVICE*KEYMASK), and the flag each side belongs to.
    private static let deviceBits: [Int: (bit: UInt64, pair: UInt64, family: CGEventFlags)] = [
        Key.lctrl: (0x0001, 0x2001, .maskControl), Key.rctrl: (0x2000, 0x2001, .maskControl),
        Key.lshift: (0x0002, 0x0006, .maskShift), Key.rshift: (0x0004, 0x0006, .maskShift),
        Key.lcmd: (0x0008, 0x0018, .maskCommand), Key.rcmd: (0x0010, 0x0018, .maskCommand),
        Key.lopt: (0x0020, 0x0060, .maskAlternate), Key.ropt: (0x0040, 0x0060, .maskAlternate),
    ]

    /// Whether a modifier key is down after a flagsChanged event; nil for keys that are not modifiers (Caps Lock).
    static func modifierDown(_ code: Int, _ flags: CGEventFlags) -> Bool? {
        if code == Key.fn { return flags.contains(.maskSecondaryFn) }
        guard let d = deviceBits[code] else { return nil }
        if flags.rawValue & d.bit != 0 { return true }
        // Some keyboards and remote sessions leave the side bits out; then the shared flag decides.
        if flags.rawValue & d.pair == 0 { return flags.contains(d.family) }
        return false
    }

    /// For resyncing after lost key-ups. Plain keys are trusted to the tap (reading them can need more
    /// permissions), and so is fn, whose flag is not reliably reported here.
    static func isPhysicallyDown(_ code: Int) -> Bool {
        if Key.isMouse(code) { return NSEvent.pressedMouseButtons & (1 << (code - 1000)) != 0 }
        if code == Key.fn || !Key.modifiers.contains(code) { return true }
        return modifierDown(code, CGEventSource.flagsState(.combinedSessionState)) ?? true
    }
}

private func eventTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent,
                              refcon: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    guard let refcon else { return Unmanaged.passUnretained(event) }
    return Unmanaged<EventTap>.fromOpaque(refcon).takeUnretainedValue().handle(type, event)
}
