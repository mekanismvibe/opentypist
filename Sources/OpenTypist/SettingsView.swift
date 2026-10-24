import AppKit
import Charts
import Combine
import SwiftUI
import UniformTypeIdentifiers

struct SettingsView: View {
    @ObservedObject private var state = ViewState.shared
    /// Taller only for snapshots of whole tabs.
    var height: CGFloat = 600

    var body: some View {
        TabView(selection: $state.settingsTab) {
            GeneralSettings().tabItem { Label("General", systemImage: "gearshape") }.tag("general")
            PersonalSettings().tabItem { Label("Personal", systemImage: "person.crop.circle") }.tag("personal")
            ReplacementsSettings().tabItem { Label("Replacements", systemImage: "text.badge.plus") }.tag("replacements")
            AppsSettings().tabItem { Label("Apps", systemImage: "square.grid.2x2") }.tag("apps")
            ModelSettings().tabItem { Label("Model", systemImage: "cpu") }.tag("model")
            StatsSettings().tabItem { Label("Stats", systemImage: "chart.bar") }.tag("stats")
        }
        .frame(width: 620, height: height)
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
                Picker("Suggestions", selection: $settings.confidence) {
                    ForEach(ConfidenceLevel.allCases) { Text($0.title).tag($0) }
                }
                Text("Suggestions stop where the model starts guessing. \"Fewer, more accurate\" shows about half as often but is right much more of the time. Finishing a word you're in the middle of typing always uses a looser bar, since those are right nearly 9 times out of 10.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Toggle("Suggest in the middle of a line", isOn: $settings.midLineSuggestions)
                Toggle("Use app name and window title as context", isOn: $settings.useAppContext)
                Toggle("Use text visible on screen as context", isOn: $settings.useScreenContext)
                Toggle("Add a space after accepting a suggestion", isOn: $settings.acceptTrailingSpace)
            }
            Section("App") {
                LaunchAtLoginToggle()
                Toggle("Show menu bar icon", isOn: $settings.showMenuBarIcon)
                if !settings.showMenuBarIcon {
                    Text("To get back to Settings, open OpenTypist again from Applications or Spotlight.")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }
            Section("Autocorrect") {
                AutocorrectSettings()
            }
            Section("Keys") {
                KeysSettings()
            }
            Section("Permissions") {
                PermissionsPanel()
            }
            Section("Memory & battery") {
                PowerSettings()
            }
            Section("Updates") {
                UpdatesPanel()
            }
            Section("Diagnostics") {
                LabeledContent("Last check") { Text(status.lastCheck).multilineTextAlignment(.trailing) }
                LabeledContent("Keyboard monitor", value: status.keyboardTapRunning ? "Running" : "Not running")
            }
        }
        .formStyle(.grouped)
    }
}

private struct KeysSettings: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var recorder = KeyRecorder.shared

    var body: some View {
        LabeledContent("Accept next word") { KeyRecorderField(id: "word", combo: $settings.acceptKeys.word) }
        LabeledContent("Accept whole suggestion") { KeyRecorderField(id: "all", combo: $settings.acceptKeys.all) }
        LabeledContent("Dismiss", value: "Esc")
        if let message = recorder.message {
            Text(message).font(.callout).foregroundStyle(.red)
        } else if settings.acceptKeys.word == settings.acceptKeys.all {
            Text("Both actions use the same key; the whole suggestion wins.").font(.callout).foregroundStyle(.orange)
        }
        HStack {
            Text("These keys only do anything while a suggestion is showing; otherwise they reach the app as usual. Apps that use Tab themselves can get their own keys in the Apps tab.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Spacer()
            Button("Reset") { settings.acceptKeys = .standard }.disabled(settings.acceptKeys == .standard)
        }
    }
}

