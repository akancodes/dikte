import AppKit

/// The small pill at the bottom of the screen. It never takes focus and lets clicks through, so the
/// text still lands in the window the user is typing in.
@MainActor
final class Overlay {
    enum Mode { case hidden, listening, handsFree, working, done, message }

    private let panel: NSPanel
    private let pill = PillView(frame: NSRect(x: 0, y: 0, width: 260, height: 44))
    private var timer: Timer?
    private var level: (() -> Float)?
    private var hideAt = Date.distantFuture
    private(set) var mode = Mode.hidden

    init() {
        panel = NSPanel(contentRect: pill.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .statusBar
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.contentView = pill
    }

    func showListening(title: String = "Listening", level: @escaping () -> Float) {
        self.level = level
        pill.levels = Array(repeating: 0, count: PillView.bars)
        set(.listening, title, "Release to send · Space: hands-free · Esc", autoHide: 0)
    }

    func showHandsFree() { set(.handsFree, "Listening hands-free", "Shortcut to send · Esc to cancel", autoHide: 0) }

    func showWorking(_ title: String = "Transcribing") {
        level = nil
        set(.working, title, "Esc to cancel", autoHide: 0)
    }

    func showDone(_ text: String) {
        level = nil
        set(.done, text, "", autoHide: 0.9)
    }

    func showMessage(_ text: String, _ hint: String = "", seconds: Double = 4) {
        level = nil
        set(.message, text, hint, autoHide: seconds)
    }

    func hideNow() {
        mode = .hidden
        level = nil
        timer?.invalidate()
        timer = nil
        panel.orderOut(nil)
    }

    private func set(_ mode: Mode, _ text: String, _ hint: String, autoHide: Double) {
        let wasHidden = self.mode == .hidden || !panel.isVisible
        self.mode = mode
        pill.mode = mode
        pill.text = text
        pill.hint = hint
        pill.since = Date()
        hideAt = autoHide == 0 ? .distantFuture : Date().addingTimeInterval(autoHide)
        if wasHidden { place() }
        panel.orderFrontRegardless()
        if timer == nil {
            let t = Timer(timeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
                Task { @MainActor in self?.tick() }
            }
            RunLoop.main.add(t, forMode: .common)
            timer = t
        }
        pill.needsDisplay = true
    }

    /// Bottom centre of the screen the mouse is on, above the Dock.
    private func place() {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let area = screen?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: area.midX - pill.frame.width / 2, y: area.minY + 28))
    }

    private func tick() {
        if Date() >= hideAt {
            hideNow()
            return
        }
        if let level {
            pill.levels.removeFirst()
            // Speech peaks sit far below full scale; a square root makes quiet talk visible.
            pill.levels.append(min(1, level().squareRoot() * 1.4))
        }
        pill.needsDisplay = true
    }
}

final class PillView: NSView {
    static let bars = 18

    var mode = Overlay.Mode.hidden
    var text = ""
    var hint = ""
    var since = Date()
    var levels = [Float](repeating: 0, count: PillView.bars)

    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let bounds = self.bounds
        NSColor(srgbRed: 24 / 255, green: 24 / 255, blue: 27 / 255, alpha: 0.96).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: bounds.height / 2, yRadius: bounds.height / 2).fill()

        let t = CGFloat(Date().timeIntervalSince(since))
        let pad: CGFloat = 16, mid = bounds.height / 2
        let accent: NSColor
        switch mode {
        case .listening: accent = NSColor(srgbRed: 0.94, green: 0.27, blue: 0.27, alpha: 1)
        case .handsFree: accent = NSColor(srgbRed: 0.98, green: 0.45, blue: 0.09, alpha: 1)
        case .working: accent = NSColor(srgbRed: 0.38, green: 0.65, blue: 0.98, alpha: 1)
        case .done: accent = NSColor(srgbRed: 0.29, green: 0.87, blue: 0.5, alpha: 1)
        default: accent = NSColor(srgbRed: 0.98, green: 0.8, blue: 0.08, alpha: 1)
        }

        // Status dot: pulses while listening, spins while working.
        let dot: CGFloat = 9
        if mode == .working {
            let arc = NSBezierPath()
            let start = (t * 360).truncatingRemainder(dividingBy: 360)
            arc.appendArc(withCenter: NSPoint(x: pad + dot / 2, y: mid), radius: dot / 2 + 1, startAngle: start, endAngle: start + 270)
            arc.lineWidth = 2.2
            arc.lineCapStyle = .round
            accent.setStroke()
            arc.stroke()
        } else {
            let pulse = mode == .listening || mode == .handsFree ? 0.75 + 0.25 * sin(t * 6) : 1
            accent.withAlphaComponent(pulse).setFill()
            NSBezierPath(ovalIn: NSRect(x: pad, y: mid - dot / 2, width: dot, height: dot)).fill()
        }

        let textX = pad + dot + 10
        var right = bounds.width - pad
        if mode == .listening || mode == .handsFree {
            // Level meter on the right.
            let barW: CGFloat = 3, gap: CGFloat = 2
            let x0 = bounds.width - pad - CGFloat(PillView.bars) * (barW + gap)
            NSColor(white: 0.96, alpha: 0.86).setFill()
            for (i, v) in levels.enumerated() {
                let h = max(2, CGFloat(v) * (bounds.height - 18))
                NSBezierPath(rect: NSRect(x: x0 + CGFloat(i) * (barW + gap), y: mid - h / 2, width: barW, height: h)).fill()
            }
            right = x0 - 8
        }

        let style = NSMutableParagraphStyle()
        style.lineBreakMode = .byTruncatingTail
        let title = mode == .working ? text + String(repeating: ".", count: 1 + Int(t * 3) % 3) : text
        let titleAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold), .foregroundColor: NSColor(white: 0.96, alpha: 1), .paragraphStyle: style,
        ]
        let hintAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor(white: 0.64, alpha: 1), .paragraphStyle: style,
        ]
        let width = max(0, right - textX)
        if hint.isEmpty {
            (title as NSString).draw(in: NSRect(x: textX, y: mid - 9, width: width, height: 18), withAttributes: titleAttrs)
        } else {
            (title as NSString).draw(in: NSRect(x: textX, y: mid - 17, width: width, height: 17), withAttributes: titleAttrs)
            (hint as NSString).draw(in: NSRect(x: textX, y: mid + 1, width: width, height: 15), withAttributes: hintAttrs)
        }
    }
}
