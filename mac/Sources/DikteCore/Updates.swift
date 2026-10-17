import Foundation

/// A release version, "0.5.0". Unset parts count as 0, so 0.5 equals 0.5.0 and 0.5.0.0.
public struct AppVersion: Comparable, CustomStringConvertible, Sendable {
    public let parts: [Int]

    /// "0.5.0" → 0.5.0; two to four plain numbers only, so "0.5.0-beta" and "dev" are nil.
    public init?(_ text: String) {
        let pieces = text.split(separator: ".", omittingEmptySubsequences: false)
        guard (2...4).contains(pieces.count) else { return nil }
        var parts: [Int] = []
        for piece in pieces {
            guard !piece.isEmpty, piece.allSatisfy({ ("0"..."9").contains($0) }), let n = Int(piece) else { return nil }
            parts.append(n)
        }
        while parts.count < 4 { parts.append(0) }
        self.parts = parts
    }

    /// "v0.5.0" → 0.5.0; anything else (a pre-release suffix too) → nil.
    public static func tag(_ tag: String?) -> AppVersion? {
        guard let tag, tag.hasPrefix("v") else { return nil }
        return AppVersion(String(tag.dropFirst()))
    }

    public static func < (a: AppVersion, b: AppVersion) -> Bool { a.parts.lexicographicallyPrecedes(b.parts) }

    /// Three parts, like the tags: "0.5.0".
    public var description: String { parts.prefix(3).map(String.init).joined(separator: ".") }
}

/// A published release's download, as GitHub lists it.
public struct Release: Equatable, Sendable {
    public let version: AppVersion
    public let url: URL
    public let size: Int64
    /// Lower-case hex.
    public let sha256: String
}

/// Reads GitHub's "latest release" answer. No network here, so it is tested on any machine.
public enum Updates {
    public static let latestURL = URL(string: "https://api.github.com/repos/akancodes/dikte/releases/latest")!
    public static let macAsset = "Dikte-mac.zip"

    /// The release in `json` if it is newer than `current` and carries `asset` with a SHA-256 digest;
    /// otherwise nil. A download without a digest is never installed: the digest is what proves the file
    /// is the one the release built.
    public static func newer(_ json: Data, than current: AppVersion, asset: String) throws -> Release? {
        guard let root = try JSONSerialization.jsonObject(with: json) as? [String: Any] else { return nil }
        if root["draft"] as? Bool == true || root["prerelease"] as? Bool == true { return nil }
        guard let version = AppVersion.tag(root["tag_name"] as? String), current < version else { return nil }
        guard let assets = root["assets"] as? [[String: Any]] else { return nil }

        for item in assets where item["name"] as? String == asset {
            guard let link = item["browser_download_url"] as? String, link.hasPrefix("https://github.com/"),
                  let url = URL(string: link),
                  let size = (item["size"] as? NSNumber)?.int64Value, size > 0,
                  let digest = item["digest"] as? String, digest.hasPrefix("sha256:") else { return nil }
            let hex = digest.dropFirst("sha256:".count).lowercased()
            guard hex.count == 64, hex.allSatisfy({ "0123456789abcdef".contains($0) }) else { return nil }
            return Release(version: version, url: url, size: size, sha256: hex)
        }
        return nil
    }
}
