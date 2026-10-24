import SwiftUI

/// Settings → Personal: the profile, what was learned, never-correct words
/// and writing samples.
struct PersonalSettings: View {
    @ObservedObject private var personal = Personalization.shared
    @ObservedObject private var samples = WritingSamples.shared
    @ObservedObject private var settings = Settings.shared
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        Form {
            profileSection
            if let proposal = personal.profile.proposal { proposalSection(proposal) }
            historySection
            learnedSection
            neverCorrectSection
            samplesSection
        }
        .formStyle(.grouped)
        .sheet(isPresented: $state.showingSamples) { SamplesList() }
    }

    // MARK: Profile

    private var isEditing: Bool { state.profileDraft != state.profileDraftBase }

    private var profileSection: some View {
        Section("Your profile") {
            Text("OpenTypist reads this before every suggestion. Write facts about yourself and how you write, in the first person (\"I'm a nurse in Toronto. I keep texts short.\"), rather than instructions.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            TextEditor(text: $state.profileDraft)
                .font(.body)
                .frame(minHeight: 110)
                .scrollContentBackground(.hidden)
                .padding(4)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.secondary.opacity(0.08)))
            HStack {
                Text("\(state.profileDraft.count)/\(Personalization.maxProfileLength)")
                    .font(.caption).foregroundStyle(state.profileDraft.count > Personalization.maxProfileLength ? .red : .secondary)
                Spacer()
                Button("Revert") { state.profileDraft = state.profileDraftBase }.disabled(!isEditing)
                Button("Save") {
                    personal.saveProfileText(state.profileDraft)
                    state.profileDraft = personal.profile.text
                    state.profileDraftBase = personal.profile.text
                }
                .disabled(!isEditing)
                .keyboardShortcut("s", modifiers: .command)
            }
            Toggle("Use my profile in suggestions", isOn: $settings.useProfile)
            Toggle("Lock my profile (OpenTypist won't change it)", isOn: Binding(
                get: { personal.profile.locked },
                set: { personal.setLocked($0) }
            ))
            Picker("When OpenTypist learns something new", selection: Binding(
                get: { personal.profile.mode },
                set: { personal.setMode($0) }
            )) {
                ForEach(ProfileUpdateMode.allCases) { Text($0.title).tag($0) }
            }
            .disabled(personal.profile.locked)
            HStack {
                Text("OpenTypist adds what it learns as lines at the end. If you edit or delete one of those lines, that topic is yours and OpenTypist stops touching it.")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Spacer()
                Button("Update Now") {
                    let note = personal.updateProfile()
                    AppStatus.shared.note(note.map { "Profile: \($0)" } ?? "Profile: nothing new to add")
                }
                .disabled(personal.profile.locked || isEditing)
            }
        }
    }

    private func proposalSection(_ proposal: String) -> some View {
        Section("Suggested update") {
            if let note = personal.profile.proposalNote {
                Text(note).font(.headline)
            }
            Text(proposal)
                .font(.callout)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.accentColor.opacity(0.08)))
            HStack {
                Spacer()
                Button("Dismiss") { personal.dismissProposal() }
                Button("Apply") { personal.applyProposal() }.keyboardShortcut(.defaultAction)
            }
        }
    }

    private var historySection: some View {
        Section("History") {
            ForEach(personal.profile.history.reversed().prefix(12)) { version in
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(version.note).font(.callout)
                        Text("\(version.date.formatted(date: .abbreviated, time: .shortened)) · \(source(version.source))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if version.text != personal.profile.text {
                        Button("Restore") { personal.restore(version) }
                    } else {
                        Text("Current").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .help(version.text)
            }
        }
    }

    private func source(_ source: ProfileVersion.Source) -> String {
        switch source {
        case .initial: return "default"
        case .you: return "you"
        case .openTypist: return "OpenTypist"
        case .restored: return "restored"
        }
    }

    // MARK: Learned

    private var learnedSection: some View {
        Section("Learned from your typing") {
            Toggle("Learn from how I type", isOn: $settings.learnHabits)
            Text("Names, words the dictionary doesn't know, your spelling, greetings and sign-offs, and how casually you write in each app. Only keys you type are used, never text on screen, and only the counts are kept, encrypted on this Mac.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            LearnedChips(title: "Names", items: personal.learnedNames) { personal.removeLearnedName($0) }
            LearnedChips(title: "Your words", items: personal.learnedWords) { personal.removeNeverCorrect($0) }
            ForEach(personal.generatedLines().filter { ["spelling", "greetings", "style"].contains($0.topic) }, id: \.topic) { entry in
                Text(entry.line).font(.callout)
            }
            HStack {
                Text("Typed \(personal.learned.names.values.reduce(0, +) + personal.learned.words.values.reduce(0, +)) names and words so far; \(Personalization.learnThreshold) uses make one count.")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Forget Everything Learned", role: .destructive) { personal.forgetLearned() }
            }
        }
    }

    private var neverCorrectSection: some View {
        Section("Never correct") {
            Text("Autocorrect leaves these words alone. Words are added when you undo a fix with Delete; your learned words above count too.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            LearnedChips(title: "Words", items: personal.learned.neverCorrect.sorted()) { personal.removeNeverCorrect($0) }
            HStack {
                TextField("Add a word", text: $state.newNeverCorrectWord)
                    .onSubmit(addNeverCorrect)
                Button("Add", action: addNeverCorrect).disabled(state.newNeverCorrectWord.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
    }

    private func addNeverCorrect() {
        personal.addNeverCorrect(state.newNeverCorrectWord)
        state.newNeverCorrectWord = ""
    }

    // MARK: Samples

    private var samplesSection: some View {
        Section("Writing samples") {
            Toggle("Keep samples of my writing to match my style", isOn: $settings.keepWritingSamples)
            Text("When you leave a text field or send a message, OpenTypist keeps what you wrote there (up to 600 characters) and shows the model a few recent samples from the same app, so suggestions sound like you. Samples are encrypted on this Mac and never leave it. Apps you turned off, password fields, card numbers and long codes are never kept.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Text("\(samples.samples.count) sample\(samples.samples.count == 1 ? "" : "s") stored").font(.callout)
                Spacer()
                Button("Show Samples…") { state.showingSamples = true }.disabled(samples.samples.isEmpty)
                Button("Delete All", role: .destructive) { samples.deleteAll() }.disabled(samples.samples.isEmpty)
            }
        }
    }
}

/// A wrapping row of removable words.
private struct LearnedChips: View {
    let title: String
    let items: [String]
    let remove: (String) -> Void

    var body: some View {
        LabeledContent(title) {
            if items.isEmpty {
                Text("None yet").foregroundStyle(.secondary)
            } else {
                ChipLayout(spacing: 6) {
                    ForEach(items, id: \.self) { item in
                        HStack(spacing: 3) {
                            Text(item)
                            Button { remove(item) } label: { Image(systemName: "xmark.circle.fill") }
                                .buttonStyle(.plain).foregroundStyle(.secondary)
                                .help("Remove \(item)")
                        }
                        .font(.callout)
                        .padding(.horizontal, 7).padding(.vertical, 2)
                        .background(Capsule().fill(Color.secondary.opacity(0.12)))
                    }
                }
            }
        }
    }
}

/// Lays children out left to right, wrapping onto new rows.
private struct ChipLayout: Layout {
    var spacing: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 300
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0, maxX: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0, x + size.width > width { x = 0; y += rowHeight + spacing; rowHeight = 0 }
            x += size.width + spacing
            maxX = max(maxX, x - spacing)
            rowHeight = max(rowHeight, size.height)
        }
        return CGSize(width: min(maxX, width), height: y + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowHeight: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX { x = bounds.minX; y += rowHeight + spacing; rowHeight = 0 }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}

/// Sheet listing stored writing samples, each deletable.
private struct SamplesList: View {
    @ObservedObject private var samples = WritingSamples.shared
    @ObservedObject private var state = ViewState.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Writing samples").font(.title2.weight(.semibold))
            List {
                ForEach(samples.samples.reversed()) { sample in
                    HStack(alignment: .top) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text("\(sample.appName)\(sample.windowTitle.map { " · \($0)" } ?? "") · \(sample.date.formatted(date: .abbreviated, time: .shortened))")
                                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            Text(sample.text).font(.callout).lineLimit(4)
                        }
                        Spacer()
                        Button { samples.delete(sample) } label: { Image(systemName: "trash") }
                            .buttonStyle(.borderless)
                            .help("Delete this sample")
                    }
                }
            }
            HStack {
                Spacer()
                Button("Done") { state.showingSamples = false }.keyboardShortcut(.defaultAction)
            }
        }
        .padding()
        .frame(width: 560, height: 480)
    }
}
