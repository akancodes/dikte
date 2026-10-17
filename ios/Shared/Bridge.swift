import Foundation
import Security

/// How the app and the keyboard talk. iOS never gives a keyboard the microphone, so the app listens and
/// transcribes in the background while the keyboard only asks for it and types the result. They share a
/// keychain group (state and the last result) and Darwin notifications (the nudges), which needs no App Group.
enum Bridge {
    static let prefix = "dev.akan.dikte.ios"

    /// "TEAMID.dev.akan.dikte.ios.shared"; the team prefix comes from Info.plist, filled in at build time.
    static var group: String {
        (Bundle.main.object(forInfoDictionaryKey: "AppIdentifierPrefix") as? String ?? "") + prefix + ".shared"
    }

    /// The app's own keychain group, which the keyboard cannot read: the API key lives there.
    static var privateGroup: String {
        (Bundle.main.object(forInfoDictionaryKey: "AppIdentifierPrefix") as? String ?? "") + prefix
    }

    /// Where the keyboard opens the app when no session is running.
    static let listenURL = URL(string: "dikte://listen")!

    enum Signal: String, CaseIterable {
        // keyboard → app
        case start, stop, cancel, ping
        // app → keyboard
        case pong, changed

        var name: String { Bridge.prefix + "." + rawValue }
    }

    static func post(_ signal: Signal) {
        CFNotificationCenterPostNotification(CFNotificationCenterGetDarwinNotifyCenter(),
                                             CFNotificationName(signal.name as CFString), nil, nil, true)
    }
}

/// What the app is doing, as the keyboard sees it.
struct LiveState: Codable, Equatable {
    enum Phase: String, Codable {
        /// No session: the keyboard has to open the app first.
        case off
        /// The microphone is open and waiting for the keyboard.
        case ready
        case listening
        /// The recording is with the speech model.
        case working
    }

    var phase: Phase = .off
    var sessionEnds: Date?
    /// The last thing that went wrong, for the keyboard to show once.
    var message: String?
    var messageAt: Date?
    var updated = Date()
}

/// The newest transcript meant for the keyboard.
struct Dictation: Codable, Equatable {
    var id = UUID()
    var text: String
    var at = Date()
}

/// The keyboard reports in, so the app knows it was added and given Full Access.
struct KeyboardSeen: Codable, Equatable {
    var at = Date()
    var fullAccess: Bool
}

/// Small Codable values in the shared keychain group.
enum SharedStore {
    private static let service = "dikte.bridge"

    static func read<T: Decodable>(_ type: T.Type, _ account: String, group: String = Bridge.group) -> T? {
        var query = base(account, group)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var out: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        return try? decoder.decode(T.self, from: data)
    }

    @discardableResult
    static func write<T: Encodable>(_ value: T, _ account: String, group: String = Bridge.group) -> Bool {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        guard let data = try? encoder.encode(value) else { return false }
        let query = base(account, group)
        let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if status == errSecSuccess { return true }
        guard status == errSecItemNotFound else { return false }
        var item = query
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    static func delete(_ account: String, group: String = Bridge.group) {
        SecItemDelete(base(account, group) as CFDictionary)
    }

    private static func base(_ account: String, _ group: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrAccessGroup as String: group,
        ]
    }
}

/// Runs closures on the main queue when the other side posts a signal.
final class SignalCenter {
    static let shared = SignalCenter()

    private var handlers: [String: [() -> Void]] = [:]

    private init() {}

    func on(_ signal: Bridge.Signal, _ handler: @escaping () -> Void) {
        let name = signal.name
        if handlers[name] == nil {
            handlers[name] = []
            let callback: CFNotificationCallback = { _, observer, name, _, _ in
                guard let observer, let raw = name?.rawValue else { return }
                let name = raw as String
                let center = Unmanaged<SignalCenter>.fromOpaque(observer).takeUnretainedValue()
                DispatchQueue.main.async { center.fire(name) }
            }
            CFNotificationCenterAddObserver(CFNotificationCenterGetDarwinNotifyCenter(),
                                            Unmanaged.passUnretained(self).toOpaque(), callback,
                                            name as CFString, nil, .deliverImmediately)
        }
        handlers[name]?.append(handler)
    }

    private func fire(_ name: String) {
        handlers[name]?.forEach { $0() }
    }
}