private struct PowerSettings: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var power = PowerMonitor.shared
    @ObservedObject private var status = AppStatus.shared
    @ObservedObject private var models = ModelManager.shared

    private var smaller: ModelInfo? { ModelCatalog.model(id: settings.smallerModelID) }
    private var usesSmaller: Bool { settings.batteryBehavior == .smallerModel || settings.lowPowerBehavior == .smallerModel }

    var body: some View {
        Toggle("Unload the model when I'm not typing", isOn: $settings.unloadWhenIdle)
        Picker("After", selection: $settings.idleUnloadMinutes) {
            ForEach([5, 15, 30, 60, 120], id: \.self) { minutes in
                Text(minutes < 60 ? "\(minutes) minutes" : "\(minutes / 60) hour\(minutes > 60 ? "s" : "")").tag(minutes)
            }
        }
        .disabled(!settings.unloadWhenIdle)
        Text(unloadNote).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)

        Picker("On battery", selection: $settings.batteryBehavior) {
            ForEach(PowerBehavior.allCases) { Text($0.title).tag($0) }
        }
        Picker("In Low Power Mode", selection: $settings.lowPowerBehavior) {
            ForEach(PowerBehavior.allCases) { Text($0.title).tag($0) }
        }
        Picker("Smaller model", selection: $settings.smallerModelID) {
            ForEach(ModelCatalog.all.filter { $0.size < 4_000_000_000 }) { model in
                Text("\(model.name) (\(model.sizeText))\(models.isDownloaded(model) ? "" : ", not downloaded")").tag(model.id)
            }
        }
        .disabled(!usesSmaller)
        if usesSmaller, let smaller, !models.isDownloaded(smaller) {
            HStack {
                Text("\(smaller.name) isn't downloaded yet, so the usual model is used until it is.")
                    .font(.callout).foregroundStyle(.orange)
                Spacer()
                Button("Download (\(smaller.sizeText))") { models.startDownload(smaller) }.disabled(models.isDownloading)
            }
        }
        Text("Right now: \(power.onBattery ? "on battery" : "plugged in")\(power.lowPowerMode ? ", Low Power Mode on" : "").")
            .font(.caption).foregroundStyle(.secondary)
    }

    private var unloadNote: String {
        let size = models.activeModelURL.flatMap { try? FileManager.default.attributesOfItem(atPath: $0.path)[.size] as? NSNumber }
        let memory = size.map { ByteCountFormatter.string(fromByteCount: $0.int64Value, countStyle: .memory) } ?? "the model's memory"
        let load = status.lastModelLoadSeconds.map { String(format: "%.1f seconds", $0) } ?? "a moment"
        return "Frees about \(memory). The model loads again on your next keystroke; the last load took \(load), and the first suggestion waits for it."
    }
}

private struct UpdatesPanel: View {
    @ObservedObject private var updater = Updater.shared
    @ObservedObject private var settings = Settings.shared

