import AppKit
import AVFoundation
import ApplicationServices
import DikteCore
import Security
import ServiceManagement

enum Paths {
    static let dir: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Dikte", isDirectory: true)
    }()
    static let settings = dir.appendingPathComponent("settings.json")
    static let history = dir.appendingPathComponent("history.json")
    static let stats = dir.appendingPathComponent("stats.json")
    static let failedAudio = dir.appendingPathComponent("last-failed.wav")
    static let ducked = dir.appendingPathComponent("ducked.json")
    static let log = dir.appendingPathComponent("dikte.log")
}

enum Log {
    static let lock = NSLock()
    static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f
    }()

    /// Errors and timings only; transcripts never go in the log.
    static func write(_ line: String) {
        lock.lock()
        defer { lock.unlock() }
        let fm = FileManager.default
        try? fm.createDirectory(at: Paths.dir, withIntermediateDirectories: true)
        if let size = (try? fm.attributesOfItem(atPath: Paths.log.path))?[.size] as? Int, size > 1_000_000 {
            let old = Paths.log.appendingPathExtension("old")
            try? fm.removeItem(at: old)
            try? fm.moveItem(at: Paths.log, to: old)
        }
        let entry = Data("\(formatter.string(from: Date())) \(line)\n".utf8)
        if let handle = try? FileHandle(forWritingTo: Paths.log) {
            handle.seekToEndOfFile()
            handle.write(entry)
            try? handle.close()
        } else {
            try? entry.write(to: Paths.log)
        }
    }
}

/// The API key, in the login keychain. Read once, then kept in memory: an unsigned app that was
/// updated is asked again for keychain access, and that should happen once, not on every redraw.
enum KeyStore {
    static let service = "Dikte"
    static let account = "openai-api-key"
    static var cached: String??

    static func get() -> String? {
        if let cached { return cached }
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var out: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &out)
        var key: String?
        if status == errSecSuccess, let data = out as? Data { key = String(data: data, encoding: .utf8) }
        else if status != errSecItemNotFound { Log.write("Keychain read failed: \(status)") }
        if key == nil, let env = ProcessInfo.processInfo.environment["OPENAI_API_KEY"], !env.isEmpty { key = env }
        cached = .some(key)
        return key
    }

    static func save(_ key: String) throws {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(base as CFDictionary)
        var item = base
        item[kSecValueData as String] = Data(key.utf8)
        item[kSecAttrLabel as String] = "Dikte (OpenAI API key)"
        let status = SecItemAdd(item as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw ConfigError("The keychain refused the key (\(status)).")
        }
        cached = .some(key)
    }
}

enum Autostart {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    static func set(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            Log.write("Login item change failed: \(error.localizedDescription)")
        }
    }
}

enum Permissions {
    /// "granted", "denied" or "ask", for the window.
    static var microphone: String {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return "granted"
        case .denied, .restricted: return "denied"
        default: return "ask"
        }
    }

    static func requestMicrophone() {
        guard AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined else { return }
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            Log.write("Microphone access \(granted ? "granted" : "refused")")
        }
    }

    static var accessibility: Bool { AXIsProcessTrusted() }

    /// Shows macOS's own "Dikte would like to control this computer" dialog, with its button to System Settings.
    static func promptAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }

    /// Whether the 🌐 key has a job of its own (emoji, input source), which then fires when a hold ends.
    /// 0 = Do Nothing, 3 = Start Dictation (a double press): both leave a single hold alone.
    static var globeKeyBusy: Bool {
        let value = UserDefaults(suiteName: "com.apple.HIToolbox")?.object(forKey: "AppleFnUsageType") as? Int
        return value != 0 && value != 3
    }

    static func open(_ pane: String) {
        let urls = [
            "accessibility": "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility",
            "microphone": "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone",
            "keyboard": "x-apple.systempreferences:com.apple.Keyboard-Settings.extension",
        ]
        if pane == "accessibility" { promptAccessibility() }
        if let url = urls[pane].flatMap(URL.init(string:)) { NSWorkspace.shared.open(url) }
    }
}

/// Short soft blips, made in memory: no sound files to ship.
enum Sounds {
    static let startSound = NSSound(data: Wav.tones(sampleRate: 44100, volume: 0.18, [(660, 55), (880, 70)]))
    static let stopSound = NSSound(data: Wav.tones(sampleRate: 44100, volume: 0.18, [(880, 55), (660, 70)]))
    static let errorSound = NSSound(data: Wav.tones(sampleRate: 44100, volume: 0.2, [(330, 110), (247, 160)]))

    static func start() { play(startSound) }
    static func stop() { play(stopSound) }
    static func error() { play(errorSound) }

    static func play(_ sound: NSSound?) {
        guard let sound else { return }
        sound.stop()
        sound.play()
    }
}

/// The menu-bar icon: the app icon's sound wave, drawn as a template (it follows the menu bar's colour)
/// while idle, red while listening and blue while transcribing.
enum MenuBarIcon {
    static let idle = make(nil)
    static let listening = make(NSColor(srgbRed: 0.94, green: 0.27, blue: 0.27, alpha: 1))
    static let working = make(NSColor(srgbRed: 0.33, green: 0.58, blue: 1, alpha: 1))

    static func make(_ color: NSColor?) -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            (color ?? .black).setFill()
            let heights: [CGFloat] = [5, 9.5, 14, 9.5, 5]
            let width: CGFloat = 2.2, gap: CGFloat = 1.5
            var x = (rect.width - (CGFloat(heights.count) * width + CGFloat(heights.count - 1) * gap)) / 2
            for h in heights {
                NSBezierPath(roundedRect: NSRect(x: x, y: (rect.height - h) / 2, width: width, height: h),
                             xRadius: width / 2, yRadius: width / 2).fill()
                x += width + gap
            }
            return true
        }
        image.isTemplate = color == nil
        return image
    }
}

/// Opens in order: each transcript waits for the one recorded before it, even if it came back sooner.
@MainActor
final class Gate {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiters.append($0) }
    }

    func open() {
        isOpen = true
        let waiting = waiters
        waiters = []
        for waiter in waiting { waiter.resume() }
    }
}
