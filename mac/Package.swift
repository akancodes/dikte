// swift-tools-version:5.9
import PackageDescription

var targets: [Target] = [
    // Plain Foundation: the hotkey logic, settings, API client, WAV, history and stats. Tested on any OS.
    .target(name: "DikteCore"),
    .testTarget(name: "DikteCoreTests", dependencies: ["DikteCore"]),
]

#if os(macOS)
// The menu-bar app around the core: AppKit, CoreGraphics event tap, AVAudioEngine, WKWebView.
targets.append(.executableTarget(name: "Dikte", dependencies: ["DikteCore"]))
#endif

let package = Package(
    name: "Dikte",
    platforms: [.macOS(.v13)],
    targets: targets
)
