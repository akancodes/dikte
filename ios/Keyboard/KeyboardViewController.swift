import SwiftUI
import UIKit

/// Dikte's keyboard. It cannot use the microphone (iOS forbids it in keyboards), so it asks the app, which
/// listens in the background during a session, and types what comes back. With no session running it opens
/// the app, which starts one and listens straight away.
final class KeyboardViewController: UIInputViewController {
    private let model = KeyboardModel()
    private var host: UIHostingController<KeyboardView>?

    override func viewDidLoad() {
        super.viewDidLoad()
        model.controller = self
        let host = UIHostingController(rootView: KeyboardView(model: model))
        host.view.backgroundColor = .clear
        host.view.translatesAutoresizingMaskIntoConstraints = false
        addChild(host)
        view.addSubview(host.view)
        let height = host.view.heightAnchor.constraint(equalToConstant: 236)
        height.priority = UILayoutPriority(999)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            host.view.topAnchor.constraint(equalTo: view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            height,
        ])
        host.didMove(toParent: self)
        self.host = host
        KeyboardModel.listen()
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        KeyboardModel.current = model
        model.appeared(fullAccess: hasFullAccess, needsGlobe: needsInputModeSwitchKey)
    }

    override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        model.disappeared()
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

/// What the keyboard shows, and the conversation with the app.
@MainActor
final class KeyboardModel: ObservableObject {
    enum Status: Equatable {
        case idle, connecting, listening, working
        case message(String)
    }

    /// The keyboard on screen now; Darwin notifications go to it.
    static weak var current: KeyboardModel?
    private static var listening = false

    @Published private(set) var status: Status = .idle
    @Published private(set) var fullAccess = true
    @Published private(set) var needsGlobe = true
    @Published private(set) var lastText: String?

    weak var controller: KeyboardViewController?
    private var appAlive = false
    private var pingAt = Date.distantPast
    private var pressedAt = Date.distantPast
    private var startedByPress = false
    private var waitTask: Task<Void, Never>?
    private let defaults = UserDefaults.standard
    private let haptic = UIImpactFeedbackGenerator(style: .medium)

    /// Signals are registered once per keyboard process and passed to whichever keyboard is showing.
    static func listen() {
        guard !listening else { return }
        listening = true
        SignalCenter.shared.on(.pong) { KeyboardModel.current?.appAlive = true }
        SignalCenter.shared.on(.changed) { KeyboardModel.current?.refresh() }
    }

    func appeared(fullAccess: Bool, needsGlobe: Bool) {
        self.fullAccess = fullAccess
        self.needsGlobe = needsGlobe
        SharedStore.write(KeyboardSeen(fullAccess: fullAccess), "keyboard")
        lastText = SharedStore.read(Dictation.self, "dictation")?.text
        ping()
        refresh()
    }

    func disappeared() {
        waitTask?.cancel()
    }

    private func ping() {
        appAlive = false
        pingAt = Date()
        Bridge.post(.ping)
    }

    // MARK: the button

    /// Tap: start, and tap again to send. Hold: talk while holding, send on release.
    func pressDown() {
        pressedAt = Date()
        startedByPress = false
        guard fullAccess else {
            status = .message("Turn on Allow Full Access: Settings → Dikte → Keyboards.")
            return
        }
        switch status {
        case .listening:
            return // the release sends
        case .working, .connecting:
            return
        default:
            startedByPress = true
            begin()
        }
    }

    func pressUp() {
        let held = Date().timeIntervalSince(pressedAt)
        if case .listening = status {
            // A press that started it and was held is hold-to-talk; any other press sends.
            if !startedByPress || held > 0.45 { send() }
        }
    }

    private func begin() {
        haptic.impactOccurred()
        defaults.set(Date().timeIntervalSince1970, forKey: "awaitingSince")
        status = .connecting
        Bridge.post(.start)
        // The app answers by going to "listening"; if it does not, there is no session: open the app.
        waitTask?.cancel()
        waitTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(700))
            guard let self, !Task.isCancelled, self.status == .connecting else { return }
            self.status = .idle
            if self.controller?.openApp() != true {
                self.status = .message("Open the Dikte app and tap Start, then come back.")
            }
        }
    }

    private func send() {
        haptic.impactOccurred()
        status = .working
        Bridge.post(.stop)
    }

    func cancel() {
        Bridge.post(.cancel)
        defaults.removeObject(forKey: "awaitingSince")
        status = .idle
    }

    // MARK: what the app says

    func refresh() {
        guard let state = SharedStore.read(LiveState.self, "state") else { return }
        switch state.phase {
        case .listening:
            waitTask?.cancel()
            status = .listening
        case .working:
            status = .working
        case .ready, .off:
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
        controller?.textDocumentProxy.insertText(dictation.text)
    }

    // MARK: the other keys

    func insertLast() {
        guard let text = lastText else { return }
        controller?.textDocumentProxy.insertText(text)
    }

    func space() { controller?.textDocumentProxy.insertText(" ") }
    func newline() { controller?.textDocumentProxy.insertText("\n") }
    func backspace() { controller?.textDocumentProxy.deleteBackward() }
    func nextKeyboard() { controller?.advanceToNextInputMode() }
}

