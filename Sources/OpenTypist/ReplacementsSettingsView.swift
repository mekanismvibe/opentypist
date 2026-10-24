import SwiftUI

/// Settings → Replacements: snippets and the macOS Text Replacements list.
struct ReplacementsSettings: View {
    @ObservedObject private var replacements = TextReplacements.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        Form {
            Section {
                Toggle("Expand text replacements as I type", isOn: $settings.textReplacementsEnabled)
                Text("Type a shortcut, then a space or punctuation, and it becomes the full text. Works in every app, including browsers, Slack and Discord where macOS's own replacements don't. Press Delete right after to undo.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }

            Section("Your snippets") {
                ForEach(replacements.snippets) { snippet in
                    HStack(alignment: .top) {
                        Text(snippet.shortcut).font(.body.monospaced()).frame(width: 130, alignment: .leading)
                        Text(snippet.expansion).lineLimit(3).frame(maxWidth: .infinity, alignment: .leading)
                        Button { replacements.remove(snippet) } label: { Image(systemName: "minus.circle") }
                            .buttonStyle(.borderless)
                            .help("Delete this snippet")
                    }
                }
                HStack(alignment: .top) {
                    TextField("Shortcut", text: $state.newSnippetShortcut)
                        .font(.body.monospaced())
                        .frame(width: 130)
                    TextField("Becomes…", text: $state.newSnippetExpansion, axis: .vertical)
                        .lineLimit(1...4)
                    Button("Add", action: addSnippet)
                        .disabled(state.newSnippetShortcut.isEmpty || state.newSnippetExpansion.isEmpty)
                }
                if let message = state.snippetMessage {
                    Text(message).font(.callout).foregroundStyle(.red)
                } else {
                    Text("For example \"@@\" → your email address, or \";addr\" → your address. Line breaks in the text are kept.")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }

            Section("macOS Text Replacements") {
                Toggle("Use my macOS Text Replacements (\(replacements.systemReplacements.count))", isOn: $settings.useSystemReplacements)
                ForEach(replacements.systemReplacements) { item in
                    HStack {
                        Text(item.shortcut).font(.body.monospaced()).frame(width: 130, alignment: .leading)
                        Text(item.phrase).lineLimit(2).foregroundStyle(.secondary)
                    }
                }
                HStack {
                    Text("Edit these in System Settings → Keyboard → Text Replacements. Your snippets win if both have the same shortcut.")
                        .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("Reload") { replacements.reloadSystem() }
                    Button("Open Keyboard Settings") {
                        if let url = URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension") {
                            NSWorkspace.shared.open(url)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { replacements.reloadSystem() }
    }

    private func addSnippet() {
        if let message = replacements.add(shortcut: state.newSnippetShortcut, expansion: state.newSnippetExpansion) {
            state.snippetMessage = message
        } else {
            state.newSnippetShortcut = ""
            state.newSnippetExpansion = ""
            state.snippetMessage = nil
        }
    }
}
