import Combine
import SwiftUI
import UIKit

/// The keyboard's own view: a real input view, so key clicks follow the iPhone's "Keyboard Clicks" setting.
final class ClickingInputView: UIInputView, UIInputViewAudioFeedback {
    var enableInputClicksWhenVisible: Bool { true }
}

/// Dikte's keyboard: a full keyboard to type and fix with, and a microphone strip on top. It cannot use the
/// microphone itself (iOS forbids it in keyboards), so it asks the app, which listens in the background during a
/// session, and types what comes back.
final class KeyboardViewController: UIInputViewController {
    private let model = KeyboardModel()
    private let keys = KeysView()
    private var toolbarHost: UIHostingController<ToolbarView>?
    private var panelHost: UIHostingController<PanelView>?
    private var lastSpaceAt = Date.distantPast
    private let tap = UIImpactFeedbackGenerator(style: .light)
    private var watching: AnyCancellable?

    static let toolbarHeight: CGFloat = 48
    static let keysHeight: CGFloat = 216

    override func loadView() {
        let input = ClickingInputView(frame: .zero, inputViewStyle: .keyboard)
        input.allowsSelfSizing = true
        view = input
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        model.controller = self

        let toolbar = UIHostingController(rootView: ToolbarView(model: model))
        let panel = UIHostingController(rootView: PanelView(model: model))
        // No safe-area padding: it pushed the strip's content down onto the keys.
        toolbar.safeAreaRegions = []
        panel.safeAreaRegions = []
        for host in [toolbar, panel] as [UIViewController] {
            host.view.backgroundColor = .clear
            host.view.translatesAutoresizingMaskIntoConstraints = false
            addChild(host)
        }
        keys.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(toolbar.view)
        view.addSubview(keys)
        view.addSubview(panel.view)
        toolbar.didMove(toParent: self)
        panel.didMove(toParent: self)
        panel.view.isHidden = true
        toolbarHost = toolbar
        panelHost = panel

        let height = view.heightAnchor.constraint(equalToConstant: Self.toolbarHeight + Self.keysHeight + 6)
        height.priority = UILayoutPriority(999)
        NSLayoutConstraint.activate([
            height,
            toolbar.view.topAnchor.constraint(equalTo: view.topAnchor),
            toolbar.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            toolbar.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            toolbar.view.heightAnchor.constraint(equalToConstant: Self.toolbarHeight),
            keys.topAnchor.constraint(equalTo: toolbar.view.bottomAnchor),
            keys.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            keys.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            keys.heightAnchor.constraint(equalToConstant: Self.keysHeight),
            panel.view.topAnchor.constraint(equalTo: keys.topAnchor, constant: -2),
            panel.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            panel.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            panel.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])

        keys.attachGlobe(target: self, action: #selector(handleInputModeList(from:with:)))
        keys.actions = KeysView.Actions(
            type: { [weak self] text in self?.insert(text) },
            delete: { [weak self] in
                self?.textDocumentProxy.deleteBackward()
                self?.edited()
            },
            deleteWord: { [weak self] in self?.deleteWord() },
            space: { [weak self] in self?.space() },
            ret: { [weak self] in self?.insert("\n") },
            moveCursor: { [weak self] steps in
                self?.textDocumentProxy.adjustTextPosition(byCharacterOffset: steps)
                self?.model.updateSuggestions()
            },
            shiftChanged: {},
            feedback: { [weak self] in self?.feedback() }
        )

        watching = model.$panel.receive(on: RunLoop.main).sink { [weak self] panel in
            self?.panelHost?.view.isHidden = panel == nil
        }
        KeyboardModel.listen()
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        KeyboardModel.current = model
        let prefs = SharedStore.read(KeyboardPrefs.self, "keyboardPrefs") ?? KeyboardPrefs()
        keys.layout = KeysView.Layout(rawValue: prefs.layout) ?? .turkish
        model.layout = keys.layout
        keys.needsGlobe = needsInputModeSwitchKey
        updateTraits()
        edited()
        model.appeared(fullAccess: hasFullAccess, prefs: prefs)
        tap.prepare()
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        model.disappeared()
    }

    override func textDidChange(_ textInput: UITextInput?) {
        super.textDidChange(textInput)
        updateTraits()
        edited()
    }

    override func selectionDidChange(_ textInput: UITextInput?) {
        super.selectionDidChange(textInput)
        edited()
    }

    // MARK: typing

    private var proxy: UITextDocumentProxy { textDocumentProxy }

    var contextBefore: String { proxy.documentContextBeforeInput ?? "" }

    func insert(_ text: String) {
        proxy.insertText(text)
        edited()
    }

    /// Two spaces after a word make ". ", and a space from the number pages goes back to the letters.
    private func space() {
        let before = contextBefore
        if Date().timeIntervalSince(lastSpaceAt) < 0.8, before.hasSuffix(" "),
           let previous = before.dropLast().last, previous.isLetter || previous.isNumber {
            proxy.deleteBackward()
            proxy.insertText(". ")
            lastSpaceAt = .distantPast
        } else {
            proxy.insertText(" ")
            lastSpaceAt = Date()
        }
        if keys.page != .letters { keys.page = .letters }
        edited()
    }

    private func deleteWord() {
        var chars = Array(contextBefore)
        var count = 0
        while let c = chars.last, c.isWhitespace { chars.removeLast(); count += 1 }
        while let c = chars.last, !c.isWhitespace { chars.removeLast(); count += 1 }
        for _ in 0..<max(count, 1) { proxy.deleteBackward() }
        edited()
    }

    /// Swaps the word being typed for a suggestion.
    func replaceCurrentWord(with word: String) {
        let current = Speller.currentWord(contextBefore)
        for _ in 0..<current.count { proxy.deleteBackward() }
        proxy.insertText(word + " ")
        edited()
    }

    private func edited() {
        if keys.shift != .locked { keys.shift = shouldCapitalize() ? .on : .off }
        model.updateSuggestions()
    }

    private func feedback() {
        if hasFullAccess { tap.impactOccurred(intensity: 0.55) }
        UIDevice.current.playInputClick()
    }

    private func shouldCapitalize() -> Bool {
        let type = proxy.autocapitalizationType ?? .sentences
        if type == .none { return false }
        if type == .allCharacters { return true }
        let before = contextBefore
        guard let last = before.last else { return true }
        if type == .words { return last.isWhitespace }
        if last.isNewline { return true }
        guard last.isWhitespace else { return false }
        guard let end = before.trimmingCharacters(in: .whitespaces).last else { return true }
        return ".!?".contains(end) || end.isNewline
    }

    private func updateTraits() {
        switch proxy.returnKeyType ?? .default {
        case .go: keys.returnTitle = "go"
        case .google, .yahoo, .search: keys.returnTitle = "search"
        case .join: keys.returnTitle = "join"
        case .next: keys.returnTitle = "next"
        case .route: keys.returnTitle = "route"
        case .send: keys.returnTitle = "send"
        case .done: keys.returnTitle = "done"
        case .continue: keys.returnTitle = "continue"
        default: keys.returnTitle = "return"
        }
        switch proxy.keyboardType ?? .default {
        case .emailAddress: keys.addressKeys = ["@", "."]
        case .URL, .webSearch: keys.addressKeys = ["/", "."]
        case .twitter: keys.addressKeys = ["@", "#"]
        default: keys.addressKeys = []
        }
        let numeric: [UIKeyboardType] = [.numberPad, .decimalPad, .numbersAndPunctuation, .asciiCapableNumberPad]
        if let type = proxy.keyboardType, numeric.contains(type), keys.page == .letters, contextBefore.isEmpty {
            keys.page = .numbers
        }
        view.overrideUserInterfaceStyle = proxy.keyboardAppearance == .dark ? .dark : .unspecified
    }

    /// Keyboards may not open URLs, but the app object at the end of the responder chain can. This is how
    /// dictation keyboards reach their app; if a future iOS closes it, the keyboard says to open Dikte by hand.
    func openApp() -> Bool {
        let selector = NSSelectorFromString("openURL:options:completionHandler:")
        var responder: UIResponder? = self
        while let current = responder {
            if let app = current as? UIApplication, app.responds(to: selector) {
                typealias Open = @convention(c) (AnyObject, Selector, NSURL, NSDictionary, AnyObject?) -> Void
                let open = unsafeBitCast(app.method(for: selector), to: Open.self)
                open(app, selector, Bridge.listenURL as NSURL, NSDictionary(), nil)
                return true
            }
            responder = current.next
        }
        return false
    }
}

/// What the strip and the panels show, and the conversation with the app.
@MainActor
final class KeyboardModel: ObservableObject {
    enum Status: Equatable {
        case idle, connecting, listening, working
        case message(String)
    }

