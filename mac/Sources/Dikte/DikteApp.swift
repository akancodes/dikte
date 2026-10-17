import AppKit

@main
enum DikteApp {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let controller = AppController()
        app.delegate = controller
        // A menu-bar app; the window brings a Dock icon while it is open.
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(controller) { app.run() }
    }
}
