import UIKit

/// The typing keys, drawn and touched in UIKit: SwiftUI gestures drop the overlapping touches of two-thumb
/// typing. Every touch is tracked on its own, a letter is typed when its finger lifts (so a finger can slide to
/// the right key), and a new touch types the letter still held by an earlier one, as the system keyboard does.
final class KeysView: UIView {
    enum Layout: String { case turkish = "tr", english = "en" }
    enum Page { case letters, numbers, symbols }
    enum Shift { case off, on, locked }

    enum Kind: Equatable {
        case char(String)
        case shift, delete, space, ret, globe
        case page(Page)
        /// Empty room at a row's ends; touches there go to the nearest key.
        case spacer
    }

    struct Key {
        var kind: Kind
        /// Width in letter-key units; nil shares what the row has left.
        var units: CGFloat?
        var frame: CGRect = .zero
    }

    struct Actions {
        var type: (String) -> Void = { _ in }
        var delete: () -> Void = {}
        var deleteWord: () -> Void = {}
        var space: () -> Void = {}
        var ret: () -> Void = {}
        var moveCursor: (Int) -> Void = { _ in }
        var shiftChanged: () -> Void = {}
        var feedback: () -> Void = {}
    }

    var actions = Actions()
    /// The view the globe key hands its touches to (handleInputModeList), set by the controller.
    var globeTarget: (target: Any, action: Selector)?

    var layout: Layout = .turkish { didSet { if oldValue != layout { rebuild() } } }
    var page: Page = .letters { didSet { if oldValue != page { rebuild() } } }
    var shift: Shift = .off { didSet { if oldValue != shift { refreshLabels() } } }
    var needsGlobe = true { didSet { if oldValue != needsGlobe { rebuild() } } }
    var returnTitle = "return" { didSet { if oldValue != returnTitle { refreshLabels() } } }
    /// Extra keys beside the space bar for e-mail and web addresses.
    var addressKeys: [String] = [] { didSet { if oldValue != addressKeys { rebuild() } } }

    private var rows: [[Key]] = []
    private var caps: [KeyCap] = []
    private var globeButton: UIButton?
    private var touchKeys: [UITouch: (row: Int, index: Int)] = [:]
    private var popup: UILabel?
    private var lastShiftTap = Date.distantPast

    private var deleteTimer: Timer?
    private var deleteRepeats = 0
    private var spaceStart: CGPoint?
    private var spaceMoved: CGFloat = 0
    private var trackpad = false

    private let margin: CGFloat = 3
    private let rowGap: CGFloat = 11
    /// Room between the strip above and the first row.
    private let topInset: CGFloat = 6

    override init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        clipsToBounds = false
        rebuild()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    // MARK: the rows

    private func letterRows() -> [[Key]] {
        func chars(_ s: String) -> [Key] { s.map { Key(kind: .char(String($0)), units: 1) } }
        switch layout {
        case .turkish:
            return [
                chars("qwertyuıopğü"),
                chars("asdfghjklşi"),
                [Key(kind: .shift, units: nil)] + chars("zxcvbnmöç") + [Key(kind: .delete, units: nil)],
            ]
        case .english:
            return [
                chars("qwertyuiop"),
                [Key(kind: .spacer, units: nil)] + chars("asdfghjkl") + [Key(kind: .spacer, units: nil)],
                [Key(kind: .shift, units: 1.45), Key(kind: .spacer, units: nil)] + chars("zxcvbnm")
                    + [Key(kind: .spacer, units: nil), Key(kind: .delete, units: 1.45)],
            ]
        }
    }

    private func numberRows() -> [[Key]] {
        func chars(_ s: [String], _ units: CGFloat = 1) -> [Key] { s.map { Key(kind: .char($0), units: units) } }
        let currency = layout == .turkish ? "₺" : "$"
        let punctuation = chars([".", ",", "?", "!", "'"], 1.4)
        if page == .numbers {
            return [
                chars(["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"]),
                chars(["-", "/", ":", ";", "(", ")", currency, "&", "@", "\""]),
                [Key(kind: .page(.symbols), units: nil)] + punctuation + [Key(kind: .delete, units: nil)],
            ]
        }
        return [
            chars(["[", "]", "{", "}", "#", "%", "^", "*", "+", "="]),
            chars(["_", "\\", "|", "~", "<", ">", "€", "£", "$", "•"]),
            [Key(kind: .page(.numbers), units: nil)] + punctuation + [Key(kind: .delete, units: nil)],
        ]
    }

