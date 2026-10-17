import AppKit
import CoreServices
import CryptoKit
import DikteCore

struct UpdateError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

/// Keeps Dikte.app current from the GitHub releases. The release's zip is downloaded into Application
/// Support, checked against its SHA-256 and unpacked there. Swapping a bundle is two renames: the
/// running Dikte.app moves aside into that folder, the new one takes its place, `open -n` starts it and
/// this one quits. The next launch deletes what is left.
final class Updater: Sendable {
    // Its own session: a download on a slow line can take longer than an API call is allowed.
    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForResource = 10 * 60
        return URLSession(configuration: config)
    }()

    /// nil when not running from a built Dikte.app (`swift run`).
    static let current = AppVersion(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
    static var currentText: String { current?.description ?? "dev" }

    static var bundle: URL { Bundle.main.bundleURL }
    static let workDir = Paths.dir.appendingPathComponent("Update", isDirectory: true)

    /// Why this copy cannot replace itself, in words for the window; nil when it can.
    static var installProblem: String? {
        guard current != nil, bundle.pathExtension == "app" else { return "Updates need the built Dikte.app" }
        // Run straight from Downloads, macOS starts a read-only copy of the app somewhere else.
        if bundle.path.contains("/AppTranslocation/") { return "Cannot update: move Dikte into Applications" }
        if !FileManager.default.isWritableFile(atPath: bundle.deletingLastPathComponent().path) {
            return "Cannot update: move Dikte to a folder you can write to"
        }
        return nil
    }

    /// The newer release, or nil when this one is the latest (or nothing is published yet).
    func check() async throws -> Release? {
        guard let current = Updater.current else { return nil }
        var request = URLRequest(url: Updates.latestURL)
        request.setValue("Dikte/\(Updater.currentText)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 { return nil }
        guard (200..<300).contains(status) else { throw UpdateError("GitHub answered \(status).") }
        return try Updates.newer(data, than: current, asset: Updates.macAsset)
    }

    /// Downloads the release's zip, checks size and SHA-256, unpacks it and returns the new Dikte.app.
    func download(_ release: Release) async throws -> URL {
        let fm = FileManager.default
        let dir = Updater.workDir
        try? fm.removeItem(at: dir)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        do {
            let (temp, response) = try await session.download(from: release.url)
            let zip = dir.appendingPathComponent(Updates.macAsset)
            try fm.moveItem(at: temp, to: zip)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard status == 200 else { throw UpdateError("The download answered \(status).") }

            let size = (try fm.attributesOfItem(atPath: zip.path)[.size] as? NSNumber)?.int64Value ?? -1
            if size != release.size { throw UpdateError("The download is \(size) bytes, the release says \(release.size).") }
            if try Updater.sha256(zip) != release.sha256 { throw UpdateError("The download does not match the release's SHA-256.") }

            let unpacked = dir.appendingPathComponent("new", isDirectory: true)
            try Updater.run("/usr/bin/ditto", ["-x", "-k", zip.path, unpacked.path])
            try? fm.removeItem(at: zip)
            let app = unpacked.appendingPathComponent("Dikte.app", isDirectory: true)
            let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")) as? [String: Any]
            guard info?["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
                  AppVersion(info?["CFBundleShortVersionString"] as? String ?? "") == release.version else {
                throw UpdateError("The zip does not hold Dikte \(release.version).")
            }
            // Proves the unpacked bundle is whole: the ad-hoc signature seals every file in it.
            try Updater.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
            // Downloaded by the app itself it should carry no quarantine flag, but one would make
            // Gatekeeper refuse the relaunch. xattr fails when there is nothing to remove.
            try? Updater.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
            return app
        } catch {
            try? fm.removeItem(at: dir)
            throw error
        }
    }

    /// Swaps `app` in for the running bundle and starts it. On true the caller quits at once.
    /// On false everything is back as it was.
    @MainActor
    static func installAndRestart(_ app: URL, openWindow: Bool) -> Bool {
        let fm = FileManager.default
        let target = bundle
        let old = workDir.appendingPathComponent("Dikte-\(currentText).app", isDirectory: true)
        do {
            try? fm.removeItem(at: old)
            try fm.moveItem(at: target, to: old)
        } catch {
            Log.write("Update not installed, the app could not be moved aside: \(error.localizedDescription)")
            try? fm.removeItem(at: workDir)
            return false
        }
        do {
            try fm.moveItem(at: app, to: target)
            // Tell Launch Services the bundle at this path changed before asking it to start it.
            LSRegisterURL(target as CFURL, true)
            var args = ["-n", target.path, "--args", "--updated-from", currentText]
            if !openWindow { args.append("--background") }
            try run("/usr/bin/open", args)
            Log.write("Update installed, restarting from \(currentText).")
            return true
        } catch {
            Log.write("Update not installed, putting the old app back: \(error.localizedDescription)")
            do {
                if fm.fileExists(atPath: target.path) { try fm.removeItem(at: target) }
                try fm.moveItem(at: old, to: target)
                try? fm.removeItem(at: workDir)
            } catch {
                Log.write("Could not put the old app back (\(error.localizedDescription)); it is at \(old.path).")
            }
            return false
        }
    }

    /// Deletes what an update left behind (the previous app, a half-finished download).
    static func cleanUp() {
        try? FileManager.default.removeItem(at: workDir)
    }

    static func sha256(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Runs a system tool and throws its error output when it fails.
    static func run(_ tool: String, _ args: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = args
        let errors = Pipe()
        process.standardError = errors
        process.standardOutput = FileHandle.nullDevice
        try process.run()
        let output = errors.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let text = String(decoding: output, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            throw UpdateError("\(URL(fileURLWithPath: tool).lastPathComponent) failed: \(text)")
        }
    }
}
