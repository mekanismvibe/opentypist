import AppKit
import Combine
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    /// Bump to show the walkthrough again after it changes substantially.
    private static let onboardingVersion = 2

    private let engine = EngineService()
    private lazy var controller = CompletionController(engine: engine)
    private(set) lazy var autocorrector = Autocorrector(engine: engine)
    private let monitor = KeyboardMonitor()
    private let settings = Settings.shared
    private let models = ModelManager.shared
    private let status = AppStatus.shared

    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private var onboardingWindow: NSWindow?
    private var permissionTimer: Timer?
    private var lastExternalApp: NSRunningApplication?
    private var cancellables = Set<AnyCancellable>()

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = NSImage(systemSymbolName: "text.cursor", accessibilityDescription: "OpenTypist")
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        monitor.onKeyDown = { [weak self] event in self?.handleKeyDown(event) ?? false }
        monitor.onMouseDown = { [weak self] in
            self?.controller.handleMouseDown()
            self?.autocorrector.reset()
        }
        controller.onAccept = { [weak self] text in self?.autocorrector.noteInserted(text) }
        autocorrector.onTextChanged = { [weak self] in self?.controller.textChangedExternally() }
        AppStatus.shared.autocorrector = autocorrector

        engine.onStateChange = { [weak self] state in
            AppStatus.shared.engine = state
            self?.updateIcon()
        }

        if let app = NSWorkspace.shared.frontmostApplication, app != .current { lastExternalApp = app }
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard let self else { return }
            self.controller.dismiss()
            self.autocorrector.reset()
            WritingSamples.shared.flush()
            if let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication, app != .current {
                self.lastExternalApp = app
            }
        }

        models.$revision.dropFirst().sink { [weak self] _ in
            DispatchQueue.main.async { self?.loadModel(force: true) }
        }.store(in: &cancellables)
        // Power settings or situation changed: maybe switch model or pause.
        let powerSettings = Publishers.Merge3(
            settings.$batteryBehavior.dropFirst().map { _ in () },
            settings.$lowPowerBehavior.dropFirst().map { _ in () },
            settings.$smallerModelID.dropFirst().map { _ in () }
        )
        powerSettings.sink { [weak self] in DispatchQueue.main.async { self?.loadModel() } }.store(in: &cancellables)
        settings.$unloadWhenIdle.dropFirst().sink { [weak self] enabled in
            guard let self, !enabled, self.sleeping else { return }
            self.sleeping = false
            DispatchQueue.main.async { self.loadModel() }
        }.store(in: &cancellables)
        PowerMonitor.shared.onChange = { [weak self] in self?.loadModel() }
        PowerMonitor.shared.start()
        startIdleTimer()
        settings.$enabled.dropFirst().sink { [weak self] enabled in
            if !enabled { self?.controller.dismiss() }
            DispatchQueue.main.async { self?.updateIcon() }
        }.store(in: &cancellables)
        settings.$disabledApps.dropFirst().sink { [weak self] _ in self?.controller.dismiss() }.store(in: &cancellables)
        settings.$showMenuBarIcon.sink { [weak self] show in self?.statusItem.isVisible = show }.store(in: &cancellables)

        startKeyboardMonitorWhenAllowed()
        loadModel()
        Updater.shared.startAutomaticChecks()

        if UserDefaults.standard.integer(forKey: "onboardingVersion") < Self.onboardingVersion {
            showOnboarding()
        }
        updateIcon()
    }

    /// Order matters: accepting a suggestion wins over autocorrect, which
    /// wins over normal typing.
    private func handleKeyDown(_ event: CGEvent) -> Bool {
        noteTyping()
        if controller.handleSuggestionKey(event) { return true }
        if autocorrector.handleKeyDown(event, app: NSWorkspace.shared.frontmostApplication) {
            return true
        }
        controller.handleKeyDown(event)
        return false
    }

    /// `opentypist://settings/<tab>` (general, personal, apps, model, stats)
    /// and `opentypist://setup` open those windows.
    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls where url.scheme == "opentypist" {
            if url.host == "setup" {
                showOnboarding()
            } else if url.host == "settings" {
                let tab = url.pathComponents.dropFirst().first ?? "general"
                if ["general", "personal", "replacements", "apps", "model", "stats"].contains(tab) { ViewState.shared.settingsTab = tab }
                showSettings()
            }
        }
    }

    /// Opening the app again (Finder, Spotlight) shows Settings: the only
    /// way back in when the menu bar icon is hidden.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSettings()
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        WritingSamples.shared.flush()
        Personalization.shared.saveLearned()
        engine.shutdown()
    }

    // MARK: - Setup

    private func startKeyboardMonitorWhenAllowed() {
        tryStartMonitor()
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 1.5, repeats: true) { [weak self] _ in
            self?.tryStartMonitor()
        }
    }

    private func tryStartMonitor() {
        guard !monitor.isRunning, FocusReader.isTrusted else { return }
        if monitor.start() {
            status.keyboardTapRunning = true
            status.note("Keyboard monitor started")
        } else {
            status.note("Accessibility is on, but macOS refused keyboard access")
        }
        updateIcon()
    }

    // MARK: - Model lifecycle

    /// The model file currently loaded (or loading).
    private var loadedModelURL: URL?
    /// Unloaded after a stretch without typing; reloads on the next keystroke.
    private var sleeping = false
    /// Paused by the battery / Low Power Mode setting.
    private var powerPaused = false
    private var lastTyping = Date()
    private var idleTimer: Timer?

    /// Loads the model that fits the current situation: the selected one,
    /// the smaller one on battery or in Low Power Mode if so configured, or
    /// none while paused or sleeping.
    private func loadModel(force: Bool = false) {
        let behavior = PowerMonitor.shared.behavior
        powerPaused = behavior == .pause
        if powerPaused {
            engine.unload()
            loadedModelURL = nil
            status.note("Suggestions paused \(PowerMonitor.shared.situation)")
            updateIcon()
            return
        }
        var url = models.activeModelURL
        if behavior == .smallerModel {
            if let small = ModelCatalog.model(id: settings.smallerModelID), models.isDownloaded(small) {
                url = models.fileURL(for: small)
            } else {
                status.note("The smaller model isn't downloaded; using the usual one")
            }
        }
        guard let url else {
            engine.unload()
            loadedModelURL = nil
            return
        }
        if sleeping { return }
        if !force, url == loadedModelURL, engine.state != .idle { return }
        loadedModelURL = url
        engine.load(path: url.path)
    }

    private var loadedModelName: String {
        guard let url = loadedModelURL else { return models.activeModelName ?? "model" }
        return ModelCatalog.all.first { $0.fileName == url.lastPathComponent }?.name ?? url.deletingPathExtension().lastPathComponent
    }

    private func noteTyping() {
        lastTyping = Date()
        guard sleeping, !settings.isDisabled(bundleID: NSWorkspace.shared.frontmostApplication?.bundleIdentifier) else { return }
        sleeping = false
        status.note("Loading the model again")
        loadModel()
    }

    private func startIdleTimer() {
        idleTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in
            guard let self, self.settings.unloadWhenIdle, !self.sleeping, self.engine.isReady,
                  Date().timeIntervalSince(self.lastTyping) > Double(self.settings.idleUnloadMinutes * 60) else { return }
            self.sleeping = true
            self.engine.unload()
            self.loadedModelURL = nil
            self.status.note("Unloaded the model after \(self.settings.idleUnloadMinutes) minutes without typing")
            self.updateIcon()
        }
    }

    private func updateIcon() {
        let active = settings.enabled && engine.isReady && monitor.isRunning
        statusItem.button?.appearsDisabled = !active
    }

    // MARK: - Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        let statusLine = NSMenuItem(title: statusText(), action: nil, keyEquivalent: "")
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        let lastCheck = NSMenuItem(title: "Last check: \(status.lastCheck)", action: nil, keyEquivalent: "")
        lastCheck.isEnabled = false
        menu.addItem(lastCheck)
        menu.addItem(.separator())

        let toggle = item("Enable Completions", #selector(toggleEnabled))
        toggle.state = settings.enabled ? .on : .off
        menu.addItem(toggle)

        if let app = lastExternalApp, let bundleID = app.bundleIdentifier {
            let name = app.localizedName ?? bundleID
            let perApp = item("Disable in \(name)", #selector(toggleCurrentApp))
            perApp.state = settings.isDisabled(bundleID: bundleID) ? .on : .off
            menu.addItem(perApp)
        }

        let lengthMenu = NSMenu()
        for length in CompletionLength.allCases {
            let entry = item(length.title, #selector(chooseLength(_:)))
            entry.representedObject = length.rawValue
            entry.state = settings.length == length ? .on : .off
            lengthMenu.addItem(entry)
        }
        let lengthItem = NSMenuItem(title: "Suggestion Length", action: nil, keyEquivalent: "")
        lengthItem.submenu = lengthMenu
        menu.addItem(lengthItem)

        let space = item("Add Space After Accepting", #selector(toggleTrailingSpace))
        space.state = settings.acceptTrailingSpace ? .on : .off
        menu.addItem(space)

        menu.addItem(.separator())
        let stats = Stats.shared
        for line in [
            "Today: \(stats.wordsAcceptedToday) words accepted",
            "All time: \(stats.wordsAccepted) words, \(stats.keystrokesSaved) keystrokes saved",
        ] {
            let entry = NSMenuItem(title: line, action: nil, keyEquivalent: "")
            entry.isEnabled = false
            menu.addItem(entry)
        }

        menu.addItem(.separator())
        if !FocusReader.isTrusted {
            menu.addItem(item("Grant Accessibility Access…", #selector(openAccessibilitySettings)))
        }
        if models.isDownloading {
            menu.addItem(item("Cancel Model Download", #selector(cancelDownload)))
        }
        if let release = Updater.shared.availableRelease {
            menu.addItem(item("Update to OpenTypist \(release.version)…", #selector(showGeneralSettings)))
        } else {
            menu.addItem(item("Check for Updates…", #selector(checkForUpdates)))
        }
        if Personalization.shared.profile.proposal != nil {
            menu.addItem(item("Profile Update Ready…", #selector(showPersonalSettings)))
        }
        menu.addItem(item("Choose Model…", #selector(showModelSettings)))
        menu.addItem(item("Setup Guide…", #selector(showOnboarding)))
        let settingsItem = item("Settings…", #selector(showSettings))
        settingsItem.keyEquivalent = ","
        menu.addItem(settingsItem)
        let quit = item("Quit OpenTypist", #selector(NSApplication.terminate(_:)))
        quit.target = NSApp
        quit.keyEquivalent = "q"
        menu.addItem(quit)
    }

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        return item
    }

    private func statusText() -> String {
        if !FocusReader.isTrusted { return "Needs Accessibility access" }
        if !monitor.isRunning { return "Keyboard access blocked (see Settings)" }
        switch models.download {
        case .downloading(let id, let received, let total):
            let percent = total > 0 ? Int(Double(received) / Double(total) * 100) : 0
            return "Downloading \(ModelCatalog.model(id: id)?.name ?? "model")… \(percent)%"
        case .verifying:
            return "Checking downloaded model…"
        case .failed(_, let message):
            if models.activeModelURL == nil { return "Download failed: \(message)" }
        case .none:
            break
        }
        if powerPaused { return "Paused \(PowerMonitor.shared.situation) to save battery" }
        if sleeping { return "Model unloaded to save memory; loads when you type" }
        switch engine.state {
        case .idle: return models.activeModelURL == nil ? "No model yet: choose one" : "Starting…"
        case .loading: return "Loading \(loadedModelName)…"
        case .failed(let message): return "Model error: \(message)"
        case .ready:
            return settings.enabled ? "Ready · \(loadedModelName)" : "Paused"
        }
    }

    @objc private func toggleEnabled() { settings.enabled.toggle() }

    @objc private func toggleTrailingSpace() { settings.acceptTrailingSpace.toggle() }

    @objc private func toggleCurrentApp() {
        guard let bundleID = lastExternalApp?.bundleIdentifier else { return }
        settings.setDisabled(!settings.isDisabled(bundleID: bundleID), bundleID: bundleID)
    }

    @objc private func chooseLength(_ sender: NSMenuItem) {
        if let raw = sender.representedObject as? String, let length = CompletionLength(rawValue: raw) {
            settings.length = length
        }
    }

    @objc private func cancelDownload() { models.cancelDownload() }

    @objc private func openAccessibilitySettings() { AppActions.openAccessibilitySettings() }

    @objc private func showGeneralSettings() {
        ViewState.shared.settingsTab = "general"
        showSettings()
    }

    @objc private func checkForUpdates() {
        Updater.shared.check()
        showGeneralSettings()
    }

    @objc private func showPersonalSettings() {
        ViewState.shared.settingsTab = "personal"
        showSettings()
    }

    @objc private func showModelSettings() {
        ViewState.shared.settingsTab = "model"
        showSettings()
    }

    @objc func showSettings() {
        if settingsWindow == nil {
            let window = NSWindow(contentViewController: NSHostingController(rootView: SettingsView()))
            window.title = "OpenTypist Settings"
            window.styleMask = [.titled, .closable, .miniaturizable]
            window.isReleasedWhenClosed = false
            window.center()
            settingsWindow = window
        }
        NSApp.activate(ignoringOtherApps: true)
        settingsWindow?.makeKeyAndOrderFront(nil)
    }

    @objc func showOnboarding() {
        if onboardingWindow == nil {
            let view = OnboardingView { [weak self] in self?.finishOnboarding() }
            let window = NSWindow(contentViewController: NSHostingController(rootView: view))
            window.title = "Set Up OpenTypist"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()
            onboardingWindow = window
            NotificationCenter.default.addObserver(
                forName: NSWindow.willCloseNotification, object: window, queue: .main
            ) { [weak self] _ in self?.markOnboardingDone() }
        }
        NSApp.activate(ignoringOtherApps: true)
        onboardingWindow?.makeKeyAndOrderFront(nil)
    }

    private func finishOnboarding() {
        onboardingWindow?.close()
    }

    private func markOnboardingDone() {
        UserDefaults.standard.set(Self.onboardingVersion, forKey: "onboardingVersion")
        ViewState.shared.onboardingStep = 0
    }
}