    enum Panel: Equatable {
        /// No session: what is about to happen, and the button that opens the app.
        case startSession
        case fullAccess
        case listening
    }

    /// The keyboard on screen now; Darwin notifications go to it.
    static weak var current: KeyboardModel?
    private static var listening = false

    @Published private(set) var status: Status = .idle
    @Published private(set) var panel: Panel?
    @Published private(set) var fullAccess = true
    @Published private(set) var lastText: String?
    @Published private(set) var sessionOn = false
    @Published private(set) var sessionMinutes = 60
    @Published private(set) var suggestions: [String] = []
    @Published private(set) var misspelled = false
    /// The mic is held down: letting go sends.
    @Published private(set) var holding = false
    @Published private(set) var listeningSince = Date()

    weak var controller: KeyboardViewController?
    var layout: KeysView.Layout = .turkish
    private var pressedAt = Date.distantPast
    private var startedByPress = false
    private var waitTask: Task<Void, Never>?
    private let defaults = UserDefaults.standard
    private let haptic = UIImpactFeedbackGenerator(style: .medium)

    /// Signals are registered once per keyboard process and passed to whichever keyboard is showing.
    static func listen() {
        guard !listening else { return }
        listening = true
        SignalCenter.shared.on(.changed) { KeyboardModel.current?.refresh() }
    }

