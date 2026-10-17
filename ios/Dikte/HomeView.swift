import DikteCore
import SwiftUI

/// One screen: the big button leads, the keyboard session sits under it, then what was said lately.
struct HomeView: View {
    @EnvironmentObject private var d: Dictator
    @State private var showSettings = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    if d.cameFromKeyboard && d.phase == .listening { backBanner }
                    Orb()
                        .padding(.top, 8)
                    if let notice = d.notice { noticeCard(notice) }
                    sessionCard
                    if !setupDone { SetupCard(openSettings: { showSettings = true }) }
                    if !d.history.isEmpty { recent }
                    if d.week.totalCount > 0 { weekLine }
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 32)
            }
            .background(Palette.background.ignoresSafeArea())
            .navigationTitle("Dikte")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { showSettings = true } label: { Image(systemName: "gearshape.fill") }
                        .accessibilityLabel("Settings")
                }
            }
            .sheet(isPresented: $showSettings) { SettingsView() }
        }
    }

    private var setupDone: Bool {
        d.hasKey && d.keyboardAdded && d.keyboard?.fullAccess == true
    }

    private var backBanner: some View {
        HStack(spacing: 12) {
            Image(systemName: "arrow.uturn.backward.circle.fill").font(.title)
            VStack(alignment: .leading, spacing: 2) {
                Text("Listening. Go back and talk.").font(.headline)
                Text("Tap ◀ at the top left, speak, then tap Dikte's button on the keyboard.")
                    .font(.subheadline).foregroundStyle(.white.opacity(0.8))
            }
            Spacer(minLength: 0)
        }
        .padding(16)
        .background(Palette.red.opacity(0.85), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
    }

    private func noticeCard(_ text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
            Text(text).font(.subheadline)
            Spacer(minLength: 0)
            Button { d.notice = nil } label: { Image(systemName: "xmark").foregroundStyle(.secondary) }
        }
        .padding(14)
        .background(Palette.card, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }

    private var sessionCard: some View {
        HStack(spacing: 14) {
            ZStack {
                Circle().fill(d.sessionOn ? Palette.green.opacity(0.2) : Color.white.opacity(0.08))
                Image(systemName: "keyboard.fill")
                    .foregroundStyle(d.sessionOn ? Palette.green : .white.opacity(0.6))
            }
            .frame(width: 46, height: 46)
            VStack(alignment: .leading, spacing: 3) {
                if d.sessionOn, let ends = d.sessionEnds {
                    Text("Keyboard is live").font(.headline)
                    Text("Talk from any app until \(ends.formatted(date: .omitted, time: .shortened))")
                        .font(.subheadline).foregroundStyle(.secondary)
                } else {
                    Text("Keyboard session").font(.headline)
                    Text("Opens the microphone for \(d.sessionMinutes) min so the keyboard can listen anywhere")
                        .font(.subheadline).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            Button {
                if d.sessionOn { d.endSession() } else { Task { await d.startSession() } }
            } label: {
                Text(d.sessionOn ? "End" : "Start")
                    .font(.headline)
                    .padding(.horizontal, 18).padding(.vertical, 10)
                    .background(d.sessionOn ? Color.white.opacity(0.12) : Palette.violet, in: Capsule())
            }
            .buttonStyle(.plain)
        }
        .padding(16)
        .background(Palette.card, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
    }

    private var recent: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Lately").font(.title3.bold())
                Spacer()
                Text("Tap to copy").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(Array(d.history.prefix(20).enumerated()), id: \.offset) { _, entry in
                Button { d.copy(entry.text) } label: {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(entry.text).font(.body).multilineTextAlignment(.leading).lineLimit(6)
                        Text(entry.at.formatted(.relative(presentation: .named)))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(14)
                    .background(Palette.card, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                }
                .buttonStyle(.plain)
            }
        }
    }

    private var weekLine: some View {
        let w = d.week
        return HStack(spacing: 0) {
            stat("\(w.weekWords)", "words this week")
            stat(String(format: "%.0f", w.weekMinutes), "minutes talked")
            stat(String(format: "$%.2f", w.monthCost), "this month")
        }
        .padding(.vertical, 14)
        .background(Palette.card, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }

    private func stat(_ value: String, _ label: String) -> some View {
        VStack(spacing: 2) {
            Text(value).font(.title3.bold().monospacedDigit())
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
    }
}

/// The big button: tap to talk here (the text is copied), tap again to send. Its colour is the state.
struct Orb: View {
    @EnvironmentObject private var d: Dictator

    var body: some View {
        VStack(spacing: 14) {
            Button(action: tap) {
                ZStack {
                    Circle()
                        .fill(color.opacity(0.18))
                        .scaleEffect(d.phase == .listening ? 1 + CGFloat(min(d.level * 2.2, 0.35)) : 1)
                        .animation(.easeOut(duration: 0.12), value: d.level)
                    Circle()
                        .fill(LinearGradient(colors: [color, color.opacity(0.75)], startPoint: .topLeading, endPoint: .bottomTrailing))
                        .padding(26)
                        .shadow(color: color.opacity(0.55), radius: 24)
                    icon
                }
                .frame(width: 230, height: 230)
            }
            .buttonStyle(.plain)
            .sensoryFeedback(.impact(weight: .medium), trigger: d.phase)

            Text(title).font(.title2.bold())
            Text(subtitle).font(.subheadline).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
    }

    @ViewBuilder private var icon: some View {
        switch d.phase {
        case .listening:
            Image(systemName: "stop.fill").font(.system(size: 54, weight: .bold))
        case .working:
            ProgressView().controlSize(.large).tint(.white)
        default:
            Image(systemName: "waveform").font(.system(size: 64, weight: .bold))
        }
    }

    private var color: Color {
        switch d.phase {
        case .listening: return Palette.red
        case .working: return Palette.blue
        default: return Palette.violet
        }
    }

    private var title: String {
        switch d.phase {
        case .listening: return String(format: "Listening  %d:%02d", Int(d.elapsed) / 60, Int(d.elapsed) % 60)
        case .working: return "Writing…"
        default:
            return d.justCopied ? "Copied" : "Tap to talk"
        }
    }

    private var subtitle: String {
        switch d.phase {
        case .listening: return d.source == .keyboard ? "Tap here or on the keyboard to send" : "Tap again when you are done"
        case .working: return "The text goes where you started"
        default: return "Here the text is copied. In other apps, use Dikte's keyboard."
        }
    }

    private func tap() {
        if d.phase == .listening { d.stop() } else { d.start(from: .app) }
    }
}

/// The three things Dikte needs once, each ticked off as it is done.
struct SetupCard: View {
    @EnvironmentObject private var d: Dictator
    let openSettings: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Set up once").font(.title3.bold())
            step(1, "Add your OpenAI API key", done: d.hasKey, button: "Add", action: openSettings)
            step(2, "Add the Dikte keyboard", detail: "Settings → Dikte → Keyboards → turn Dikte on",
                 done: d.keyboardAdded, button: "Open", action: openAppSettings)
            step(3, "Allow Full Access", detail: "Same place. Dikte's keyboard only talks to this app; nothing you type is sent anywhere.",
                 done: d.keyboard?.fullAccess == true, button: "Open", action: openAppSettings)
            if d.keyboardAdded && d.keyboard == nil {
                Text("Then open the keyboard once in any app (🌐 key) so Dikte can see it.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 20, style: .continuous)
                .fill(LinearGradient(colors: [Palette.violetDeep.opacity(0.5), Palette.card], startPoint: .topLeading, endPoint: .bottomTrailing))
        )
    }

    private func step(_ n: Int, _ title: String, detail: String? = nil, done: Bool, button: String, action: @escaping () -> Void) -> some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle().fill(done ? Palette.green : Color.white.opacity(0.12))
                if done { Image(systemName: "checkmark").font(.caption.bold()) } else { Text("\(n)").font(.caption.bold()) }
            }
            .frame(width: 26, height: 26)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.semibold)).strikethrough(done, color: .white.opacity(0.4))
                    .foregroundStyle(done ? .secondary : .primary)
                if let detail, !done { Text(detail).font(.caption).foregroundStyle(.secondary) }
            }
            Spacer(minLength: 0)
            if !done {
                Button(button, action: action)
                    .font(.subheadline.bold())
                    .padding(.horizontal, 14).padding(.vertical, 7)
                    .background(Palette.violet, in: Capsule())
                    .buttonStyle(.plain)
            }
        }
    }

    private func openAppSettings() {
        if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
    }
}
