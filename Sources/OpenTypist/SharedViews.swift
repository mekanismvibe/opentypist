import AppKit
import Combine
import ServiceManagement
import SwiftUI

/// View state lives in observable objects rather than `@State`: the SwiftUI
/// macro plugin that `@State` needs ships only with full Xcode, and this
/// project builds with the Command Line Tools.
final class ViewState: ObservableObject {
    static let shared = ViewState()

    @Published var accessibilityGranted = FocusReader.isTrusted
    @Published var screenRecordingGranted = ScreenContext.hasPermission
    @Published var askedForScreenRecording = false
    @Published var selectedApp: String?
    @Published var settingsTab = "general"
    @Published var autocorrectListsVersion = 0
    @Published var launchAtLogin = LaunchAtLogin.isEnabled
    @Published var launchAtLoginNeedsApproval = LaunchAtLogin.needsApproval
    @Published var launchAtLoginError: String?
    @Published var pickedModelID: String = Settings.shared.selectedModelID
    @Published var onboardingStep = UserDefaults.standard.integer(forKey: "onboardingStep") {
        didSet { UserDefaults.standard.set(onboardingStep, forKey: "onboardingStep") }
    }

    /// Profile text being edited, and the saved text it started from.
    @Published var profileDraft = Personalization.shared.profile.text
    @Published var profileDraftBase = Personalization.shared.profile.text
    @Published var newNeverCorrectWord = ""
    @Published var newSnippetShortcut = ""
    @Published var newSnippetExpansion = ""
    @Published var snippetMessage: String?
    @Published var hoveredStatsDay: Date?
    @Published var showingSamples = false

    private var timer: AnyCancellable?
    private var profileWatch: AnyCancellable?

    private init() {
        timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect().sink { [weak self] _ in
            self?.refreshPermissions()
        }
        // Follow outside changes (learned updates, restores) unless mid-edit.
        profileWatch = Personalization.shared.$profile.sink { [weak self] profile in
            guard let self, self.profileDraft == self.profileDraftBase else { return }
            self.profileDraft = profile.text
            self.profileDraftBase = profile.text
        }
    }

    func refreshPermissions() {
        if accessibilityGranted != FocusReader.isTrusted { accessibilityGranted = FocusReader.isTrusted }
        if screenRecordingGranted != ScreenContext.hasPermission { screenRecordingGranted = ScreenContext.hasPermission }
        if launchAtLogin != LaunchAtLogin.isEnabled { launchAtLogin = LaunchAtLogin.isEnabled }
        if launchAtLoginNeedsApproval != LaunchAtLogin.needsApproval { launchAtLoginNeedsApproval = LaunchAtLogin.needsApproval }
    }

    /// Binding for a "Launch at login" toggle.
    var launchAtLoginBinding: Binding<Bool> {
        Binding(
            get: { self.launchAtLogin },
            set: { enabled in
                do {
                    try LaunchAtLogin.set(enabled)
                    self.launchAtLoginError = nil
                } catch {
                    self.launchAtLoginError = error.localizedDescription
                }
                self.refreshPermissions()
            }
        )
    }
}

/// Button showing a key; click it and press another key to change it.
struct KeyRecorderField: View {
    let id: String
    @Binding var combo: KeyCombo
    @ObservedObject private var recorder = KeyRecorder.shared

    var body: some View {
        Button {
            if recorder.recording == id { recorder.stop() } else { recorder.start(id) { combo = $0 } }
        } label: {
            Text(recorder.recording == id ? "Press a key…" : combo.displayName)
                .frame(minWidth: 96)
        }
        .help("Click, then press the key (with any modifiers) to use. Esc cancels.")
    }
}

/// Registers OpenTypist as a login item with macOS.
enum LaunchAtLogin {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }
    static var needsApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

    static func set(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}

/// Launch-at-login toggle with approval and error notes.
struct LaunchAtLoginToggle: View {
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        Toggle("Launch OpenTypist at login", isOn: state.launchAtLoginBinding)
        if state.launchAtLoginNeedsApproval {
            HStack {
                Text("macOS needs you to allow OpenTypist in Login Items.")
                    .font(.callout).foregroundStyle(.secondary)
                Button("Open Login Items") { SMAppService.openSystemSettingsLoginItems() }
            }
        }
        if let error = state.launchAtLoginError {
            Text(error).font(.callout).foregroundStyle(.red)
        }
    }
}

enum AppActions {
    static func openAccessibilitySettings() {
        FocusReader.promptForTrust()
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    static func openInputMonitoringSettings() {
        _ = CGRequestListenEventAccess()
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")
    }

    /// macOS only applies a new Screen Recording grant after a restart.
    static func relaunch() {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", Bundle.main.bundlePath]
        try? task.run()
        NSApp.terminate(nil)
    }

    private static func open(_ string: String) {
        if let url = URL(string: string) { NSWorkspace.shared.open(url) }
    }
}

// MARK: - Permissions

struct PermissionsPanel: View {
    @ObservedObject private var state = ViewState.shared
    @ObservedObject private var status = AppStatus.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            PermissionRow(
                title: "Accessibility",
                badge: "Required",
                detail: "Reads the text field you're typing in, finds the caret, and lets Tab accept a suggestion.",
                granted: state.accessibilityGranted,
                action: AppActions.openAccessibilitySettings
            )
            if state.accessibilityGranted && !status.keyboardTapRunning {
                PermissionRow(
                    title: "Input Monitoring",
                    badge: "Required",
                    detail: "macOS is blocking keyboard access. Turn on OpenTypist under Input Monitoring.",
                    granted: false,
                    action: AppActions.openInputMonitoringSettings
                )
            }
            VStack(alignment: .leading, spacing: 6) {
                PermissionRow(
                    title: "Screen & System Audio Recording",
                    badge: "Recommended",
                    detail: "Lets OpenTypist read the rest of the window, like the email you're replying to or the chat above, so suggestions fit the conversation. Only the screen is read; audio is never recorded, and nothing leaves your Mac.",
                    granted: state.screenRecordingGranted,
                    action: {
                        state.askedForScreenRecording = true
                        ScreenContext.requestPermission()
                    }
                )
                if !state.screenRecordingGranted && state.askedForScreenRecording {
                    HStack {
                        Text("After turning it on, macOS needs OpenTypist to restart.")
                            .font(.callout).foregroundStyle(.secondary)
                        Button("Restart OpenTypist", action: AppActions.relaunch)
                    }
                    .padding(.leading, 30)
                }
            }
        }
    }
}