    func appeared(fullAccess: Bool, prefs: KeyboardPrefs) {
        self.fullAccess = fullAccess
        sessionMinutes = prefs.sessionMinutes
        SharedStore.write(KeyboardSeen(fullAccess: fullAccess), "keyboard")
        lastText = SharedStore.read(Dictation.self, "dictation")?.text
        refresh()
        // Back from the app that just opened the microphone for us: listen without another tap.
        let autoStart = defaults.double(forKey: "autoStartUntil")
        if autoStart > Date().timeIntervalSince1970, sessionOn, status == .idle {
            defaults.removeObject(forKey: "autoStartUntil")
            begin()
        }
    }

    func disappeared() {
        waitTask?.cancel()
        if status == .connecting { status = .idle }
    }

    // MARK: the microphone

    /// Tap: start, and tap again to send. Hold: talk while holding, send on release.
    func micDown() {
        pressedAt = Date()
        startedByPress = false
        guard fullAccess else {
            panel = .fullAccess
            return
        }
        switch status {
        case .listening, .working, .connecting:
            return // a release while listening sends
        default:
            guard sessionOn else {
                panel = .startSession
                return
            }
            startedByPress = true
            holding = true
            begin()
        }
    }

    func micUp() {
        let held = Date().timeIntervalSince(pressedAt)
        holding = false
        if case .listening = status {
            // A press that started it and was held is hold-to-talk; any other press sends.
            if !startedByPress || held > 0.45 { send() }
        }
    }

