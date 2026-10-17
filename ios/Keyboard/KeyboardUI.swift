import SwiftUI

private enum Tone {
    static let violet = Color(red: 0.545, green: 0.361, blue: 0.965)
    static let red = Color(red: 0.957, green: 0.247, blue: 0.369)
    static let blue = Color(red: 0.231, green: 0.510, blue: 0.965)
    static let green = Color(red: 0.204, green: 0.827, blue: 0.600)
}

/// The strip above the keys: suggestions (or what Dikte is doing) on the left, the microphone on the right.
struct ToolbarView: View {
    @ObservedObject var model: KeyboardModel

    var body: some View {
        HStack(spacing: 6) {
            leading.frame(maxWidth: .infinity, alignment: .leading)
            MicButton(model: model)
        }
        .padding(.horizontal, 6)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private var leading: some View {
        switch model.status {
        case .message(let text):
            Button { model.dismissMessage() } label: {
                HStack(spacing: 6) {
                    Image(systemName: "exclamationmark.circle.fill").foregroundStyle(.orange)
                    Text(text).font(.caption).lineLimit(2).multilineTextAlignment(.leading)
                }
            }
            .buttonStyle(.plain)
        case .working:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Writing…").font(.subheadline.weight(.medium)).foregroundStyle(.secondary)
            }
            .padding(.leading, 8)
        default:
            if !model.suggestions.isEmpty {
                suggestionRow
            } else {
                idleRow
            }
        }
    }