private struct PermissionRow: View {
    let title: String
    let badge: String
    let detail: String
    let granted: Bool
    let action: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: granted ? "checkmark.circle.fill" : "circle.dashed")
                .font(.title3)
                .foregroundStyle(granted ? .green : .secondary)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(title).font(.headline)
                    Text(badge)
                        .font(.caption2.weight(.semibold))
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background(Capsule().fill(badge == "Required" ? Color.orange.opacity(0.2) : Color.blue.opacity(0.18)))
                }
                Text(detail).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            if granted {
                Text("On").foregroundStyle(.secondary)
            } else {
                Button("Turn On…", action: action)
            }
        }
    }
}

// MARK: - Model picker

struct ModelPicker: View {
    @ObservedObject private var models = ModelManager.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var state = ViewState.shared
    var showsDelete = false

    private let hardware = HardwareInfo.current
    private var recommended: ModelInfo { ModelCatalog.recommended(for: hardware) }
    private var picked: ModelInfo { ModelCatalog.model(id: state.pickedModelID) ?? recommended }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: "desktopcomputer")
                Text("This Mac: \(hardware.summary)").font(.callout)
                Spacer()
                Text("Recommended: \(recommended.name)").font(.callout).foregroundStyle(.secondary)
            }
            ScrollView {
                VStack(spacing: 6) {
                    ForEach(ModelCatalog.all) { model in
                        ModelRow(
                            model: model,
                            isPicked: model.id == picked.id,
                            isRecommended: model.id == recommended.id,
                            fits: hardware.fits(model),
                            memoryGB: hardware.memoryGB,
                            showsDelete: showsDelete
                        )
                        .onTapGesture { state.pickedModelID = model.id }
                    }
                }
            }
            actions
        }
    }

    @ViewBuilder private var actions: some View {
        switch models.download {
        case .downloading(let id, let received, let total):
            let name = ModelCatalog.model(id: id)?.name ?? "model"
            HStack {
                ProgressView(value: Double(received), total: Double(max(total, 1))) {
                    Text("Downloading \(name)… \(format(received)) of \(format(total))").font(.callout)
                }
                Button("Cancel") { models.cancelDownload() }
            }
        case .verifying(let id):
            ProgressView { Text("Checking \(ModelCatalog.model(id: id)?.name ?? "model")…").font(.callout) }
        default:
            HStack {
                if case .failed(_, let message) = models.download {
                    Text(message).font(.callout).foregroundStyle(.red).lineLimit(2)
                }
                Spacer()
                if models.isDownloaded(picked) {
                    if models.activeCatalogModel == picked {
                        Label("In use", systemImage: "checkmark").foregroundStyle(.secondary)
                    } else {
                        Button("Use \(picked.name)") { models.select(picked) }.keyboardShortcut(.defaultAction)
                    }
                } else {
                    Button("Download \(picked.name) (\(picked.sizeText))") { models.startDownload(picked) }
                        .keyboardShortcut(.defaultAction)
                }
            }
        }
    }

    private func format(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}

private struct ModelRow: View {
    @ObservedObject private var models = ModelManager.shared
    let model: ModelInfo
    let isPicked: Bool
    let isRecommended: Bool
    let fits: Bool
    let memoryGB: Int
    let showsDelete: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: isPicked ? "largecircle.fill.circle" : "circle")
                .foregroundStyle(isPicked ? Color.accentColor : .secondary)
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(model.name).font(.headline)
                    if isRecommended {
                        Text("Recommended for this Mac")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Capsule().fill(Color.green.opacity(0.2)))
                    }
                    if models.activeCatalogModel == model {
                        Text("In use").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    }
                }
                Text("\(model.family) · \(model.speed) · \(model.quality) quality · \(model.sizeText)")
                    .font(.caption).foregroundStyle(.secondary)
                Text(model.summary).font(.callout).fixedSize(horizontal: false, vertical: true)
                if !fits {
                    Label("Needs \(model.minMemoryGB) GB+ memory; this Mac has \(memoryGB) GB", systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.orange)
                }
            }
            Spacer()
            if models.isDownloaded(model) {
                VStack(alignment: .trailing, spacing: 4) {
                    Label("Downloaded", systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(.green)
                    if showsDelete, models.activeCatalogModel != model {
                        Button("Delete") { models.delete(model) }.font(.caption)
                    }
                }
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(isPicked ? Color.accentColor.opacity(0.12) : Color.secondary.opacity(0.06)))
        .contentShape(Rectangle())
    }
}
