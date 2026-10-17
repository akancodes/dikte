import AppKit
import Carbon
import DikteCore

/// Lives in the menu bar and runs the whole loop: hotkey → record → transcribe → insert.
@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let api = Transcriber()
    private let overlay = Overlay()
    private let ducker = Ducker()
    private let stats = Stats(url: Paths.stats)
    private var settings = Settings()
    private var router = KeyRouter(machine: HotkeyMachine(spec: try! HotkeySpec.parse("fn"), lockKey: Key.space), toggle: nil)
    private var tap: EventTap?
    private var history = History(url: Paths.history, size: 20)
    private var window: MainWindow?
    private var statusItem: NSStatusItem?

    private var recorder: Recorder?
    private var micTest: Recorder?
    private var recordingStarted = Date()
    private var feedbackShown = false
    private var pending = 0 { didSet { router.setPending(pending) } }
    private var inFlight: [UUID: Task<String, Error>] = [:]
    private var cancelGeneration = 0
    private var lastGate: Gate?
    private var failedAudio: Data?
    private var errorMessage: String?
    private var errorHint: String?

    private var watchTimer: Timer?
    private var levelTimer: Timer?
    private var accessibilityTimer: Timer?
    private var settingsWatcher: DispatchSourceFileSystemObject?
    private var settingsStamp: Date?
    private var lastReload = Date.distantPast
    private var captureTarget: String?
    private var captureStarted = Date()

    private let updater = Updater()
    private var lastUse = Date()
    private var staged: URL?
    private var updating = false
    private var updateStatus = ""

    // MARK: start and stop

    func applicationDidFinishLaunching(_ notification: Notification) {
        let launchedAtLogin = AppController.launchedAsLoginItem()
        try? FileManager.default.createDirectory(at: Paths.dir, withIntermediateDirectories: true)
        Ducker.recoverFromCrash()

        let problem = loadSettings()
        router = KeyRouter(machine: AppController.machine(settings), toggle: AppController.toggle(settings))
        wireRouter()
        history = History(url: Paths.history, size: settings.historySize)

        buildMainMenu()
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = MenuBarIcon.idle
        item.button?.toolTip = "Dikte"
        let menu = NSMenu()
        menu.delegate = self
        item.menu = menu
        statusItem = item

        ensureTap()
        let watch = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in Task { @MainActor in self?.onWatch() } }
        RunLoop.main.add(watch, forMode: .common)
        watchTimer = watch
        watchSettingsFile()

        // A screen lock or sleep swallows key-ups: end any hold the way letting go would.
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.sessionDidResignActiveNotification, NSWorkspace.screensDidSleepNotification, NSWorkspace.willSleepNotification] {
            center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.releaseEverything() }
            }
        }

        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
        Log.write("Started \(version) on macOS \(ProcessInfo.processInfo.operatingSystemVersionString), hotkey \(settings.hotkey), model \(settings.model)")
        // An update starts the new app with --updated-from, and --background when the window was closed.
        let arguments = CommandLine.arguments
        let updatedFrom = arguments.firstIndex(of: "--updated-from").flatMap { arguments.indices.contains($0 + 1) ? arguments[$0 + 1] : nil }
        let quiet = launchedAtLogin || arguments.contains("--background")
        if let problem { overlay.showMessage("Settings file has a problem", problem, seconds: 8) }
        else if let updatedFrom { overlay.showMessage("Dikte updated to \(version)", "From \(updatedFrom)", seconds: 3) }

        Permissions.requestMicrophone()
        // After an update the tap is usually off: macOS ties Accessibility to the exact build.
        if !quiet || KeyStore.get() == nil || tap == nil { openWindow() }
        else if problem == nil && updatedFrom == nil { overlay.showMessage("Dikte is ready", "Hold \(AppController.pretty(settings.hotkey)) to talk", seconds: 2.5) }

        let firstCheck = Timer(timeInterval: 45, repeats: false) { [weak self] _ in
            Task { @MainActor in
                if self?.staged == nil && self?.updating == false { Updater.cleanUp() }
                await self?.checkForUpdates(manual: false)
            }
        }
        RunLoop.main.add(firstCheck, forMode: .common)
        let everyFourHours = Timer(timeInterval: 4 * 60 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.checkForUpdates(manual: false) }
        }
        RunLoop.main.add(everyFourHours, forMode: .common)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openWindow()
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        _ = recorder?.stop()
        _ = micTest?.stop()
        ducker.restore()
    }

    /// Whether macOS opened Dikte as a login item, which waits in the menu bar instead of opening its window.
    static func launchedAsLoginItem() -> Bool {
        guard let event = NSAppleEventManager.shared().currentAppleEvent,
              event.eventID == AEEventID(kAEOpenApplication) else { return false }
        return event.paramDescriptor(forKeyword: AEKeyword(keyAEPropData))?.enumCodeValue == OSType(keyAELaunchedAsLogInItem)
    }

    private func buildMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "About Dikte", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Close Window", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        appMenu.addItem(withTitle: "Hide Dikte", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit Dikte", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        // Without an Edit menu, ⌘V does nothing in the window's fields (the API key is usually pasted).
        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)
        NSApp.mainMenu = main
    }

    // MARK: settings

    /// Returns the problem when the file could not be used (the defaults are used instead).
    private func loadSettings() -> String? {
        do {
            settings = try Settings.loadOrCreate(Paths.settings)
            settingsStamp = AppController.stamp()
            return nil
        } catch {
            Log.write("Settings unusable, using defaults: \(error.localizedDescription)")
            settings = Settings()
            return error.localizedDescription
        }
    }

    static func stamp() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: Paths.settings.path))?[.modificationDate] as? Date
    }

    static func machine(_ s: Settings) -> HotkeyMachine {
        let spec = (try? HotkeySpec.parse(s.hotkey)) ?? (try! HotkeySpec.parse("fn"))
        return HotkeyMachine(spec: spec, lockKey: KeyNames.parse(s.handsFreeKey) ?? Key.space)
    }

    static func toggle(_ s: Settings) -> ToggleKey? {
        guard !s.toggleHotkey.trimmingCharacters(in: .whitespaces).isEmpty, let spec = try? HotkeySpec.parse(s.toggleHotkey) else { return nil }
        return ToggleKey(spec: spec)
    }

    private func apply(_ fresh: Settings) {
        let hotkeyChanged = fresh.hotkey != settings.hotkey || fresh.handsFreeKey != settings.handsFreeKey
        let toggleChanged = fresh.toggleHotkey != settings.toggleHotkey
        let updatesTurnedOn = fresh.autoUpdate && !settings.autoUpdate
        settings = fresh
        if updatesTurnedOn { Task { await checkForUpdates(manual: false) } }
        if hotkeyChanged && recorder == nil { router.setMachine(AppController.machine(fresh)) }
        if toggleChanged { router.setToggle(AppController.toggle(fresh)) }
        history = History(url: Paths.history, size: fresh.historySize)
        pushState()
    }

    /// Edits to settings.json by hand apply at once. The folder is watched because editors replace the file.
    private func watchSettingsFile() {
        let fd = open(Paths.dir.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let source = DispatchSource.makeFileSystemObjectSource(fileDescriptor: fd, eventMask: .write, queue: .main)
        source.setEventHandler { [weak self] in Task { @MainActor in await self?.reloadSettings() } }
        source.setCancelHandler { close(fd) }
        source.resume()
        settingsWatcher = source
    }

    private func reloadSettings() async {
        // The folder also holds the log and history; only a changed settings.json counts.
        guard AppController.stamp() != settingsStamp else { return }
        // Editors save in several writes; one reload per burst, once the burst is over.
        if Date().timeIntervalSince(lastReload) < 0.8 { return }
        lastReload = Date()
        try? await Task.sleep(nanoseconds: 300_000_000)
        settingsStamp = AppController.stamp()
        do {
            apply(try Settings.loadOrCreate(Paths.settings))
            overlay.showMessage("Settings reloaded", "Hold \(AppController.pretty(settings.hotkey)) · \(settings.model)", seconds: 2.5)
            Log.write("Settings reloaded: hotkey \(settings.hotkey), model \(settings.model)")
        } catch {
            overlay.showMessage("Settings not applied", error.localizedDescription, seconds: 8)
        }
    }

    /// Merges a partial settings object from the window, checks it, saves it.
    private func saveFromWindow(_ patch: [String: Any]) {
        do {
            let fresh = try settings.merging(patch)
            try fresh.save(Paths.settings)
            settingsStamp = AppController.stamp() // our own write: no "reloaded" message
            apply(fresh)
            window?.post(["type": "toast", "text": "Saved"])
        } catch {
            window?.post(["type": "toast", "text": error.localizedDescription])
            pushState()
        }
    }

    // MARK: keyboard

    private func wireRouter() {
        router.onCommand = { [weak self] command in Task { @MainActor in self?.run(command) } }
        router.onCaptured = { [weak self] outcome, result in Task { @MainActor in self?.finishCapture(outcome, result) } }
        router.onCancelPending = { [weak self] in Task { @MainActor in self?.cancelPending() } }
    }

    /// Starts the event tap, or waits for Accessibility to be granted and starts it then.
    private func ensureTap() {
        if tap != nil { return }
        let router = self.router
        let candidate = EventTap(handler: { code, isDown in router.handle(code, isDown: isDown) },
                                 onReset: { [weak self] in Task { @MainActor in self?.releaseEverything() } })
        if candidate.start() {
            tap = candidate
            accessibilityTimer?.invalidate()
            accessibilityTimer = nil
            Log.write("Keyboard tap running")
            pushState()
            return
        }
        guard accessibilityTimer == nil else { return }
        Log.write("Waiting for Accessibility permission")
        Permissions.promptAccessibility()
        let poll = Timer(timeInterval: 1, repeats: true) { [weak self] _ in Task { @MainActor in self?.ensureTap() } }
        RunLoop.main.add(poll, forMode: .common)
        accessibilityTimer = poll
    }

    /// After a lost key-up (sleep, screen lock, the tap switched off): nothing is held any more.
    private func releaseEverything() {
        let command = router.resync { _ in false }
        if command != .none { run(command) }
    }

    private func onWatch() {
        let command = router.resync(EventTap.isPhysicallyDown)
        if command != .none { run(command) }
        if captureTarget != nil && Date().timeIntervalSince(captureStarted) > 12 { finishCapture(.cancelled, nil) }
        if recorder != nil || pending > 0 || captureTarget != nil || micTest != nil { lastUse = Date() }
        else if staged != nil && Date().timeIntervalSince(lastUse) > 60 && window?.isFrontmost != true { installUpdate() }

        guard let recorder else { return }
        // Show, sound and quieten only once a hold is clearly meant, so fn+F1 stays silent.
        if !feedbackShown && Date().timeIntervalSince(recordingStarted) > 0.18 {
            feedbackShown = true
            overlay.showListening { [weak recorder] in recorder?.level ?? 0 }
            if router.isLocked { overlay.showHandsFree() }
            if settings.sounds { Sounds.start() }
            if settings.quietOtherAudio { duckSoon(recorder) }
            pushState()
        }
        if recorder.duration / 60 >= Double(settings.maxMinutes) {
            router.reset()
            Task { await stopAndSend() }
        }
    }

    /// The whole output is turned down on a Mac, so the start blip plays first.
    private func duckSoon(_ recording: Recorder) {
        let level = settings.quietLevel
        let afterBlip = settings.sounds
        Task { @MainActor in
            if afterBlip { try? await Task.sleep(nanoseconds: 160_000_000) }
            if self.recorder === recording { self.ducker.duck(to: level) }
        }
    }

    private func run(_ command: HotkeyCommand) {
        switch command {
        case .start: startRecording()
        case .lock:
            guard let recorder else { return }
            overlay.showListening { [weak recorder] in recorder?.level ?? 0 }
            overlay.showHandsFree()
            if !feedbackShown && settings.quietOtherAudio { duckSoon(recorder) }
            feedbackShown = true
            pushState()
        case .stopAndSend: Task { await stopAndSend() }
        case .cancel: cancelRecording()
        case .toggle: toggleDictation()
        case .none: break
        }
    }

    private func toggleDictation() {
        if recorder == nil {
            router.enterLocked()
            startRecording()
        } else {
            router.reset()
            Task { await stopAndSend() }
        }
    }

    // MARK: recording

    private func startRecording() {
        guard recorder == nil else { return }
        stopMicTest()
        if Permissions.microphone == "denied" {
            router.reset()
            fail("Microphone access is off", "System Settings → Privacy & Security → Microphone → Dikte")
            return
        }
        let fresh = Recorder(device: settings.microphone)
        do {
            try fresh.start()
        } catch {
            router.reset()
            Log.write("Microphone failed: \(error.localizedDescription)")
            fail("Microphone not available", error.localizedDescription)
            return
        }
        recorder = fresh
        recordingStarted = Date()
        feedbackShown = false
        errorMessage = nil
        errorHint = nil
        updateIcon()
        let tick = Timer(timeInterval: 0.04, repeats: true) { [weak self] _ in Task { @MainActor in self?.onLevelTick() } }
        RunLoop.main.add(tick, forMode: .common)
        levelTimer = tick
    }

    private func onLevelTick() {
        if let window, window.window.isVisible, let recorder, feedbackShown {
            window.post(["type": "level", "v": recorder.level])
        }
    }

    private func endRecording() {
        levelTimer?.invalidate()
        levelTimer = nil
        ducker.restore()
    }

    private func cancelRecording() {
        guard let recording = recorder else { return }
        recorder = nil
        endRecording()
        _ = recording.stop()
        updateIcon()
        if feedbackShown { overlay.showMessage("Cancelled", "", seconds: 1.2) } else { overlay.hideNow() }
        pushState()
    }

    private func stopAndSend() async {
        guard let recording = recorder else { return }
        recorder = nil
        endRecording()
        let wasShown = feedbackShown
        let wav = recording.stop()
        let maxLevel = recording.maxLevel
        let length = recording.duration
        updateIcon()

        // A tap, not a hold.
        if length < 0.4 || !wasShown {
            overlay.hideNow()
            pushState()
            return
        }
        if maxLevel < 0.015 {
            if Permissions.microphone != "granted" {
                fail("Dikte cannot hear the microphone", "Allow it: System Settings → Privacy & Security → Microphone")
            } else {
                fail("No sound from the microphone", "Pick another microphone in Settings")
            }
            return
        }
        if settings.sounds { Sounds.stop() }
        await process(wav, length: length)
    }

    // MARK: transcribing

    private func process(_ wav: Data, length: Double) async {
        guard let key = KeyStore.get() else {
            failedAudio = wav
            overlay.showMessage("Connect your OpenAI key first", "The recording is kept: retry it from the menu bar", seconds: 5)
            openWindow()
            return
        }
        let s = settings
        let generation = cancelGeneration
        // Inserts keep the order the recordings were made in, even if a later one transcribes faster.
        let previous = lastGate
        let mine = Gate()
        lastGate = mine

        pending += 1
        updateIcon()
        if recorder == nil { overlay.showWorking() }
        pushState()
        let clock = Date()
        let id = UUID()
        defer {
            inFlight[id] = nil
            pending -= 1
            mine.open()
            updateIcon()
            pushState()
        }
        do {
            let api = self.api
            let work = Task { () async throws -> String in
                let text = try await api.transcribe(wav: wav, settings: s, apiKey: key)
                Log.write("Transcribed \(String(format: "%.1f", length)) s in \(Int(Date().timeIntervalSince(clock) * 1000)) ms")
                guard s.cleanup, !text.isEmpty else { return text }
                do { return try await api.cleanup(text, settings: s, apiKey: key) }
                catch is CancellationError { throw CancellationError() }
                catch { Log.write("Clean-up skipped: \(error.localizedDescription)"); return text }
            }
            inFlight[id] = work
            var text = try await work.value
            await previous?.wait()
            if generation != cancelGeneration { throw CancellationError() }

            if text.isEmpty {
                if recorder == nil { overlay.showMessage("Nothing to write", "No words were heard", seconds: 2) }
                return
            }
            let words = text.split(whereSeparator: { $0.isWhitespace }).count
            history.add(text)
            stats.record(at: Date(), words: words, seconds: length, model: s.model)
            failedAudio = nil

            if let window, window.isFrontmost {
                // Dictated with Dikte's own window in front: show it there instead of pasting into the page.
                window.post(["type": "result", "text": text])
                overlay.hideNow()
            } else {
                if s.appendSpace, let last = text.last, !last.isWhitespace { text += " " }
                await Inserter.insert(text, settings: s)
                if recorder == nil { overlay.showDone("\(words) words") }
            }
        } catch is CancellationError {
            if recorder == nil { overlay.showMessage("Cancelled", "", seconds: 1.2) }
        } catch let error as ApiError {
            failedAudio = wav
            try? wav.write(to: Paths.failedAudio)
            Log.write("Transcription failed: \(error.message)")
            fail(error.message, "The recording is kept: Retry from the menu bar")
        } catch {
            failedAudio = wav
            Log.write("Unexpected failure: \(error)")
            fail("Something went wrong", error.localizedDescription)
        }
    }

    private func cancelPending() {
        cancelGeneration += 1
        for work in inFlight.values { work.cancel() }
    }

    private func fail(_ title: String, _ hint: String) {
        errorMessage = title
        errorHint = hint
        overlay.showMessage(title, hint, seconds: 6)
        if settings.sounds { Sounds.error() }
        pushState()
    }

    private func updateIcon() {
        statusItem?.button?.image = recorder != nil ? MenuBarIcon.listening : pending > 0 ? MenuBarIcon.working : MenuBarIcon.idle
    }

    // MARK: picking a shortcut

    private func startCapture(_ target: String) {
        guard recorder == nil, ["hotkey", "toggleHotkey"].contains(target) else { return }
        router.startCapture()
        captureTarget = target
        captureStarted = Date()
        pushState()
    }

    private func finishCapture(_ outcome: CaptureOutcome, _ result: String?) {
        let target = captureTarget
        router.cancelCapture()
        captureTarget = nil
        if outcome == .done, let target, let result {
            Log.write("Shortcut picked for \(target): \(result)")
            saveFromWindow([target: result])
            return
        }
        if outcome == .unusable { window?.post(["type": "toast", "text": "That key cannot be a shortcut. Try another."]) }
        pushState()
    }

    // MARK: window

    private func openWindow() {
        if window == nil {
            let fresh = MainWindow()
            fresh.onMessage = { [weak self] message in self?.onWindowMessage(message) }
            fresh.onClose = { [weak self] in
                guard let self else { return }
                self.stopMicTest()
                if self.captureTarget != nil { self.finishCapture(.cancelled, nil) }
                self.window = nil
            }
            window = fresh
        }
        window?.showAndFocus()
    }

    private func onWindowMessage(_ m: [String: Any]) {
        switch m["type"] as? String ?? "" {
        case "ready": pushState()
        case "save": if let patch = m["settings"] as? [String: Any] { saveFromWindow(patch) }
        case "setKey": Task { await setKey(m["key"] as? String ?? "") }
        case "toggleDictation": toggleDictation()
        case "copy":
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(m["text"] as? String ?? "", forType: .string)
        case "clearHistory":
            history.clear()
            pushState()
        case "autostart":
            Autostart.set(m["on"] as? Bool ?? false)
            pushState()
        case "micTest": startMicTest()
        case "capture": startCapture(m["target"] as? String ?? "")
        case "captureCancel": finishCapture(.cancelled, nil)
        case "openUrl":
            if let url = URL(string: m["url"] as? String ?? ""), ["http", "https"].contains(url.scheme ?? "") { NSWorkspace.shared.open(url) }
        case "openPermission": Permissions.open(m["which"] as? String ?? "")
        case "openLog": AppController.openInTextEdit(Paths.log)
        case "openSettingsFile": AppController.openInTextEdit(Paths.settings)
        case "checkUpdates": Task { await checkForUpdates(manual: true) }
        default: break
        }
    }

    private func setKey(_ raw: String) async {
        let key = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        var problem = await api.checkKey(settings: settings, apiKey: key)
        if problem == nil {
            do {
                try KeyStore.save(key)
                Log.write("API key saved.")
            } catch {
                problem = error.localizedDescription
            }
        }
        window?.post(["type": "keyResult", "ok": problem == nil, "message": problem ?? ""])
        pushState()
    }

    private func pushState() {
        guard let window else { return }
        let key = KeyStore.get()
        let status = recorder != nil && feedbackShown ? (router.isLocked ? "handsfree" : "listening")
            : pending > 0 ? "working"
            : errorMessage != nil ? "error"
            : "idle"
        let usesFn = (try? HotkeySpec.parse(settings.hotkey))?.usesFn == true
            || (try? HotkeySpec.parse(settings.toggleHotkey))?.usesFn == true
        let state: [String: Any] = [
            "platform": "mac",
            "status": status,
            "message": errorMessage ?? NSNull(),
            "hint": errorHint ?? NSNull(),
            "hasKey": key != nil,
            "keyHint": key.map { $0.count > 8 ? "…" + String($0.suffix(4)) : "" } ?? "",
            "settings": settings.jsonObject(),
            "devices": AudioDevices.inputNames(),
            "languages": Languages.known.map { [$0.code, $0.name] },
            "history": history.items.map { ["time": AppController.when($0.at), "text": $0.text] },
            "stats": stats.summary(now: Date()).json,
            "autostart": Autostart.isEnabled,
            "capture": captureTarget ?? NSNull(),
            "permissions": ["accessibility": tap != nil, "microphone": Permissions.microphone] as [String: Any],
            "globeHint": usesFn && Permissions.globeKeyBusy,
            "update": ["version": Updater.currentText, "status": updateStatus, "busy": updating] as [String: Any],
        ]
        window.post(["type": "state", "state": state])
    }

    static func when(_ at: Date) -> String {
        let calendar = Calendar.current
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        if calendar.isDateInToday(at) {
            f.dateFormat = "HH:mm"
            return f.string(from: at)
        }
        if calendar.isDateInYesterday(at) { return "Yesterday" }
        f.dateFormat = "d MMM"
        return f.string(from: at)
    }

    static func pretty(_ hotkey: String) -> String {
        let names = ["fn": "Fn", "cmd": "Cmd", "lcmd": "Left Cmd", "rcmd": "Right Cmd", "opt": "Option", "lopt": "Left Option",
                     "ropt": "Right Option", "ctrl": "Ctrl", "lctrl": "Left Ctrl", "rctrl": "Right Ctrl", "shift": "Shift",
                     "space": "Space", "mouse3": "Middle click", "mouse4": "Mouse 4", "mouse5": "Mouse 5"]
        return hotkey.split(separator: "+").map { part in
            let p = part.trimmingCharacters(in: .whitespaces).lowercased()
            return names[p] ?? p.uppercased()
        }.joined(separator: " + ")
    }

    static func openInTextEdit(_ url: URL) {
        if !FileManager.default.fileExists(atPath: url.path) { try? Data().write(to: url) }
        let textEdit = URL(fileURLWithPath: "/System/Applications/TextEdit.app")
        NSWorkspace.shared.open([url], withApplicationAt: textEdit, configuration: NSWorkspace.OpenConfiguration())
    }

    private func startMicTest() {
        guard recorder == nil, micTest == nil else { return }
        let test = Recorder(device: settings.microphone)
        do {
            try test.start()
        } catch {
            Log.write("Microphone test failed: \(error.localizedDescription)")
            window?.post(["type": "toast", "text": "That microphone could not be opened"])
            return
        }
        micTest = test
        let started = Date()
        let tick = Timer(timeInterval: 0.05, repeats: true) { [weak self] timer in
            Task { @MainActor in
                guard let self else { return timer.invalidate() }
                if self.micTest !== test || Date().timeIntervalSince(started) > 5 {
                    timer.invalidate()
                    if self.micTest === test { self.stopMicTest() }
                    self.window?.post(["type": "micLevel", "v": 0])
                    return
                }
                self.window?.post(["type": "micLevel", "v": test.level])
            }
        }
        RunLoop.main.add(tick, forMode: .common)
    }

    private func stopMicTest() {
        guard let test = micTest else { return }
        micTest = nil
        _ = test.stop()
    }

    // MARK: updates

    /// Finds and downloads a newer release; onWatch installs it once Dikte has been idle a minute.
    private func checkForUpdates(manual: Bool) async {
        if updating || staged != nil || (!manual && !settings.autoUpdate) { return }
        if let problem = Updater.installProblem {
            updateStatus = problem
            if manual { window?.post(["type": "toast", "text": problem]) }
            pushState()
            return
        }
        updating = true
        updateStatus = "Checking…"
        pushState()
        defer {
            updating = false
            pushState()
        }
        do {
            guard let release = try await updater.check() else {
                updateStatus = "Up to date"
                if manual { window?.post(["type": "toast", "text": "Dikte is up to date"]) }
                return
            }
            updateStatus = "Downloading \(release.version)…"
            pushState()
            staged = try await updater.download(release)
            updateStatus = "\(release.version) installs when you stop dictating"
            Log.write("Update \(release.version) downloaded and verified.")
        } catch {
            updateStatus = "Could not check for updates"
            Log.write("Update check failed: \(error.localizedDescription)")
            if manual { window?.post(["type": "toast", "text": updateStatus]) }
        }
    }

    private func installUpdate() {
        guard let app = staged, recorder == nil, pending == 0 else { return }
        staged = nil
        if Updater.installAndRestart(app, openWindow: window?.window.isVisible == true) {
            NSApp.terminate(nil)
            return
        }
        updateStatus = "The update could not be installed"
        pushState()
    }

    // MARK: menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(item("Open Dikte", #selector(menuOpen)))
        menu.addItem(item(recorder == nil ? "Start hands-free dictation" : "Stop and send", #selector(menuToggle)))
        if failedAudio != nil { menu.addItem(item("Retry last recording", #selector(menuRetry))) }
        menu.addItem(.separator())
        menu.addItem(item(staged != nil ? "Restart to update" : "Check for updates", #selector(menuUpdate)))
        menu.addItem(item("Quit Dikte", #selector(menuQuit), key: "q"))
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    @objc private func menuOpen() { openWindow() }
    @objc private func menuToggle() { toggleDictation() }
    @objc private func menuQuit() { NSApp.terminate(nil) }

    @objc private func menuUpdate() {
        if staged != nil { installUpdate() } else { Task { await checkForUpdates(manual: true) } }
    }

    @objc private func menuRetry() {
        guard let wav = failedAudio else { return }
        errorMessage = nil
        errorHint = nil
        Task { await process(wav, length: Double(wav.count - 44) / Double(Recorder.sampleRate * 2)) }
    }
}