struct KeyboardView: View {
    @ObservedObject var model: KeyboardModel
    @State private var pressing = false
    @State private var deleteTimer: Timer?

    private static let violet = Color(red: 0.545, green: 0.361, blue: 0.965)
    private static let red = Color(red: 0.957, green: 0.247, blue: 0.369)
    private static let blue = Color(red: 0.231, green: 0.510, blue: 0.965)

    var body: some View {
        VStack(spacing: 10) {
            Text(caption)
                .font(.footnote.weight(.medium))
                .foregroundStyle(.secondary)
                .lineLimit(2)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity, minHeight: 34)
                .padding(.horizontal, 12)

            bigButton

            HStack(spacing: 8) {
                if model.needsGlobe {
                    key(Image(systemName: "globe"), width: 48) { model.nextKeyboard() }
                }
                if case .listening = model.status {
                    key(Text("Cancel").font(.subheadline.weight(.semibold)), width: 78) { model.cancel() }
                } else if model.lastText != nil {
                    key(Label("Last", systemImage: "arrow.uturn.backward").font(.subheadline.weight(.semibold)), width: 78) {
                        model.insertLast()
                    }
                }
                key(Text("space").font(.subheadline), width: nil) { model.space() }
                deleteKey
                key(Image(systemName: "return"), width: 56) { model.newline() }
            }
            .frame(height: 44)
        }
        .padding(.horizontal, 8)
        .padding(.top, 4)
        .padding(.bottom, 6)
    }

    private var bigButton: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .fill(LinearGradient(colors: [color, color.opacity(0.78)], startPoint: .topLeading, endPoint: .bottomTrailing))
                .shadow(color: color.opacity(0.4), radius: pressing ? 4 : 10, y: 3)
            HStack(spacing: 12) {
                switch model.status {
                case .working, .connecting:
                    ProgressView().tint(.white)
                case .listening:
                    Image(systemName: "stop.fill").font(.system(size: 26, weight: .bold))
                default:
                    Image(systemName: "waveform").font(.system(size: 30, weight: .bold))
                }
                Text(label).font(.title3.bold())
            }
            .foregroundStyle(.white)
        }
        .frame(maxWidth: .infinity)
        .frame(height: 112)
        .scaleEffect(pressing ? 0.97 : 1)
        .animation(.easeOut(duration: 0.1), value: pressing)
        .contentShape(Rectangle())
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard !pressing else { return }
                    pressing = true
                    model.pressDown()
                }
                .onEnded { _ in
                    pressing = false
                    model.pressUp()
                }
        )
        .accessibilityAddTraits(.isButton)
        .accessibilityLabel(label)
    }

    private var deleteKey: some View {
        Image(systemName: "delete.left")
            .frame(width: 56, height: 44)
            .background(Color(uiColor: .secondarySystemFill), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        guard deleteTimer == nil else { return }
                        model.backspace()
                        // Held: repeat after a short pause, like the system keyboard.
                        let timer = Timer(timeInterval: 0.09, repeats: true) { _ in Task { @MainActor in model.backspace() } }
                        timer.fireDate = Date().addingTimeInterval(0.45)
                        RunLoop.main.add(timer, forMode: .common)
                        deleteTimer = timer
                    }
                    .onEnded { _ in
                        deleteTimer?.invalidate()
                        deleteTimer = nil
                    }
            )
            .accessibilityAddTraits(.isButton)
            .accessibilityLabel("Delete")
    }

    private func key<Content: View>(_ content: Content, width: CGFloat?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            content
                .frame(maxWidth: width ?? .infinity, minHeight: 44)
                .frame(width: width)
                .background(Color(uiColor: .secondarySystemFill), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .buttonStyle(.plain)
        .foregroundStyle(.primary)
    }

    private var color: Color {
        switch model.status {
        case .listening: return Self.red
        case .working: return Self.blue
        default: return Self.violet
        }
    }

    private var label: String {
        switch model.status {
        case .listening: return "Tap to send"
        case .working: return "Writing…"
        case .connecting: return "Starting…"
        default: return "Tap or hold to talk"
        }
    }

    private var caption: String {
        switch model.status {
        case .message(let text): return text
        case .listening: return "Listening. Hold-to-talk sends when you let go."
        case .working: return "The text appears here in a moment."
        default: return model.fullAccess ? "Dikte" : "Turn on Allow Full Access: Settings → Dikte → Keyboards."
        }
    }
}
