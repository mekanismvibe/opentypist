import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers

struct SettingsView: View {
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        TabView(selection: $state.settingsTab) {
            GeneralSettings().tabItem { Label("General", systemImage: "gearshape") }.tag("general")
            AppsSettings().tabItem { Label("Apps", systemImage: "square.grid.2x2") }.tag("apps")
            ModelSettings().tabItem { Label("Model", systemImage: "cpu") }.tag("model")
            StatsSettings().tabItem { Label("Stats", systemImage: "chart.bar") }.tag("stats")
        }
        .frame(width: 620, height: 600)
    }
}

private struct GeneralSettings: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var status = AppStatus.shared

    var body: some View {
        Form {
            Section {
                Toggle("Enable completions", isOn: $settings.enabled)
                Picker("Suggestion length", selection: $settings.length) {
                    ForEach(CompletionLength.allCases) { Text($0.title).tag($0) }
                }
                Toggle("Use app name and window title as context", isOn: $settings.useAppContext)
                Toggle("Use text visible on screen as context", isOn: $settings.useScreenContext)
                Toggle("Add a space after an accepted word", isOn: $settings.acceptTrailingSpace)
            }
            Section("Keys") {
                LabeledContent("Accept next word", value: "Tab")
                LabeledContent("Accept whole suggestion", value: "Shift-Tab")
                LabeledContent("Dismiss", value: "Esc")
                Text("Keep typing to ignore a suggestion. Option-Tab always sends a normal Tab.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Section("Permissions") {
                PermissionsPanel()
            }
            Section("Diagnostics") {
                LabeledContent("Last check") { Text(status.lastCheck).multilineTextAlignment(.trailing) }
                LabeledContent("Keyboard monitor", value: status.keyboardTapRunning ? "Running" : "Not running")
            }
        }
        .formStyle(.grouped)
    }
}

private struct AppsSettings: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var state = ViewState.shared
    private var selection: String? { state.selectedApp }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Completions are turned off in these apps:")
            List(selection: $state.selectedApp) {
                ForEach(settings.disabledApps, id: \.self) { bundleID in
                    AppRow(bundleID: bundleID).tag(bundleID)
                }
            }
            .listStyle(.bordered(alternatesRowBackgrounds: true))
            HStack {
                Button("Add App…", action: addApp)
                Button("Remove") {
                    if let selection { settings.setDisabled(false, bundleID: selection) }
                    state.selectedApp = nil
                }
                .disabled(selection == nil)
                Spacer()
                Button("Restore Defaults") { settings.disabledApps = Settings.defaultDisabledApps }
            }
            Text("Tip: the menu bar icon has a “Disable in …” toggle for the app you are using.")
                .font(.callout).foregroundStyle(.secondary)
        }
        .padding()
    }

    private func addApp() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.application]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.allowsMultipleSelection = true
        guard panel.runModal() == .OK else { return }
        for url in panel.urls {
            if let id = Bundle(url: url)?.bundleIdentifier { settings.setDisabled(true, bundleID: id) }
        }
    }
}

private struct AppRow: View {
    let bundleID: String

    var body: some View {
        let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
        HStack {
            if let url {
                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().frame(width: 18, height: 18)
                Text(FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: ""))
            } else {
                Image(systemName: "app.dashed").frame(width: 18, height: 18)
                Text(bundleID)
            }
            Spacer()
            Text(bundleID).font(.caption).foregroundStyle(.secondary)
        }
    }
}

private struct ModelSettings: View {
    @ObservedObject private var models = ModelManager.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var status = AppStatus.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Engine: \(engineText)").font(.callout)
                Spacer()
                if let url = models.activeModelURL {
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                }
            }
            ModelPicker(showsDelete: true)
            Divider()
            HStack {
                if let custom = settings.customModelPath {
                    Text("Using your own file: \(URL(fileURLWithPath: custom).lastPathComponent)")
                        .font(.callout).lineLimit(1).truncationMode(.middle)
                    Button("Stop Using It") { models.useCustomModel(nil) }
                } else {
                    Text("Have another GGUF model? Base (non-chat) models work best.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Choose .gguf File…") { models.chooseModelFile() }
            }
        }
        .padding()
    }

    private var engineText: String {
        switch status.engine {
        case .idle: return models.activeModelURL == nil ? "no model" : "idle"
        case .loading: return "loading \(models.activeModelName ?? "model")…"
        case .ready: return "ready (\(models.activeModelName ?? "model"))"
        case .failed(let message): return message
        }
    }
}

private struct StatsSettings: View {
    @ObservedObject private var stats = Stats.shared

    var body: some View {
        Form {
            Section {
                LabeledContent("Words accepted today", value: "\(stats.wordsAcceptedToday)")
                LabeledContent("Words accepted", value: "\(stats.wordsAccepted)")
                LabeledContent("Characters inserted", value: "\(stats.charactersInserted)")
                LabeledContent("Keystrokes saved", value: "\(stats.keystrokesSaved)")
                LabeledContent("Suggestions shown", value: "\(stats.suggestionsShown)")
                LabeledContent("Accept rate", value: acceptRate)
            }
            Section {
                Button("Reset Statistics", role: .destructive) { stats.reset() }
                Text("Only counts are stored, never the text you type.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private var acceptRate: String {
        guard stats.suggestionsShown > 0 else { return "–" }
        let rate = Double(stats.acceptKeyPresses) / Double(stats.suggestionsShown) * 100
        return String(format: "%.0f%%", min(rate, 100))
    }
}