    var body: some View {
        LabeledContent("Version", value: updater.currentVersion)
        Toggle("Check for updates automatically", isOn: $settings.checkForUpdates)
        HStack {
            Text(statusText).font(.callout).foregroundStyle(statusIsError ? .red : .secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
            if let release = updater.availableRelease {
                Button("Release Notes") { NSWorkspace.shared.open(release.page) }
                Button("Install and Relaunch") { updater.install() }.keyboardShortcut(.defaultAction)
            } else {
                Button("Check Now") { updater.check() }.disabled(updater.state == .checking)
            }
        }
        Text("Updates come from the project's GitHub releases; checking sends nothing about you.")
            .font(.caption).foregroundStyle(.secondary)
    }

    private var statusIsError: Bool {
        if case .failed = updater.state { return true }
        return false
    }

    private var statusText: String {
        switch updater.state {
        case .idle:
            return updater.lastCheck.map { "Last checked \($0.formatted(date: .abbreviated, time: .shortened))" } ?? "Not checked yet"
        case .checking: return "Checking…"
        case .upToDate: return "OpenTypist is up to date."
        case .available(let release): return "OpenTypist \(release.version) is available."
        case .installing(let step): return step
        case .failed(let message): return message
        }
    }
}

private struct AutocorrectSettings: View {
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        // Read so the view refreshes after an import.
        let _ = state.autocorrectListsVersion
        let autocorrector = AppStatus.shared.autocorrector
        Toggle("Fix common misspellings as you type", isOn: $settings.autocorrectEnabled)
        Toggle("Let the AI model fix other typos", isOn: $settings.aiTypoFix)
        Text("The model checks typos the lists can't settle (several possible fixes, or a misspelling that is also a real word) and words the macOS dictionary doesn't know, and only fixes one when the correction clearly fits the sentence better. Press Delete right after a fix to undo it.")
            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        LabeledContent("Built-in list", value: "\(autocorrector?.builtInCount ?? 0) entries (Wikipedia)")
        ForEach(autocorrector?.userListCounts ?? [], id: \.name) { list in
            LabeledContent(list.name, value: "\(list.rules) entries")
        }
        HStack {
            Text("Import plain misspelling->correction lists or AutoHotkey scripts like AutoCorrect.ahk.")
                .font(.callout).foregroundStyle(.secondary)
            Spacer()
            Button("Show Folder") {
                try? FileManager.default.createDirectory(at: Autocorrector.userListsDirectory, withIntermediateDirectories: true)
                NSWorkspace.shared.open(Autocorrector.userListsDirectory)
            }
            Button("Import List…", action: importList)
        }
    }

