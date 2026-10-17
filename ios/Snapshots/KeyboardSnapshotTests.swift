import SwiftUI
import UIKit
import XCTest

/// Draws the keyboard as it would sit under an app, in the states that matter, into $SNAPSHOT_DIR.
@MainActor
final class KeyboardSnapshotTests: XCTestCase {
    private let width: CGFloat = 393

    func testKeyboardStates() throws {
        let dir = URL(fileURLWithPath: ProcessInfo.processInfo.environment["SNAPSHOT_DIR"] ?? NSTemporaryDirectory())
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        let shots: [(String, UIUserInterfaceStyle, KeysView.Layout, KeysView.Page, KeysView.Shift, (KeyboardModel) -> Void)] = [
            ("1-turkish-dark-idle", .dark, .turkish, .letters, .on, { $0.stage(lastText: "Şimdi bu branch'i merge et") }),
            ("2-turkish-light-suggestions", .light, .turkish, .letters, .off, {
                $0.stage(suggestions: ["merhaba", "merhabalar", "merhem"], misspelled: true)
            }),
            ("3-english-dark", .dark, .english, .letters, .off, { $0.stage(sessionOn: false) }),
            ("4-numbers-dark", .dark, .turkish, .numbers, .off, { $0.stage() }),
            ("5-symbols-light", .light, .turkish, .symbols, .off, { $0.stage() }),
            ("6-listening", .dark, .turkish, .letters, .off, { $0.stage(status: .listening, panel: .listening) }),
            ("7-start-session-dark", .dark, .turkish, .letters, .off, { $0.stage(panel: .startSession, sessionOn: false) }),
            ("8-start-session-light", .light, .turkish, .letters, .off, { $0.stage(panel: .startSession, sessionOn: false) }),
            ("9-working", .light, .turkish, .letters, .off, { $0.stage(status: .working) }),
            ("10-full-access", .dark, .turkish, .letters, .off, { $0.stage(panel: .fullAccess) }),
        ]
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.first as? UIWindowScene)
        for (name, style, layout, page, shift, setUp) in shots {
            let model = KeyboardModel()
            setUp(model)
            let height = KeyboardViewController.toolbarHeight + KeyboardViewController.keysHeight + 6
            let window = UIWindow(windowScene: scene)
            window.frame = CGRect(x: 0, y: 0, width: width, height: height)
            window.overrideUserInterfaceStyle = style
            let root = UIViewController()
            window.rootViewController = root
            window.isHidden = false
            // The system keyboard's backdrop.
            root.view.backgroundColor = style == .dark ? UIColor(white: 0.17, alpha: 1) : UIColor(red: 0.82, green: 0.83, blue: 0.85, alpha: 1)

            let toolbar = UIHostingController(rootView: ToolbarView(model: model))
            let panel = UIHostingController(rootView: PanelView(model: model))
            let keys = KeysView()
            keys.layout = layout
            keys.page = page
            keys.shift = shift
            keys.needsGlobe = false
            keys.returnTitle = name.contains("light") ? "send" : "return"
            toolbar.safeAreaRegions = []
            panel.safeAreaRegions = []
            for host in [toolbar, panel] as [UIViewController] {
                host.view.backgroundColor = .clear
                root.addChild(host)
            }
            root.view.addSubview(toolbar.view)
            root.view.addSubview(keys)
            root.view.addSubview(panel.view)
            toolbar.view.frame = CGRect(x: 0, y: 0, width: width, height: KeyboardViewController.toolbarHeight)
            keys.frame = CGRect(x: 0, y: KeyboardViewController.toolbarHeight, width: width, height: KeyboardViewController.keysHeight)
            panel.view.frame = CGRect(x: 0, y: KeyboardViewController.toolbarHeight - 2, width: width,
                                      height: height - KeyboardViewController.toolbarHeight + 2)
            panel.view.isHidden = model.panel == nil
            root.view.layoutIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.4))

            let format = UIGraphicsImageRendererFormat()
            format.scale = 3
            let image = UIGraphicsImageRenderer(bounds: root.view.bounds, format: format).image { _ in
                root.view.drawHierarchy(in: root.view.bounds, afterScreenUpdates: true)
            }
            try XCTUnwrap(image.pngData()).write(to: dir.appendingPathComponent("\(name).png"))
            window.isHidden = true
        }
    }
}
