import AppKit
import Carbon.HIToolbox
import DikteCore

/// Puts text where the user is typing: the focused field of whatever app is in front.
@MainActor
enum Inserter {
    static let concealed = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")
    static let transient = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")

    static func insert(_ text: String, settings s: Settings) async {
        // ⌘V pressed while fn or ⌃⌥ is still down is another shortcut; wait for the hands to come off the hotkey.
        let held: CGEventFlags = [.maskCommand, .maskAlternate, .maskControl, .maskShift, .maskSecondaryFn]
        for _ in 0..<60 {
            if CGEventSource.flagsState(.combinedSessionState).intersection(held).isEmpty { break }
            try? await Task.sleep(nanoseconds: 25_000_000)
        }
        if s.insertMode == "type" { type(text) } else { await paste(text, restore: s.restoreClipboard) }
    }

    static func source() -> CGEventSource? {
        let source = CGEventSource(stateID: .combinedSessionState)
        source?.userData = EventTap.syntheticMark
        return source
    }

    static func type(_ text: String) {
        // A newline would be a Return, which sends a half-finished prompt in a terminal.
        let flat = text.replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
        let units = Array(flat.utf16)
        let source = source()
        // Events carry at most 20 UTF-16 units each.
        var i = 0
        while i < units.count {
            var end = min(i + 20, units.count)
            if end < units.count, UTF16.isLeadSurrogate(units[end - 1]) { end -= 1 }
            let chunk = Array(units[i..<end])
            guard let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false) else { return }
            down.flags = []
            up.flags = []
            down.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: chunk)
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
            i = end
        }
    }

    static func paste(_ text: String, restore: Bool) async {
        let pasteboard = NSPasteboard.general
        let saved = restore ? snapshot(pasteboard) : nil

        pasteboard.clearContents()
        let item = NSPasteboardItem()
        item.setString(text, forType: .string)
        // Keeps dictations out of clipboard managers' history.
        item.setData(Data(), forType: concealed)
        item.setData(Data(), forType: transient)
        pasteboard.writeObjects([item])
        let ours = pasteboard.changeCount

        let v = CGKeyCode(keyCode(for: "v") ?? 9)
        let source = source()
        if let down = CGEvent(keyboardEventSource: source, virtualKey: v, keyDown: true),
           let up = CGEvent(keyboardEventSource: source, virtualKey: v, keyDown: false) {
            down.flags = .maskCommand
            up.flags = .maskCommand
            down.post(tap: .cghidEventTap)
            up.post(tap: .cghidEventTap)
        }

        guard let saved, !saved.isEmpty else { return }
        // The target reads the clipboard when it handles the keystroke, which can lag in a busy terminal.
        try? await Task.sleep(nanoseconds: 700_000_000)
        guard pasteboard.changeCount == ours else { return } // something else was copied meanwhile
        pasteboard.clearContents()
        pasteboard.writeObjects(saved)
    }

    /// Copies of what is on the clipboard, every type of every item, to put back after pasting.
    static func snapshot(_ pasteboard: NSPasteboard) -> [NSPasteboardItem] {
        (pasteboard.pasteboardItems ?? []).map { original in
            let copy = NSPasteboardItem()
            for type in original.types {
                if let data = original.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        }
    }

    /// The key that types `character` in the current keyboard layout: ⌘V is matched by character, and
    /// "v" sits elsewhere on Turkish F, Dvorak and others.
    static func keyCode(for character: String) -> Int? {
        let sources = [TISCopyCurrentKeyboardLayoutInputSource(), TISCopyCurrentASCIICapableKeyboardLayoutInputSource()]
        for case let source? in sources.map({ $0?.takeRetainedValue() }) {
            guard let pointer = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { continue }
            let data = Unmanaged<CFData>.fromOpaque(pointer).takeUnretainedValue() as Data
            let found: Int? = data.withUnsafeBytes { raw in
                guard let layout = raw.baseAddress?.assumingMemoryBound(to: UCKeyboardLayout.self) else { return nil }
                for code in 0..<128 {
                    var dead: UInt32 = 0
                    var length = 0
                    var chars = [UniChar](repeating: 0, count: 4)
                    let status = UCKeyTranslate(layout, UInt16(code), UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                                                OptionBits(kUCKeyTranslateNoDeadKeysMask), &dead, chars.count, &length, &chars)
                    if status == noErr, length == 1, String(utf16CodeUnits: chars, count: 1) == character { return code }
                }
                return nil
            }
            if let found { return found }
        }
        return nil
    }
}
