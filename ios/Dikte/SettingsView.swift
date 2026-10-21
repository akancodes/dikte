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
                modesSection
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
                    Toggle("Add a space after the text", isOn: binding(\.appendSpace))
                }
                Section {
                    Picker("Letters", selection: $d.keyboardLayout) {
                        Text("Türkçe Q").tag("tr")
                        Text("English").tag("en")
                    }
                    Picker("Microphone stays open", selection: $d.sessionMinutes) {
                        ForEach([15, 30, 60, 120], id: \.self) { Text("\($0) min").tag($0) }
                        Text("Until I end it").tag(0)
                    }
                } header: {
                    Text("Keyboard")
                } footer: {
                    Text("Türkçe Q has every English letter too. The microphone stays open for this long after the last dictation, so the keyboard can listen without opening Dikte; iOS shows the orange dot meanwhile and music keeps playing. Longer costs a little more battery.")
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

    private var modesSection: some View {
        Section {
            Picker("Active mode", selection: binding(\.activeMode)) {
                Text("As spoken").tag(Modes.plain)
                ForEach(d.settings.modes, id: \.id) { Text($0.name).tag($0.id) }
            }
            ForEach(d.settings.modes, id: \.id) { mode in
                NavigationLink {
                    ModeEditor(id: mode.id)
                } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(mode.name)
                        Text(mode.instruction).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
            }
            .onDelete { offsets in
                d.update { s in
                    let gone = offsets.map { s.modes[$0].id }
                    s.modes.remove(atOffsets: offsets)
                    if gone.contains(s.activeMode) { s.activeMode = Modes.plain }
                }
            }
            Button {
                d.update { s in
                    let id = Modes.newId("New mode", taken: s.modes.map(\.id))
                    s.modes.append(Mode(id: id, name: "New mode", instruction: "Write it as…"))
                }
            } label: {
                Label("New mode", systemImage: "plus")
            }
        } header: {
            Text("Modes")
        } footer: {
            Text("How your words are written: each mode is an instruction in your own words, sent with the transcript to a chat model. Switch modes from the keyboard's strip too.")
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

/// One mode: its name, what to do with the words, and its chat model.
struct ModeEditor: View {
    @EnvironmentObject private var d: Dictator
    let id: String
    @State private var name = ""
    @State private var instruction = ""
    @State private var model = ""

    var body: some View {
        Form {
            Section("Name") {
                TextField("Name", text: $name)
                    .onChange(of: name) { _, v in save { $0.name = v.trimmingCharacters(in: .whitespaces) } }
            }
            Section {
                TextField("Write it as a short list…", text: $instruction, axis: .vertical)
                    .lineLimit(4...12)
                    .onChange(of: instruction) { _, v in save { $0.instruction = v.trimmingCharacters(in: .whitespacesAndNewlines) } }
            } header: {
                Text("What to do with your words")
            } footer: {
                Text("Plain words are enough: \"make it a list\", \"turn it into a clear prompt\". The transcript is never answered, only rewritten.")
            }
            Section {
                TextField("Default (\(d.settings.cleanupModel))", text: $model)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                    .onChange(of: model) { _, v in save { $0.model = v.trimmingCharacters(in: .whitespaces) } }
            } header: {
                Text("Model")
            } footer: {
                Text("Empty uses the default. gpt-4.1-mini answers in under a second; gpt-4.1 and gpt-5.6-luna write a little more smoothly and take longer.")
            }
            Section {
                Button(d.settings.activeMode == id ? "Active mode" : "Make it the active mode") {
                    d.update { $0.activeMode = id }
                }
                .disabled(d.settings.activeMode == id)
            }
        }
        .scrollContentBackground(.hidden)
        .background(Palette.background.ignoresSafeArea())
        .navigationTitle(name.isEmpty ? "Mode" : name)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            guard let mode = d.settings.modes.first(where: { $0.id == id }) else { return }
            name = mode.name
            instruction = mode.instruction
            model = mode.model
        }
    }

    /// Empty names and instructions are not saved: a mode always has both.
    private func save(_ change: @escaping (inout Mode) -> Void) {
        d.update { s in
            guard let i = s.modes.firstIndex(where: { $0.id == id }) else { return }
            var m = s.modes[i]
            change(&m)
            if !m.name.isEmpty && !m.instruction.isEmpty { s.modes[i] = m }
        }
    }
}