    private func begin() {
        haptic.impactOccurred()
        panel = nil
        defaults.set(Date().timeIntervalSince1970, forKey: "awaitingSince")
        status = .connecting
        Bridge.post(.start)
        // The app answers by going to "listening"; if it does not, its session is gone.
        waitTask?.cancel()
        waitTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(800))
            guard let self, !Task.isCancelled, self.status == .connecting else { return }
            self.status = .idle
            self.holding = false
            self.sessionOn = false
            self.panel = .startSession
        }
    }

    func send() {
        haptic.impactOccurred()
        status = .working
        panel = nil
        Bridge.post(.stop)
    }

    func cancel() {
        Bridge.post(.cancel)
        defaults.removeObject(forKey: "awaitingSince")
        status = .idle
        panel = nil
        holding = false
    }

    /// The panel's "Open Dikte": the app opens and turns the microphone on, and this keyboard starts listening
    /// when it is back.
    func openApp() {
        defaults.set(Date().timeIntervalSince1970 + 180, forKey: "autoStartUntil")
        panel = nil
        if controller?.openApp() != true {
            status = .message("Open the Dikte app yourself, then come back.")
        }
    }

    func closePanel() {
        panel = nil
        defaults.removeObject(forKey: "autoStartUntil")
    }

    // MARK: what the app says

    func refresh() {
        guard let state = SharedStore.read(LiveState.self, "state") else { return }
        sessionOn = state.phase != .off
        switch state.phase {
        case .listening:
            waitTask?.cancel()
            if status != .listening { listeningSince = Date() }
            status = .listening
            panel = .listening
        case .working:
            status = .working
            if panel == .listening { panel = nil }
        case .ready, .off:
            if panel == .listening { panel = nil }
            if case .connecting = status { break } // still waiting for the app to answer
            if let message = state.message, let at = state.messageAt, Date().timeIntervalSince(at) < 8 {
                status = .message(message)
            } else if case .message = status {
                break
            } else {
                status = .idle
            }
        }
        insertIfAwaited()
    }

    /// Types the newest dictation if this keyboard asked for it and has not typed it yet.
    private func insertIfAwaited() {
        guard let dictation = SharedStore.read(Dictation.self, "dictation") else { return }
        lastText = dictation.text
        let since = defaults.double(forKey: "awaitingSince")
        guard since > 0, dictation.at.timeIntervalSince1970 >= since,
              Date().timeIntervalSince1970 - since < 15 * 60,
              defaults.string(forKey: "lastInserted") != dictation.id.uuidString else { return }
        defaults.set(dictation.id.uuidString, forKey: "lastInserted")
        defaults.removeObject(forKey: "awaitingSince")
        controller?.insert(dictation.text)
    }

    // MARK: the strip

    func insertLast() {
        guard let text = lastText else { return }
        controller?.insert(text)
    }

    func choose(_ suggestion: String) {
        controller?.replaceCurrentWord(with: suggestion)
    }

    func dismissMessage() {
        if case .message = status { status = .idle }
    }

    #if DEBUG
    /// Puts the model in a given state, for the snapshot tests.
    func stage(status: Status = .idle, panel: Panel? = nil, suggestions: [String] = [], misspelled: Bool = false,
               sessionOn: Bool = true, lastText: String? = nil) {
        self.status = status
        self.panel = panel
        self.suggestions = suggestions
        self.misspelled = misspelled
        self.sessionOn = sessionOn
        self.lastText = lastText
        listeningSince = Date().addingTimeInterval(-7)
    }
    #endif

    func updateSuggestions() {
        let word = Speller.currentWord(controller?.contextBefore ?? "")
        let result = Speller.suggestions(for: word, layout: layout)
        if result.items != suggestions { suggestions = result.items }
        if result.misspelled != misspelled { misspelled = result.misspelled }
    }
}

/// Suggestions from iOS's own dictionaries. Dikte never corrects by itself: a mixed Turkish and English
/// sentence would be "fixed" wrongly. A word counts as misspelled only when neither language knows it.
@MainActor
enum Speller {
    private static let checker = UITextChecker()

    static func languages(_ layout: KeysView.Layout) -> [String] {
        let available = UITextChecker.availableLanguages
        func find(_ code: String) -> String? {
            available.first { $0 == code } ?? available.first { $0.hasPrefix(String(code.prefix(2))) }
        }
        let order = layout == .turkish ? ["tr_TR", "en_US"] : ["en_US", "tr_TR"]
        var out: [String] = []
        for code in order { if let lang = find(code), !out.contains(lang) { out.append(lang) } }
        return out
    }

    static func currentWord(_ before: String) -> String {
        String(before.reversed().prefix { $0.isLetter || $0 == "'" || $0 == "’" }.reversed())
    }

    static func suggestions(for word: String, layout: KeysView.Layout) -> (items: [String], misspelled: Bool) {
        guard word.count >= 2 else { return ([], false) }
        let langs = languages(layout)
        guard let primary = langs.first else { return ([], false) }
        let range = NSRange(location: 0, length: (word as NSString).length)
        let known = langs.contains {
            checker.rangeOfMisspelledWord(in: word, range: range, startingAt: 0, wrap: false, language: $0).location == NSNotFound
        }
        var items: [String] = []
        if known {
            items = checker.completions(forPartialWordRange: range, in: word, language: primary) ?? []
        } else {
            for lang in langs { items += checker.guesses(forWordRange: range, in: word, language: lang) ?? [] }
        }
        let locale = Locale(identifier: layout == .turkish ? "tr_TR" : "en_US")
        let lowered = word.lowercased(with: locale)
        var seen = Set<String>()
        var out: [String] = []
        for item in items {
            let shaped = shape(item, like: word, locale)
            let key = shaped.lowercased(with: locale)
            guard key != lowered, !seen.contains(key) else { continue }
            seen.insert(key)
            out.append(shaped)
            if out.count == 3 { break }
        }
        return (out, !known)
    }

    /// "Merhaba" for a capitalised word, "MERHABA" for one in capitals.
    private static func shape(_ item: String, like word: String, _ locale: Locale) -> String {
        guard let first = word.first, first.isUppercase else { return item }
        if word.count > 1, word.allSatisfy({ !$0.isLetter || $0.isUppercase }) { return item.uppercased(with: locale) }
        return item.prefix(1).uppercased(with: locale) + item.dropFirst()
    }
}
