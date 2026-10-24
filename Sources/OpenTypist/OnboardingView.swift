import SwiftUI

/// First-launch walkthrough: what OpenTypist does, the permissions it needs,
/// and which model to download. Nothing is downloaded until the user picks.
struct OnboardingView: View {
    @ObservedObject private var state = ViewState.shared
    @ObservedObject private var models = ModelManager.shared
    @ObservedObject private var settings = Settings.shared
    let onFinish: () -> Void

    private let lastStep = 3

    var body: some View {
        VStack(spacing: 0) {
            Group {
                switch state.onboardingStep {
                case 0: welcome
                case 1: permissions
                case 2: modelStep
                default: ready
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(28)

            Divider()
            HStack {
                HStack(spacing: 6) {
                    ForEach(0...lastStep, id: \.self) { step in
                        Circle()
                            .fill(step == state.onboardingStep ? Color.accentColor : Color.secondary.opacity(0.3))
                            .frame(width: 7, height: 7)
                    }
                }
                Spacer()
                if state.onboardingStep > 0 {
                    Button("Back") { state.onboardingStep -= 1 }
                }
                if state.onboardingStep < lastStep {
                    Button(nextTitle) { state.onboardingStep += 1 }
                        .keyboardShortcut(.defaultAction)
                } else {
                    Button("Start Typing", action: onFinish)
                        .keyboardShortcut(.defaultAction)
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 14)
        }
        .frame(width: 640, height: 600)
    }

    private var nextTitle: String {
        switch state.onboardingStep {
        case 1 where !state.accessibilityGranted: return "Continue Without Access"
        case 2 where models.activeModelURL == nil && !models.isDownloading: return "Skip for Now"
        default: return "Continue"
        }
    }

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 18) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 72, height: 72)
            Text("Welcome to OpenTypist").font(.largeTitle.weight(.semibold))
            Text("OpenTypist suggests the next few words as you type, in almost any app. Suggestions show up as faint text right after your cursor.")
                .font(.title3).fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 10) {
                KeyHint(key: "tab", text: "accepts the next word")
                KeyHint(key: "⇧ tab", text: "accepts the whole suggestion")
                KeyHint(key: "esc", text: "dismisses it, or just keep typing")
            }
            Text("Everything runs on this Mac with an open source AI model. Nothing you type is sent anywhere.")
                .foregroundStyle(.secondary)
        }
    }

    private var permissions: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Permissions").font(.largeTitle.weight(.semibold))
            Text("OpenTypist needs your permission to see what you type and to read the screen around it. Click Turn On, then enable OpenTypist in the System Settings list that opens.")
                .fixedSize(horizontal: false, vertical: true)
            PermissionsPanel()
        }
    }

    private var modelStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Choose a Model").font(.largeTitle.weight(.semibold))
            Text("Pick the AI model that writes the suggestions. Bigger models are smarter but slower and use more memory. The one marked Recommended fits your Mac best. You can change this later in Settings.")
                .fixedSize(horizontal: false, vertical: true)
            ModelPicker()
        }
    }

    private var ready: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text(readyTitle).font(.largeTitle.weight(.semibold))
            Text(readyDetail).font(.title3).fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 10) {
                KeyHint(key: "tab", text: "accepts the next word")
                KeyHint(key: "⇧ tab", text: "accepts the whole suggestion")
                KeyHint(key: "esc", text: "dismisses it")
            }
            Text("OpenTypist lives in the menu bar (the text-cursor icon). From there you can pause it, turn it off for one app, change the suggestion length, and open this guide again.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 6) {
                LaunchAtLoginToggle()
                Toggle("Add a space after accepting a suggestion", isOn: $settings.acceptTrailingSpace)
            }
        }
    }

    private var readyTitle: String {
        if !state.accessibilityGranted { return "Almost There" }
        if models.activeModelURL == nil { return models.isDownloading ? "Downloading…" : "No Model Yet" }
        return "You're All Set"
    }

    private var readyDetail: String {
        if !state.accessibilityGranted {
            return "Turn on Accessibility access for OpenTypist (step 2) and suggestions will start appearing."
        }
        if models.activeModelURL == nil {
            return models.isDownloading
                ? "Your model is downloading. Suggestions start automatically when it finishes. You can close this window."
                : "Choose and download a model in step 3 (or later from Settings) to start getting suggestions."
        }
        return "Open Notes, Mail or Messages and start typing. After a moment, faint grey words appear after your cursor."
    }
}

private struct KeyHint: View {
    let key: String
    let text: String

    var body: some View {
        HStack(spacing: 10) {
            Text(key)
                .font(.system(.callout, design: .rounded).weight(.semibold))
                .padding(.horizontal, 8).padding(.vertical, 3)
                .background(RoundedRectangle(cornerRadius: 5).stroke(Color.secondary.opacity(0.6)))
                .frame(minWidth: 56)
            Text(text)
        }
    }
}