    private func importList() {
        let panel = NSOpenPanel()
        panel.allowsOtherFileTypes = true
        panel.allowsMultipleSelection = true
        panel.title = "Import autocorrect lists (.ahk or .txt)"
        guard panel.runModal() == .OK else { return }
        for url in panel.urls { _ = try? AppStatus.shared.autocorrector?.importList(from: url) }
        state.autocorrectListsVersion += 1
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

            Divider().padding(.vertical, 4)
            Text("Different accept keys in these apps:")
            List {
                ForEach(settings.appAcceptKeys.keys.sorted(), id: \.self) { bundleID in
                    HStack {
                        AppRow(bundleID: bundleID, showsIdentifier: false)
                        KeyRecorderField(id: "\(bundleID).word", combo: binding(bundleID, \.word))
                            .help("Accept next word")
                        KeyRecorderField(id: "\(bundleID).all", combo: binding(bundleID, \.all))
                            .help("Accept whole suggestion")
                        Button { settings.appAcceptKeys[bundleID] = nil } label: { Image(systemName: "minus.circle") }
                            .buttonStyle(.borderless)
                            .help("Use the normal keys in this app")
                    }
                }
            }
            .listStyle(.bordered(alternatesRowBackgrounds: true))
            .frame(minHeight: 90)
            HStack {
                Button("Add App…") {
                    for id in Self.chooseApps() where settings.appAcceptKeys[id] == nil {
                        settings.appAcceptKeys[id] = AcceptKeys(word: .rightArrow, all: .optionRightArrow)
                    }
                }
                Spacer()
                Text("For example → and ⌥→ in apps where Tab already does something.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
        .padding()
    }

    private func binding(_ bundleID: String, _ key: WritableKeyPath<AcceptKeys, KeyCombo>) -> Binding<KeyCombo> {
        Binding(
            get: { (settings.appAcceptKeys[bundleID] ?? settings.acceptKeys)[keyPath: key] },
            set: { combo in
                var keys = settings.appAcceptKeys[bundleID] ?? settings.acceptKeys
                keys[keyPath: key] = combo
                settings.appAcceptKeys[bundleID] = keys
            }
        )
    }

    private func addApp() {
        for id in Self.chooseApps() { settings.setDisabled(true, bundleID: id) }
    }

    private static func chooseApps() -> [String] {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.application]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        panel.allowsMultipleSelection = true
        guard panel.runModal() == .OK else { return [] }
        return panel.urls.compactMap { Bundle(url: $0)?.bundleIdentifier }
    }
}

private struct AppRow: View {
    let bundleID: String
    var showsIdentifier = true

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
            if showsIdentifier {
                Text(bundleID).font(.caption).foregroundStyle(.secondary)
            }
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

/// Last 30 days of accepted words: three headline numbers and a bar per
/// day, with the day's count shown on hover.
private struct WordsPerDayChart: View {
    @ObservedObject private var stats = Stats.shared
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        let days = stats.recentDays(30)
        let week = days.suffix(7).reduce(0) { $0 + $1.words }
        let month = days.reduce(0) { $0 + $1.words }
        let best = days.max { $0.words < $1.words }
        let hovered = state.hoveredStatsDay.flatMap { day in days.first { Calendar.current.isDate($0.date, inSameDayAs: day) } }

        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 28) {
                StatTile(title: "This week", value: "\(week)")
                StatTile(title: "Last 30 days", value: "\(month)")
                StatTile(title: "Best day", value: best.map { $0.words > 0 ? "\($0.words)" : "–" } ?? "–",
                         detail: best.flatMap { $0.words > 0 ? $0.date.formatted(.dateTime.month(.abbreviated).day()) : nil })
            }
            Chart(days, id: \.date) { day in
                BarMark(x: .value("Day", day.date, unit: .day), y: .value("Words", day.words), width: .ratio(0.7))
                    .cornerRadius(3)
                    .foregroundStyle(Color.accentColor.opacity(hovered == nil || hovered?.date == day.date ? 1 : 0.4))
            }
            .chartYAxis {
                AxisMarks(position: .leading) { _ in
                    AxisGridLine().foregroundStyle(Color.secondary.opacity(0.2))
                    AxisValueLabel()
                }
            }
            .chartXAxis {
                // Weekly labels, stopping short of the right edge so none clip.
                AxisMarks(values: stride(from: 2, to: days.count - 3, by: 7).map { days[$0].date }) { _ in
                    AxisValueLabel(format: .dateTime.month(.abbreviated).day())
                }
            }
            .chartOverlay { proxy in
                GeometryReader { geometry in
                    Rectangle().fill(.clear).contentShape(Rectangle())
                        .onContinuousHover { phase in
                            switch phase {
                            case .active(let location):
                                guard let plot = proxy.plotFrame else { return }
                                let x = location.x - geometry[plot].origin.x
                                state.hoveredStatsDay = proxy.value(atX: x, as: Date.self)
                            case .ended:
                                state.hoveredStatsDay = nil
                            }
                        }
                }
            }
            .frame(height: 160)
            Text(hovered.map { "\($0.date.formatted(.dateTime.weekday(.wide).month(.abbreviated).day())): \($0.words) word\($0.words == 1 ? "" : "s") accepted" }
                 ?? "Hover over a bar to see the day.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
    }
}

private struct StatTile: View {
    let title: String
    let value: String
    var detail: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            Text(value).font(.title2.weight(.semibold)).monospacedDigit()
            if let detail { Text(detail).font(.caption2).foregroundStyle(.secondary) }
        }
    }
}

private struct StatsSettings: View {
    @ObservedObject private var stats = Stats.shared

    var body: some View {
        Form {
            Section("Words accepted per day") {
                WordsPerDayChart()
            }
            Section {
                LabeledContent("Words accepted today", value: "\(stats.wordsAcceptedToday)")
                LabeledContent("Words accepted", value: "\(stats.wordsAccepted)")
                LabeledContent("Characters inserted", value: "\(stats.charactersInserted)")
                LabeledContent("Keystrokes saved", value: "\(stats.keystrokesSaved)")
                LabeledContent("Suggestions shown", value: "\(stats.suggestionsShown)")
                LabeledContent("Typos fixed", value: "\(stats.correctionsMade)")
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