    private var suggestionRow: some View {
        HStack(spacing: 0) {
            ForEach(Array(model.suggestions.enumerated()), id: \.offset) { i, word in
                if i > 0 { Divider().frame(height: 22) }
                Button { model.choose(word) } label: {
                    Text(word)
                        .font(.body.weight(i == 0 && model.misspelled ? .semibold : .regular))
                        .lineLimit(1).minimumScaleFactor(0.7)
                        .frame(maxWidth: .infinity, minHeight: 40)
                        .background(i == 0 && model.misspelled ? Color.primary.opacity(0.1) : .clear,
                                    in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    private var idleRow: some View {
        HStack(spacing: 10) {
            HStack(spacing: 6) {
                Circle().fill(model.sessionOn ? Tone.green : Color.secondary.opacity(0.5)).frame(width: 8, height: 8)
                Text(model.sessionOn ? "Mic on" : "Mic off").font(.footnote.weight(.semibold)).foregroundStyle(.secondary)
            }
            .padding(.leading, 8)
            if model.lastText != nil {
                Button { model.insertLast() } label: {
                    Label("Last", systemImage: "arrow.uturn.backward")
                        .font(.footnote.weight(.semibold))
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .background(Color.primary.opacity(0.08), in: Capsule())
                }
                .buttonStyle(.plain)
            }
        }
    }
}

/// Tap to start and tap to send, or hold to talk and let go to send.
struct MicButton: View {
    @ObservedObject var model: KeyboardModel
    @State private var pressing = false

    var body: some View {
        ZStack {
            Capsule().fill(color)
            switch model.status {
            case .connecting, .working:
                ProgressView().tint(.white)
            case .listening:
                Image(systemName: "stop.fill").font(.system(size: 17, weight: .bold))
            default:
                HStack(spacing: 5) {
                    Image(systemName: "waveform").font(.system(size: 18, weight: .bold))
                    Text("Talk").font(.subheadline.weight(.bold))
                }
            }
        }
        .foregroundStyle(.white)
        .frame(width: 92, height: 38)
        .scaleEffect(pressing ? 0.94 : 1)
        .animation(.easeOut(duration: 0.1), value: pressing)
        .contentShape(Rectangle())
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard !pressing else { return }
                    pressing = true
                    model.micDown()
                }
                .onEnded { _ in
                    pressing = false
                    model.micUp()
                }
        )
        .accessibilityAddTraits(.isButton)
        .accessibilityLabel(model.status == .listening ? "Send" : "Dictate")
    }

    private var color: Color {
        switch model.status {
        case .listening: return Tone.red
        case .working: return Tone.blue
        default: return Tone.violet
        }
    }
}

/// What covers the keys: the listening panel, or the step that needs the app first.
struct PanelView: View {
    @ObservedObject var model: KeyboardModel

    var body: some View {
        Group {
            switch model.panel {
            case .listening: listening
            case .startSession: startSession
            case .fullAccess: fullAccess
            case nil: Color.clear
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var listening: some View {
        ZStack {
            LinearGradient(colors: [Tone.red, Color(red: 0.80, green: 0.16, blue: 0.29)], startPoint: .top, endPoint: .bottom)
            VStack(spacing: 14) {
                TimelineView(.animation) { context in
                    let t = context.date.timeIntervalSinceReferenceDate
                    let elapsed = max(0, context.date.timeIntervalSince(model.listeningSince))
                    VStack(spacing: 10) {
                        HStack(spacing: 5) {
                            ForEach(0..<13, id: \.self) { i in
                                let wave = sin(t * 5.5 + Double(i) * 0.8) * 0.5 + 0.5
                                let wobble = sin(t * 8.3 + Double(i * i) * 0.37) * 0.5 + 0.5
                                Capsule().fill(.white.opacity(0.9))
                                    .frame(width: 5, height: 8 + 30 * (0.35 * wave + 0.65 * wobble) * (i % 4 == 0 ? 0.6 : 1))
                            }
                        }
                        .frame(height: 40)
                        Text(String(format: "Listening  %d:%02d", Int(elapsed) / 60, Int(elapsed) % 60))
                            .font(.headline.monospacedDigit())
                    }
                }
                if model.holding {
                    Text("Let go of Talk to send").font(.subheadline.weight(.semibold)).opacity(0.9)
                } else {
                    HStack(spacing: 12) {
                        Button { model.cancel() } label: {
                            Text("Cancel").font(.headline)
                                .frame(width: 110, height: 50)
                                .background(.white.opacity(0.2), in: Capsule())
                        }
                        Button { model.send() } label: {
                            Label("Send", systemImage: "arrow.up").font(.headline)
                                .foregroundStyle(Tone.red)
                                .frame(width: 170, height: 50)
                                .background(.white, in: Capsule())
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
            .foregroundStyle(.white)
        }
    }

    private var startSession: some View {
        ZStack {
            Color(uiColor: .secondarySystemBackground)
            VStack(spacing: 12) {
                Text("Turn the microphone on").font(.headline)
                HStack(alignment: .top, spacing: 6) {
                    step("1", "app.badge.fill", "Dikte opens and turns the mic on")
                    arrow
                    step("2", "arrow.uturn.backward.circle.fill", "Tap ◀ at the top left to come back")
                    arrow
                    step("3", "waveform", "It starts listening here")
                }
                Text(minutes).font(.caption).foregroundStyle(.secondary)
                HStack(spacing: 10) {
                    Button("Not now") { model.closePanel() }
                        .font(.subheadline.weight(.semibold))
                        .frame(width: 100, height: 44)
                        .background(Color.primary.opacity(0.08), in: Capsule())
                    Button { model.openApp() } label: {
                        Label("Open Dikte", systemImage: "arrow.up.forward.app.fill")
                            .font(.headline).foregroundStyle(.white)
                            .frame(width: 190, height: 44)
                            .background(Tone.violet, in: Capsule())
                    }
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 12)
        }
    }

    private var minutes: String {
        model.sessionMinutes == 0
            ? "iOS lets only the app use the microphone. It stays on until you end it in Dikte."
            : "iOS lets only the app use the microphone. It stays on for \(model.sessionMinutes) min after your last dictation."
    }

    private func step(_ n: String, _ symbol: String, _ text: String) -> some View {
        VStack(spacing: 6) {
            ZStack {
                Circle().fill(Tone.violet.opacity(0.18))
                Image(systemName: symbol).font(.system(size: 18, weight: .semibold)).foregroundStyle(Tone.violet)
            }
            .frame(width: 40, height: 40)
            Text(text).font(.caption2.weight(.medium)).multilineTextAlignment(.center).fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity)
    }

    private var arrow: some View {
        Image(systemName: "chevron.right").font(.caption.bold()).foregroundStyle(.tertiary).padding(.top, 14)
    }

    private var fullAccess: some View {
        ZStack {
            Color(uiColor: .secondarySystemBackground)
            VStack(spacing: 10) {
                Image(systemName: "lock.open.fill").font(.title2).foregroundStyle(Tone.violet)
                Text("Allow Full Access to dictate").font(.headline)
                Text("Settings → Dikte → Keyboards → Allow Full Access.\nThe keyboard uses it only to talk to the Dikte app; what you type goes nowhere.")
                    .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                Button("OK") { model.closePanel() }
                    .font(.headline)
                    .frame(width: 140, height: 42)
                    .background(Color.primary.opacity(0.1), in: Capsule())
                    .buttonStyle(.plain)
            }
            .padding(.horizontal, 16)
        }
    }
}
