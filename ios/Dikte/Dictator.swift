import AVFoundation
import DikteCore
import SwiftUI
import UIKit

/// Files in the app's own container.
enum Paths {
    static let dir: URL = {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }()
    static let settings = dir.appendingPathComponent("settings.json")
    static let history = dir.appendingPathComponent("history.json")
    static let stats = dir.appendingPathComponent("stats.json")
}

/// The OpenAI API key, in the app's private keychain group: the keyboard never sees it.
enum KeyStore {
    static func get() -> String? {
        SharedStore.read(String.self, "openai-api-key", group: Bridge.privateGroup)
    }

    static func set(_ key: String?) {
        if let key, !key.isEmpty {
            SharedStore.write(key, "openai-api-key", group: Bridge.privateGroup)
        } else {
            SharedStore.delete("openai-api-key", group: Bridge.privateGroup)
        }
    }
}

/// The whole loop: a session keeps the microphone open, the keyboard (or the big button here) starts and
/// stops a dictation, the recording goes to the speech model, and the text goes back to the keyboard.
@MainActor
final class Dictator: ObservableObject {
    enum Source { case keyboard, app }

    @Published private(set) var phase: LiveState.Phase = .off
    @Published private(set) var sessionEnds: Date?
    @Published private(set) var level: Float = 0
    @Published private(set) var elapsed: Double = 0
    @Published private(set) var source: Source = .app
    /// Shown on the main screen until the next dictation.
    @Published var notice: String?
    /// "Copied" shows on the big button for a moment.
    @Published private(set) var justCopied = false
    @Published private(set) var history: [HistoryEntry] = []
    @Published private(set) var week: StatsSummary
    @Published private(set) var hasKey = KeyStore.get() != nil
    @Published private(set) var keyboard: KeyboardSeen?
    /// Opened from the keyboard: the screen says to go back.
    @Published var cameFromKeyboard = false

    @Published private(set) var settings: Settings
    /// How long the microphone stays open after the last dictation; 0 keeps it open until it is ended here.
    @Published var sessionMinutes = UserDefaults.standard.object(forKey: "sessionMinutes") as? Int ?? 60 {
        didSet {
            UserDefaults.standard.set(sessionMinutes, forKey: "sessionMinutes")
            if sessionOn { extend(); publish() }
            writeKeyboardPrefs()
        }
    }
    /// The keyboard's letters: "tr" (Turkish Q, which has every English letter too) or "en".
    @Published var keyboardLayout = UserDefaults.standard.string(forKey: "keyboardLayout") ?? "tr" {
        didSet {
            UserDefaults.standard.set(keyboardLayout, forKey: "keyboardLayout")
            writeKeyboardPrefs()
        }
    }

    private let mic = Microphone()
    private let api = Transcriber()
    private let historyStore: History
    private let stats: Stats
    private var ticker: Timer?
    private var startedAt = Date()
    private var pending = 0

