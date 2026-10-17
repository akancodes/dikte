import DikteCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var d: Dictator
    @Environment(\.dismiss) private var dismiss
    @State private var key = ""
    @State private var keyProblem: String?
    @State private var checking = false
    @State private var vocabulary = ""

    private static let models = ["gpt-transcribe", "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1"]

    var body: some View {
        NavigationStack {
            Form {
                keySection
                languageSection
                Section {
                    TextField("Names and terms", text: $vocabulary, axis: .vertical)
                        .lineLimit(2...6)
                        .onChange(of: vocabulary) { _, text in d.update { $0.vocabulary = text } }
                } header: {
                    Text("Words to spell your way")
                } footer: {
                    Text("Comma-separated. The model is told to write these exactly so.")
                }
                Section("Speech") {
                    Picker("Model", selection: binding(\.model)) {
                        ForEach(modelChoices, id: \.self) { Text($0) }
                    }
                    Toggle(isOn: binding(\.cleanup)) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Tidy up")
                            Text("Drops um and uh, keeps only the correction after \"no wait\". A second, small model call.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    Toggle("Add a space after the text", isOn: binding(\.appendSpace))
                }
                Section {
                    Picker("Microphone stays open", selection: $d.sessionMinutes) {
                        ForEach([5, 15, 30, 60], id: \.self) { Text("\($0) min").tag($0) }
                    }
                } header: {
                    Text("Keyboard session")
                } footer: {
                    Text("After the last dictation. While it is open iOS shows the orange microphone dot; music keeps playing.")
                }
                Section {
                    TextField("API address", text: binding(\.apiBase))
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                } header: {
                    Text("Advanced")
                } footer: {
                    Text("Any OpenAI-compatible server: Groq, a local Whisper server...")
                }
                if !d.history.isEmpty {
                    Section {
                        Button("Clear recent dictations", role: .destructive) { d.clearHistory() }
                    }
                }
                Section {
                    LabeledContent("Version", value: version)
                }
            }
            .scrollContentBackground(.hidden)
            .background(Palette.background.ignoresSafeArea())
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
            }
            .onAppear { vocabulary = d.settings.vocabulary }
        }
    }

    private var keySection: some View {
        Section {
            if d.hasKey {
                LabeledContent("OpenAI API key", value: "Saved")
                Button("Remove the key", role: .destructive) { d.forgetKey() }
            } else {
                SecureField("sk-…", text: $key)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                Button {
                    checking = true
                    Task {
                        keyProblem = await d.saveKey(key)
                        checking = false
                        if keyProblem == nil { key = "" }
                    }
                } label: {
                    HStack {
                        Text("Check and save")
                        if checking { Spacer(); ProgressView() }
                    }
                }
                .disabled(key.isEmpty || checking)
                if let keyProblem { Text(keyProblem).font(.caption).foregroundStyle(Palette.red) }
            }
        } header: {
            Text("OpenAI API key")
        } footer: {
            Text("Kept in this iPhone's keychain, where the keyboard cannot read it. With gpt-transcribe an hour of talking costs about $0.27.")
        }
    }

    private var languageSection: some View {
        Section {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 8)], spacing: 8) {
                ForEach(Languages.known.indices, id: \.self) { i in
                    let lang = Languages.known[i]
                    let on = d.settings.languages.contains(lang.code)
                    Button {
                        d.update { s in
                            if on { s.languages.removeAll { $0 == lang.code } } else { s.languages.append(lang.code) }
                        }
                    } label: {
                        Text(lang.name)
                            .font(.subheadline.weight(on ? .bold : .regular))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 8)
                            .background(on ? Palette.violet : Color.white.opacity(0.08), in: Capsule())
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.vertical, 4)
        } header: {
            Text("Languages you speak")
        } footer: {
            Text("None: detected. One: pinned. Several: mixed freely, each word written in the language it was said.")
        }
    }

    private var modelChoices: [String] {
        Self.models.contains(d.settings.model) ? Self.models : Self.models + [d.settings.model]
    }

    private func binding<T>(_ path: WritableKeyPath<Settings, T>) -> Binding<T> {
        Binding(get: { d.settings[keyPath: path] }, set: { value in d.update { $0[keyPath: path] = value } })
    }

    private var version: String {
        let info = Bundle.main.infoDictionary
        return "\(info?["CFBundleShortVersionString"] as? String ?? "?") (\(info?["CFBundleVersion"] as? String ?? "?"))"
    }
}