    private func bottomRow() -> [Key] {
        var row = [Key(kind: .page(page == .letters ? .numbers : .letters), units: 1.6)]
        if needsGlobe { row.append(Key(kind: .globe, units: 1.3)) }
        row.append(Key(kind: .space, units: nil))
        row += addressKeys.map { Key(kind: .char($0), units: 1.1) }
        row.append(Key(kind: .ret, units: 2.5))
        return row
    }

    private func rebuild() {
        rows = (page == .letters ? letterRows() : numberRows()) + [bottomRow()]
        caps.forEach { $0.removeFromSuperview() }
        globeButton?.removeFromSuperview()
        globeButton = nil
        caps = []
        for row in rows {
            for key in row {
                let cap = KeyCap()
                cap.isHidden = key.kind == .spacer
                addSubview(cap)
                caps.append(cap)
                if key.kind == .globe {
                    let button = UIButton(type: .custom)
                    if let globeTarget { button.addTarget(globeTarget.target, action: globeTarget.action, for: .allTouchEvents) }
                    button.addTarget(self, action: #selector(globeFeedback), for: .touchDown)
                    addSubview(button)
                    globeButton = button
                }
            }
        }
        refreshLabels()
        setNeedsLayout()
    }

    @objc private func globeFeedback() { actions.feedback() }

    /// Re-attaches the globe once the controller has set its target.
    func attachGlobe(target: Any, action: Selector) {
        globeTarget = (target, action)
        rebuild()
    }

    private func refreshLabels() {
        var i = 0
        for row in rows {
            for key in row {
                caps[i].configure(label(for: key.kind), function: isFunction(key.kind), highlighted: key.kind == .shift && shift != .off,
                                  accent: key.kind == .ret && returnTitle != "return")
                i += 1
            }
        }
    }

    private func isFunction(_ kind: Kind) -> Bool {
        switch kind {
        case .char, .space: return false
        default: return true
        }
    }

    private func label(for kind: Kind) -> KeyCap.Face {
        switch kind {
        case .char(let c): return .text(page == .letters ? cased(c) : c, size: page == .letters ? 23 : 21)
        case .shift: return .symbol(shift == .locked ? "capslock.fill" : shift == .on ? "shift.fill" : "shift")
        case .delete: return .symbol("delete.left")
        case .space: return .text(layout == .turkish ? "boşluk" : "space", size: 16)
        case .ret: return returnTitle == "return" ? .symbol("return") : .text(returnTitle, size: 16)
        case .globe: return .symbol("globe")
        case .page(let p): return .text(p == .letters ? "ABC" : p == .numbers ? "123" : "#+=", size: 16)
        case .spacer: return .text("", size: 1)
        }
    }

    /// Turkish capitals: i → İ and ı → I.
    func cased(_ c: String) -> String {
        guard shift != .off else { return c }
        return c.uppercased(with: Locale(identifier: layout == .turkish ? "tr_TR" : "en_US"))
    }

    // MARK: geometry

    override func layoutSubviews() {
        super.layoutSubviews()
        let width = bounds.width
        let rowHeight = (bounds.height - topInset - rowGap * CGFloat(rows.count - 1)) / CGFloat(rows.count)
        // The widest letter row fixes the unit, so every letter key has the same width.
        let widest = CGFloat(rows[0].count)
        let gap: CGFloat = widest >= 12 ? 5 : 6
        let unit = (width - 2 * margin - gap * (widest - 1)) / widest
        var i = 0
        for r in rows.indices {
            let fixed = rows[r].compactMap { $0.units }.reduce(0, +) * unit
            let flexCount = CGFloat(rows[r].filter { $0.units == nil }.count)
            let gaps = gap * CGFloat(rows[r].count - 1)
            let flex = flexCount > 0 ? max(0, (width - 2 * margin - gaps - fixed) / flexCount) : 0
            // A row with no flexible key is centred.
            var x = flexCount > 0 ? margin : (width - fixed - gaps) / 2
            let y = topInset + CGFloat(r) * (rowHeight + rowGap)
            for k in rows[r].indices {
                let w = rows[r][k].units.map { $0 * unit } ?? flex
                let frame = CGRect(x: x, y: y, width: w, height: rowHeight)
                rows[r][k].frame = frame
                caps[i].frame = frame
                if rows[r][k].kind == .globe { globeButton?.frame = frame }
                x += w + gap
                i += 1
            }
        }
    }

    /// The key under a point: its row by height, then the nearest key in that row, so gaps never swallow a tap.
    private func keyIndex(at point: CGPoint) -> (row: Int, index: Int)? {
        guard !rows.isEmpty else { return nil }
        let rowHeight = (bounds.height - topInset) / CGFloat(rows.count)
        let r = min(rows.count - 1, max(0, Int((point.y - topInset) / rowHeight)))
        var best: (Int, CGFloat)?
        for (k, key) in rows[r].enumerated() where key.kind != .spacer {
            let d = point.x < key.frame.minX ? key.frame.minX - point.x : point.x > key.frame.maxX ? point.x - key.frame.maxX : 0
            if best == nil || d < best!.1 { best = (k, d) }
        }
        return best.map { (r, $0.0) }
    }

    private func flatIndex(_ p: (row: Int, index: Int)) -> Int {
        rows[0..<p.row].reduce(0) { $0 + $1.count } + p.index
    }

    // MARK: touches

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            guard let p = keyIndex(at: touch.location(in: self)) else { continue }
            let kind = rows[p.row][p.index].kind
            // A second finger lands while a letter is still held: type that letter now.
            for (other, q) in touchKeys where other !== touch {
                if case .char = rows[q.row][q.index].kind { release(other); commit(q) }
            }
            touchKeys[touch] = p
            press(p, true)
            actions.feedback()
            switch kind {
            case .delete:
                actions.delete()
                startDeleteRepeat()
            case .space:
                spaceStart = touch.location(in: self)
                spaceMoved = 0
                trackpad = false
            case .shift:
                let now = Date()
                if now.timeIntervalSince(lastShiftTap) < 0.3 {
                    shift = .locked
                } else {
                    shift = shift == .off ? .on : .off
                }
                lastShiftTap = now
                actions.shiftChanged()
            default:
                break
            }
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            guard let p = touchKeys[touch] else { continue }
            let kind = rows[p.row][p.index].kind
            if kind == .space, let start = spaceStart {
                // Drag on the space bar moves the cursor, a character per 9 points.
                let dx = touch.location(in: self).x - start.x
                if !trackpad, abs(dx) > 14 { trackpad = true; setTrackpad(true) }
                if trackpad {
                    let steps = Int((dx - spaceMoved) / 9)
                    if steps != 0 {
                        actions.moveCursor(steps)
                        spaceMoved += CGFloat(steps) * 9
                    }
                }
                continue
            }
            guard case .char = kind, let q = keyIndex(at: touch.location(in: self)), q != p,
                  case .char = rows[q.row][q.index].kind else { continue }
            press(p, false)
            touchKeys[touch] = q
            press(q, true)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            // Released before acting: a page key rebuilds the rows this index points into.
            guard let p = touchKeys[touch] else { continue }
            release(touch)
            switch rows[p.row][p.index].kind {
            case .space:
                if !trackpad { actions.space() }
                setTrackpad(false)
                spaceStart = nil
            case .delete:
                stopDeleteRepeat()
            case .shift, .globe, .spacer:
                break
            default:
                commit(p)
            }
        }
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            if let p = touchKeys[touch], rows[p.row][p.index].kind == .delete { stopDeleteRepeat() }
            if trackpad { setTrackpad(false) }
            release(touch)
        }
    }

    private func release(_ touch: UITouch) {
        if let p = touchKeys.removeValue(forKey: touch) { press(p, false) }
    }

    private func commit(_ p: (row: Int, index: Int)) {
        switch rows[p.row][p.index].kind {
        case .char(let c):
            actions.type(page == .letters ? cased(c) : c)
            if shift == .on { shift = .off }
        case .ret:
            actions.ret()
        case .page(let target):
            page = target
        default:
            break
        }
    }

    private func press(_ p: (row: Int, index: Int), _ down: Bool) {
        let i = flatIndex(p)
        guard i < caps.count else { return }
        let kind = rows[p.row][p.index].kind
        if case .char(let c) = kind {
            caps[i].setPressed(false)
            showPopup(down ? (page == .letters ? cased(c) : c) : nil, over: rows[p.row][p.index].frame)
        } else {
            caps[i].setPressed(down)
        }
    }

    /// The enlarged letter above the finger, as on the system keyboard.
    private func showPopup(_ text: String?, over frame: CGRect) {
        guard let text else { popup?.removeFromSuperview(); popup = nil; return }
        let label = popup ?? UILabel()
        label.text = text
        label.font = .systemFont(ofSize: 34)
        label.textAlignment = .center
        label.textColor = .label
        label.backgroundColor = KeyCap.letterColor
        label.layer.cornerRadius = 10
        label.layer.masksToBounds = true
        let w = max(frame.width + 18, 46)
        label.frame = CGRect(x: frame.midX - w / 2, y: frame.minY - 50, width: w, height: 50)
        label.frame.origin.x = min(max(label.frame.minX, 0), bounds.width - w)
        if popup == nil { addSubview(label) }
        popup = label
    }

    private func setTrackpad(_ on: Bool) {
        trackpad = on
        UIView.animate(withDuration: 0.15) {
            for cap in self.caps { cap.setBlank(on) }
        }
    }

    private func startDeleteRepeat() {
        deleteRepeats = 0
        let timer = Timer(timeInterval: 0.09, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.deleteRepeats += 1
            // After a moment of single characters, whole words go.
            if self.deleteRepeats > 14 {
                if self.deleteRepeats % 3 == 0 { self.actions.deleteWord() }
            } else {
                self.actions.delete()
            }
        }
        timer.fireDate = Date().addingTimeInterval(0.45)
        RunLoop.main.add(timer, forMode: .common)
        deleteTimer = timer
    }

    private func stopDeleteRepeat() {
        deleteTimer?.invalidate()
        deleteTimer = nil
    }
}