    init() {
        let settings = (try? Settings.loadOrCreate(Paths.settings)) ?? Settings()
        self.settings = settings
        let historyStore = History(url: Paths.history, size: max(settings.historySize, 50))
        self.historyStore = historyStore
        history = historyStore.items
        let stats = Stats(url: Paths.stats)
        self.stats = stats
        week = stats.summary(now: Date())
        keyboard = SharedStore.read(KeyboardSeen.self, "keyboard")

        let signals = SignalCenter.shared
        signals.on(.start) { [weak self] in self?.start(from: .keyboard) }
        signals.on(.stop) { [weak self] in self?.stop() }
        signals.on(.cancel) { [weak self] in self?.cancel() }

        let center = NotificationCenter.default
        center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            let type = (note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt).flatMap(AVAudioSession.InterruptionType.init)
            guard type == .began else { return }
            Task { @MainActor in self?.endSession(because: "A call or another app took the microphone. Open Dikte to start again.") }
        }
        center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.routeChanged() }
        }
        center.addObserver(forName: AVAudioSession.mediaServicesWereResetNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.endSession(because: "iOS restarted its audio. Open Dikte to start again.") }
        }
        publish()
        writeKeyboardPrefs()
    }

    private func writeKeyboardPrefs() {
        SharedStore.write(KeyboardPrefs(layout: keyboardLayout, sessionMinutes: sessionMinutes), "keyboardPrefs")
    }

    // MARK: the session

    var sessionOn: Bool { phase != .off }

    /// Opens the microphone for `sessionMinutes`; only possible while the app is on screen.
    @discardableResult
    func startSession() async -> Bool {
        if sessionOn { extend(); return true }
        guard await AVAudioApplication.requestRecordPermission() else {
            notice = "Dikte needs the microphone. Turn it on in Settings → Dikte."
            return false
        }
        do {
            try mic.open()
        } catch {
            notice = error.localizedDescription
            return false
        }
        phase = .ready
        extend()
        startTicker()
        publish()
        return true
    }

    func endSession(because message: String? = nil) {
        guard sessionOn else { return }
        _ = mic.endCapture()
        mic.close()
        ticker?.invalidate()
        ticker = nil
        phase = .off
        sessionEnds = nil
        level = 0
        if let message { notice = message }
        publish(message: message)
    }

    private func extend() {
        sessionEnds = sessionMinutes == 0 ? nil : Date().addingTimeInterval(TimeInterval(sessionMinutes * 60))
    }

    private func routeChanged() {
        guard sessionOn, !mic.isRunning else { return }
        do {
            try mic.restart()
        } catch {
            endSession(because: "The microphone went away. Open Dikte to start again.")
        }
    }

    private func startTicker() {
        ticker?.invalidate()
        let timer = Timer(timeInterval: 0.08, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        ticker = timer
    }

    private func tick() {
        level = mic.level
        if phase == .listening {
            elapsed = Date().timeIntervalSince(startedAt)
            if elapsed >= Double(settings.maxMinutes * 60) { stop() }
        } else if phase == .ready, let ends = sessionEnds, Date() >= ends {
            endSession()
        }
    }

    // MARK: one dictation

    /// Opened through dikte://listen: the keyboard had no session to ask. Open the microphone and send him back;
    /// the keyboard starts listening itself once it is on screen again, so nothing is recorded on the way.
    func openedFromKeyboard() async {
        cameFromKeyboard = true
        if !(await startSession()) { cameFromKeyboard = false }
    }

    func start(from source: Source) {
        guard phase != .listening else { return }
        guard hasKey else {
            notice = "Add your OpenAI API key in Dikte's settings first."
            publish(message: notice)
            return
        }
        guard sessionOn else {
            // The button here can open the microphone itself; a keyboard can only reach a running session.
            if source == .app { Task { if await startSession() { start(from: .app) } } }
            return
        }
        self.source = source
        notice = nil
        mic.beginCapture()
        startedAt = Date()
        elapsed = 0
        phase = .listening
        extend()
        publish()
    }

    func stop() {
        guard phase == .listening else { return }
        let (wav, seconds, peak) = mic.endCapture()
        let source = self.source
        if seconds < 0.4 || peak < 0.01 {
            phase = pending > 0 ? .working : .ready
            notice = seconds < 0.4 ? nil : "Nothing was heard. Is the microphone covered?"
            publish(message: notice)
            return
        }
        pending += 1
        phase = .working
        extend()
        publish()
        let settings = self.settings
        Task {
            do {
                let text = try await transcribe(wav, settings)
                deliver(text, seconds: seconds, to: source)
            } catch is CancellationError {
            } catch {
                notice = error.localizedDescription
                publish(message: notice)
            }
            pending -= 1
            if phase == .working { phase = pending > 0 ? .working : .ready }
            publish()
        }
    }

    func cancel() {
        guard phase == .listening else { return }
        _ = mic.endCapture()
        phase = pending > 0 ? .working : .ready
        publish()
    }

    private func transcribe(_ wav: Data, _ settings: Settings) async throws -> String {
        guard let key = KeyStore.get() else { throw ApiError("Add your OpenAI API key in Dikte's settings first.") }
        var text = try await api.transcribe(wav: wav, settings: settings, apiKey: key)
        if settings.cleanup, !text.isEmpty {
            text = (try? await api.cleanup(text, settings: settings, apiKey: key)) ?? text
        }
        return text
    }

    private func deliver(_ text: String, seconds: Double, to source: Source) {
        guard !text.isEmpty else {
            notice = "The model heard no words."
            publish(message: notice)
            return
        }
        historyStore.add(text)
        history = historyStore.items
        stats.record(at: Date(), words: text.split(whereSeparator: \.isWhitespace).count, seconds: seconds, model: settings.model)
        week = stats.summary(now: Date())
        switch source {
        case .keyboard:
            SharedStore.write(Dictation(text: settings.appendSpace ? text + " " : text), "dictation")
        case .app:
            copy(text)
        }
    }

    /// Tells the keyboard what is happening now.
    private func publish(message: String? = nil) {
        var state = LiveState(phase: phase, sessionEnds: sessionEnds)
        if let message {
            state.message = message
            state.messageAt = Date()
        }
        SharedStore.write(state, "state")
        Bridge.post(.changed)
    }

    // MARK: settings and the rest of the screen

    func update(_ change: (inout Settings) -> Void) {
        var next = settings
        change(&next)
        guard next != settings else { return }
        settings = next
        try? next.save(Paths.settings)
    }

    func saveKey(_ key: String) async -> String? {
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty else { return "Paste a key first." }
        if let problem = await api.checkKey(settings: settings, apiKey: key) { return problem }
        KeyStore.set(key)
        hasKey = true
        return nil
    }

    func forgetKey() {
        KeyStore.set(nil)
        hasKey = false
    }

    func copy(_ text: String) {
        UIPasteboard.general.string = text
        justCopied = true
        Task {
            try? await Task.sleep(for: .seconds(2.5))
            justCopied = false
        }
    }

    func clearHistory() {
        historyStore.clear()
        history = []
    }

    /// The keyboard writes when it last ran and whether it had Full Access; read again on every return.
    func refreshKeyboard() {
        keyboard = SharedStore.read(KeyboardSeen.self, "keyboard")
    }

    /// Whether Dikte is among the keyboards in Settings → General → Keyboard.
    var keyboardAdded: Bool {
        let list = UserDefaults.standard.array(forKey: "AppleKeyboards") as? [String] ?? []
        return list.contains { $0.hasPrefix(Bridge.prefix + ".keyboard") } || keyboard != nil
    }
}
