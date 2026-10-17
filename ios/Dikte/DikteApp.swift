import SwiftUI

@main
struct DikteApp: App {
    @StateObject private var dictator = Dictator()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            HomeView()
                .environmentObject(dictator)
                .preferredColorScheme(.dark)
                .tint(Palette.violet)
                .onOpenURL { url in
                    guard url.scheme == "dikte" else { return }
                    Task { await dictator.openedFromKeyboard() }
                }
                .onChange(of: scenePhase) { _, phase in
                    if phase == .active { dictator.refreshKeyboard() }
                    if phase == .background { dictator.cameFromKeyboard = false }
                }
        }
    }
}

enum Palette {
    static let violet = Color(red: 0.545, green: 0.361, blue: 0.965)
    static let violetDeep = Color(red: 0.357, green: 0.204, blue: 0.788)
    static let red = Color(red: 0.957, green: 0.247, blue: 0.369)
    static let blue = Color(red: 0.231, green: 0.510, blue: 0.965)
    static let green = Color(red: 0.204, green: 0.827, blue: 0.600)
    static let background = Color(red: 0.043, green: 0.039, blue: 0.071)
    static let card = Color.white.opacity(0.06)
}