/// One key: a rounded rectangle with a letter, a word or an SF Symbol.
final class KeyCap: UIView {
    enum Face: Equatable {
        case text(String, size: CGFloat)
        case symbol(String)
    }

    static let letterColor = UIColor { $0.userInterfaceStyle == .dark ? UIColor(white: 0.42, alpha: 1) : .white }
    static let functionColor = UIColor { $0.userInterfaceStyle == .dark ? UIColor(white: 0.27, alpha: 1) : UIColor(red: 0.67, green: 0.69, blue: 0.73, alpha: 1) }
    static let accentColor = UIColor(red: 0.545, green: 0.361, blue: 0.965, alpha: 1)

    private let label = UILabel()
    private let image = UIImageView()
    private var function = false
    private var highlighted = false
    private var accent = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        layer.cornerRadius = 6
        layer.cornerCurve = .continuous
        layer.shadowColor = UIColor.black.cgColor
        layer.shadowOpacity = 0.3
        layer.shadowRadius = 0
        layer.shadowOffset = CGSize(width: 0, height: 1)
        label.textAlignment = .center
        label.adjustsFontSizeToFitWidth = true
        label.minimumScaleFactor = 0.6
        image.contentMode = .center
        addSubview(label)
        addSubview(image)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func configure(_ face: Face, function: Bool, highlighted: Bool, accent: Bool) {
        self.function = function
        self.highlighted = highlighted
        self.accent = accent
        switch face {
        case .text(let text, let size):
            label.text = text
            label.font = .systemFont(ofSize: size)
            label.isHidden = false
            image.isHidden = true
        case .symbol(let name):
            image.image = UIImage(systemName: name, withConfiguration: UIImage.SymbolConfiguration(pointSize: 19, weight: .regular))
            image.isHidden = false
            label.isHidden = true
        }
        setPressed(false)
    }

    func setPressed(_ pressed: Bool) {
        let tint: UIColor = accent ? .white : .label
        label.textColor = tint
        image.tintColor = tint
        if accent {
            backgroundColor = pressed ? KeyCap.accentColor.withAlphaComponent(0.7) : KeyCap.accentColor
        } else if highlighted {
            backgroundColor = KeyCap.letterColor
        } else if function {
            backgroundColor = pressed ? KeyCap.letterColor : KeyCap.functionColor
        } else {
            backgroundColor = pressed ? KeyCap.functionColor : KeyCap.letterColor
        }
    }

    /// While the space bar moves the cursor, the keys go blank like the system's trackpad mode.
    func setBlank(_ blank: Bool) {
        label.alpha = blank ? 0 : 1
        image.alpha = blank ? 0 : 1
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        label.frame = bounds.insetBy(dx: 2, dy: 0)
        image.frame = bounds
    }
}
