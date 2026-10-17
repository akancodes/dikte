import AppKit
import WebKit

/// The app window: the same HTML page as on Windows (ui/index.html, in the app's Resources) in a
/// WKWebView. A small script stands in for WebView2's window.chrome.webview, so the page talks to
/// the app through the same JSON messages. Closing the window releases it.
@MainActor
final class MainWindow: NSObject, NSWindowDelegate, WKNavigationDelegate {
    private static let bridge = """
    (() => {
      const listeners = [];
      window.chrome = { webview: {
        postMessage: m => window.webkit.messageHandlers.dikte.postMessage(JSON.stringify(m)),
        addEventListener: (type, fn) => { if (type === "message") listeners.push(fn); },
      } };
      window.__dikte = m => listeners.forEach(fn => fn({ data: m }));
    })();
    """

    let window: NSWindow
    private let web: WKWebView
    private var loaded = false
    private var queue: [String] = []
    var onMessage: ([String: Any]) -> Void = { _ in }
    var onClose: () -> Void = {}

    override init() {
        let config = WKWebViewConfiguration()
        let content = WKUserContentController()
        content.addUserScript(WKUserScript(source: MainWindow.bridge, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        config.userContentController = content
        web = WKWebView(frame: NSRect(x: 0, y: 0, width: 1000, height: 720), configuration: config)
        web.setValue(false, forKey: "drawsBackground")
        web.allowsBackForwardNavigationGestures = false
        web.allowsMagnification = false

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1000, height: 720),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Dikte"
        window.minSize = NSSize(width: 640, height: 560)
        window.isReleasedWhenClosed = false
        window.backgroundColor = NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
                ? NSColor(srgbRed: 17 / 255, green: 17 / 255, blue: 19 / 255, alpha: 1)
                : NSColor(srgbRed: 246 / 255, green: 246 / 255, blue: 247 / 255, alpha: 1)
        }
        window.contentView = web
        window.center()
        window.setFrameAutosaveName("Dikte")
        super.init()

        content.add(WeakHandler(self), name: "dikte")
        window.delegate = self
        web.navigationDelegate = self
        if let page = MainWindow.page() {
            web.loadHTMLString(page, baseURL: nil)
        } else {
            Log.write("index.html is missing from the app bundle")
        }
    }

    /// The page from the bundle's Resources, or next to the binary when run from `swift run`.
    static func page() -> String? {
        let candidates = [
            Bundle.main.url(forResource: "index", withExtension: "html"),
            Bundle.main.executableURL?.deletingLastPathComponent().appendingPathComponent("index.html"),
        ]
        for case let url? in candidates {
            if let text = try? String(contentsOf: url, encoding: .utf8) { return text }
        }
        return nil
    }

    fileprivate func received(_ text: String) {
        guard let data = text.data(using: .utf8),
              let message = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return }
        onMessage(message)
    }

    func post(_ message: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: message),
              let json = String(data: data, encoding: .utf8) else { return }
        let script = "window.__dikte(\(json))"
        if loaded { web.evaluateJavaScript(script, completionHandler: nil) } else { queue.append(script) }
    }

    var isFrontmost: Bool { NSApp.isActive && window.isKeyWindow }

    func showAndFocus() {
        NSApp.setActivationPolicy(.regular)
        if window.isMiniaturized { window.deminiaturize(nil) }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func close() { window.close() }

    // MARK: delegates

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        loaded = true
        for script in queue { web.evaluateJavaScript(script, completionHandler: nil) }
        queue.removeAll()
    }

    /// The page is the whole app: nothing navigates away, links open in the browser.
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        let url = action.request.url
        if url == nil || url?.absoluteString == "about:blank" || url?.scheme == "data" || !loadedOnce {
            loadedOnce = true
            decisionHandler(.allow)
            return
        }
        decisionHandler(.cancel)
        if let url, ["http", "https"].contains(url.scheme ?? "") { NSWorkspace.shared.open(url) }
    }

    private var loadedOnce = false

    func windowWillClose(_ notification: Notification) {
        web.configuration.userContentController.removeScriptMessageHandler(forName: "dikte")
        // Back to a menu-bar app: no Dock icon while the window is closed.
        NSApp.setActivationPolicy(.accessory)
        onClose()
    }
}

/// WKUserContentController keeps its handlers alive; this keeps the window from living forever.
private final class WeakHandler: NSObject, WKScriptMessageHandler {
    weak var target: MainWindow?

    init(_ target: MainWindow) { self.target = target }

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let text = message.body as? String else { return }
        Task { @MainActor [weak self] in self?.target?.received(text) }
    }
}
