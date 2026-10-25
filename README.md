# OpenTypist

Open source, on-device autocomplete for macOS. As you type in almost any app
(Mail, Messages, Slack, Notes, browsers…), OpenTypist predicts the next few
words with a local language model and shows them right after the caret as
ghost text: the field's own font in a faded version of its text color.

- **Tab** accepts the next word
- **Shift-Tab** accepts the whole suggestion
- **Esc** dismisses it (or just keep typing)

Both accept keys can be changed (Settings → General → Keys), and apps where
Tab already does something can get their own keys (Settings → Apps), for
example → and ⌥→ in Slack.

Everything runs locally with [llama.cpp](https://github.com/ggml-org/llama.cpp)
on the GPU (Metal). Nothing you type leaves your Mac, password fields are never
read, sign-in and verification fields are left alone, and the only data stored
is a count of accepted words.

## Models

Nothing is downloaded until you pick a model in the setup guide or in
Settings → Model. OpenTypist reads your chip and memory and marks the model
that fits your Mac best. All of them are base (non-chat) models, 4-bit
quantized GGUFs from Hugging Face, pinned to a revision and checked against
their SHA-256:

| Model | Size | Recommended for |
| --- | --- | --- |
| Qwen 3 1.7B | 1.1 GB | 8 GB or older Macs, longest battery life; about 45% faster per keystroke than Gemma 4 E2B, finishes half-typed words right 84% of the time vs 87% |
| Gemma 4 E2B | 3.4 GB | 16 GB Macs (and base-chip Macs up to 24 GB); the default. Q4_0 format: same measured quality as Q4_K_M, a few percent faster on Apple GPUs (an existing Q4_K_M download keeps working) |
| Gemma 4 E4B | 5.3 GB | 24 GB+ on Pro/Max chips, 32 GB+ otherwise |
| Qwen 3 8B | 5.0 GB | 16 GB+, if you prefer a dense model |
| Gemma 4 26B A4B | 16.8 GB | 64 GB+ |
| Qwen 3 30B A3B | 18.6 GB | 48 GB+ |

You can also point OpenTypist at any other `.gguf` file.

## Autocorrect

OpenTypist also fixes misspellings as you type, in any app, the way
[AutoCorrect.ahk](https://github.com/oxinabox/AutoCorrect) does on Windows:

1. **Lists, instantly.** When a word ends (space, punctuation, Return), the
   last typed text is looked up in the misspelling lists: the built-in
   [Wikipedia list](Resources/autocorrect/README.md) (4,310 entries, CC BY-SA)
   plus any lists you import. OpenTypist swallows the ending key, deletes the
   typo, types the fix and sends the ending key again. Case follows what you
   typed (`Teh` → `The`, `TEH` → `THE`).
2. **The language model, a moment later.** Some typos the lists can't
   settle: entries with several possible fixes (`achive` → achieve or
   archive?), list "typos" that are real words (`loosing`, `payed`), and words
   the macOS dictionary doesn't know (`stoer`). For these the model scores
   each candidate in the surrounding text, with a keyboard-aware typo cost
   (swapped letters and neighbouring keys are likely slips), and fixes the word
   only if one candidate clearly fits better. This takes 20–120 ms and runs
   after you've moved on, re-typing anything you typed in the meantime.
   Capitalized words mid-sentence and ALL-CAPS words are left alone, since
   they're probably names or acronyms.

Press **Delete right after a fix** to undo it; that word won't be fixed again
this session.

A plain list beats the model for known misspellings (instant, free, never a
surprising guess), and the model beats a list where context matters, so
OpenTypist uses both. Import more lists in Settings → General → Autocorrect:
`misspelling->correction` text files or AutoHotkey scripts. The importer
understands `::typo::fix` hotstrings, the `*` (instant), `?` (inside words),
`C` (case-sensitive) and `B0` options, multi-word triggers (`doe snot` →
`does not`) and exception entries (`::design::` protecting it from
`:?:ign::ing`).

## Personalization

Settings → Personal (or `opentypist://settings/personal`):

- **Your profile.** A short note that goes at the top of every prompt, like a
  system prompt. Base models follow *facts* better than instructions, so write
  it in the first person ("I'm Alex, a nurse in Toronto. I sign emails
  'Cheers, Alex'.") rather than as commands. With that profile, the model
  signs an email "Alex" instead of "John". The profile is the first thing in
  the prompt and rarely changes, so the model caches it and it costs no
  speed.
- **It learns, if you let it.** From the keys you type (never from text on
  screen), OpenTypist notices names you mention, words the dictionary doesn't
  know, US vs British spelling, your usual greeting and sign-off, and which
  apps you write casually in. It adds these as plain lines at the end of the
  profile ("People and things I often mention: Sarah, Proxmox."), at most
  every ten minutes, or when you click **Update Now**.
- **You stay in charge.** Edit or delete one of OpenTypist's lines and that
  topic becomes yours: it won't touch it again. **Lock** the profile to stop
  all automatic changes, or choose **Ask me first** to review each update
  before it's applied. Every change goes into a **history** you can restore
  from.
- **Never correct.** Undoing a fix with Delete permanently stops autocorrect
  from touching that word. Words you type often that the dictionary doesn't
  know are left alone too, and you can add or remove words by hand.
- **Writing samples (opt-in, off by default).** When you leave a text field
  or send a message, OpenTypist keeps what you wrote (up to 600 characters)
  and shows the model a few recent samples from the same app, preferring the
  same conversation, so suggestions sound like you. Apps you turned off,
  password fields, card-like numbers and long codes are never kept. You can
  review and delete samples one by one.

Everything personal (profile, learned counts, samples, snippets) is stored
only on this Mac, AES-GCM encrypted, and never sent anywhere. Builds signed
with a Developer ID keep the key in the login Keychain. Ad-hoc builds keep it
in a key file only your user account can read, because the Keychain trusts
one exact ad-hoc binary and would ask again after every rebuild or update.
Data saved by older builds is moved over once (macOS asks to allow access to
the old Keychain item).

## Text replacements

Settings → Replacements: your macOS Text Replacements (System Settings →
Keyboard → Text Replacements) and your own snippets ("@@" → your email,
";addr" → your address) expand in every app, including browsers, Slack and
Discord where macOS's own replacements don't work. Type the shortcut, then a
space or punctuation. Line breaks in a snippet are typed as Shift-Return, so a
chat app doesn't send the message halfway. Press Delete right after to undo.

## Memory & battery

Settings → General → Memory & battery (all off by default):

- **Unload the model when I'm not typing** after 5 minutes to 2 hours. It frees
  about 3 GB with Gemma 4 E2B and reloads on your next keystroke: about 0.5–0.8 s
  while macOS still caches the file, a few seconds from a cold disk. Settings
  shows how long the last load took.
- **On battery** and **In Low Power Mode**: keep going, switch to a smaller
  model (Qwen 3 1.7B by default), or pause suggestions.

## Permissions

- **Accessibility** (required): reads the focused text field and caret, and
  lets Tab accept a suggestion.
- **Screen & System Audio Recording** (recommended): lets OpenTypist read the
  rest of the window (the email you're replying to, the chat above) with
  on-device OCR and add it to the prompt. Only the screen is captured, never
  audio; nothing is stored or sent anywhere. macOS asks OpenTypist to restart
  after you turn it on, and from time to time shows a "requesting to bypass the
  system private window picker" confirmation, which is normal for apps that
  capture windows.

The setup guide that opens on first launch walks through both. If macOS
blocks keyboard access even with Accessibility on, it also points you to
Input Monitoring.

## Features

- Inline suggestions in any app that exposes its text fields through
  Accessibility, including Chromium/Electron/WebKit apps (via text markers)
- Word-by-word or whole-suggestion accept, optionally adding a space after
  the accepted text (Settings → General, or the menu bar menu)
- Ghost text wraps like real text: when a suggestion doesn't fit before the
  field's right edge, or before a button inside the field (a send or
  microphone button, found by hit-testing the line to the right of the caret),
  the rest continues on the next line, aligned with where your lines start
- Launch at login, and an option to hide the menu bar icon (open the app again
  from Applications or Spotlight to get to Settings)
- Suggestion length: short (3 words), medium (7) or long (15)
- Context: the app name, window title and (with Screen Recording) the visible
  text of the window are added to the prompt, so suggestions fit the email,
  chat or document you are in
- Diagnostics: the menu bar and Settings → General show what happened on the
  last keystroke (e.g. "Helium: the app doesn't report where the caret is").
  The same lines go to the unified log:
  `log show --last 10m --predicate 'subsystem == "org.opentypist.OpenTypist"'`.
  Your text is never logged.
- Per-app disable list (terminals, IDEs and password managers are off by
  default), plus a "Disable in <current app>" toggle in the menu bar
- Fields where a guess does harm are left alone: sign-in and verification
  fields (a label, placeholder or id asking for an email, username, phone,
  PIN, code or card number, or an email address being typed) get no
  suggestions, autocorrect or learning, and Mail's To, Cc and Subject fields
  stay Mail's, since Tab moves between them there
- Local stats: words accepted, keystrokes saved, accept rate
- Streaming: a suggestion appears as soon as its first word is complete and
  grows word by word. On an M5 at "Balanced" the first word is ready in about
  30 ms (44 ms for the slowest 10%), against 58 ms / 143 ms for the whole
  suggestion; Tab accepts what's visible even while the rest is coming
- A quick extra Tab never reaches the app: pressed in the moment between
  accepting and the next suggestion, it accepts more of the same suggestion or
  does nothing, instead of typing a tab or moving to another field
- Suggestions that loop ("Hi S. Hi S. Hi S."), repeat the words just typed,
  or look like chat-log or web-page leftovers are dropped
- Confidence filter: a suggestion ends at the first word piece the model is
  unsure about (and a half-finished word goes with it), and nothing is shown
  if it's unsure from the start. Measured on sample messages with Gemma 4
  E2B, "Balanced" shows a suggestion 74% of the time with 50% of suggested
  words right, against 91% / 26% with no filter; "Fewer, more accurate" shows
  ~55% of the time with ~73% right
- Mid-word completion ("I'm very in" → "terested in"): the half-typed word
  leaves the prompt and the model's output has to start with it, so the model
  spells the whole word with its own tokens ("appre" → "ciate", never
  "appreaciate"), and a completion that doesn't make a real word is dropped.
  It has its own looser confidence bar, so it shows even when the words after
  it don't: with Gemma 4 E2B, 98–99% of half-typed words get a completion at
  every level, and 87–88% of those are the right word
- Works alongside macOS's own inline predictions (the gray text after the
  caret in TextEdit, Notes, Mail and Messages): OpenTypist continues after
  macOS's word, leaves Tab to macOS when it has nothing to add, and uses the
  card below the line when the two disagree
- Suggestions in the middle of a line, shown in a small card below the line
  so they don't cover the text after the caret, and trimmed to lead into that
  text instead of repeating it ("I'm very in| this job" → "terested in")
- Stats: words accepted per day for the last 30 days, this week's total and
  your best day
- KV-cache reuse between keystrokes; around 150–200 ms per keystroke on an M5

## Install

Download the DMG from the [latest release](https://github.com/mekanismvibe/opentypist/releases/latest),
open it and drag OpenTypist into Applications. Requires Apple silicon and
macOS 14 or later.

Release builds aren't signed with an Apple Developer ID yet, so the first time
you open the app macOS refuses: go to System Settings → Privacy & Security,
scroll down and click **Open Anyway** (or run
`xattr -dr com.apple.quarantine /Applications/OpenTypist.app`).

After that OpenTypist updates itself. Once a day it checks the project's
GitHub releases (turn this off in Settings → General → Updates, or use **Check
for Updates…** in the menu). Installing an update downloads the DMG, verifies
it against the published SHA-256, checks the app's bundle ID, version and code
signature, swaps it in and relaunches.

## Requirements for building

- A Mac with Apple silicon
- Swift 5.9+ (the Xcode Command Line Tools are enough)
- CMake (`brew install cmake`); Ninja is used if installed

## Build

```bash
git clone --recursive https://github.com/mekanismvibe/opentypist
cd opentypist
./scripts/build-app.sh          # → build/OpenTypist.app
./scripts/make-dmg.sh           # → build/OpenTypist-<version>.dmg (+ .sha256)
```

llama.cpp is a git submodule (`vendor/llama.cpp`) pinned to a release.
`scripts/build-llama.sh` builds it once into static libraries with the Metal
shaders embedded, for macOS 14 and later, and the app links them in: the
result is a single 12 MB app with no dependencies beyond macOS. Copy it to
`/Applications` and launch it.

### Releases

Pushing a tag like `v0.3.0` runs `.github/workflows/release.yml`: it builds
the app at that version, runs the offline self-test, makes the DMG and
publishes a GitHub release with the DMG and its checksum, which the in-app
updater then offers to everyone. Every push and pull request is built and
tested by `.github/workflows/ci.yml`. Adding Developer ID secrets to the
repository (listed at the top of `release.yml`) turns on signing and
notarization.

On first launch a setup guide walks through the permissions and lets you pick
and download a model. It can be reopened from the menu bar (Setup Guide…).

Without `CODESIGN_IDENTITY`, the app is ad-hoc signed with a designated
requirement of just its bundle identifier (`org.opentypist.OpenTypist`), so
permission grants survive rebuilds. The trade-off: macOS would extend those
grants to any ad-hoc-signed app using the same identifier. For anything you
distribute, set `CODESIGN_IDENTITY` to a real certificate.

### Testing the engine without the UI

```bash
swift build -c release
.build/release/OpenTypist --selftest --model /path/to/model.gguf
```

This prints suggestions for sample text and per-keystroke latency.
`OPENTYPIST_DEBUG=1` turns on llama.cpp logging. Other developer tools:

- `OpenTypist --selftest-offline` runs every check that doesn't need a model
  (this is what CI runs).
- `OpenTypist --calibrate` replays sample messages and measures, per
  confidence threshold, how often a suggestion appears and how often it's
  right. This is how the confidence levels were chosen.

- `OpenTypist --complete a.txt b.txt` prints the model's greedy continuation of
  each raw prompt file (for tuning prompt formats).
- `OPENTYPIST_TEST_STORE=1 OpenTypist --snapshot-personal out.png` renders
  Settings → Personal with sample data into an image. `OPENTYPIST_TEST_STORE`
  points personal storage at a throwaway folder with an in-memory key, so the
  real Keychain and data are never touched.

## How it works

| Piece | File |
| --- | --- |
| Reads the focused field, caret position and window title (AXUIElement) | `FocusReader.swift` |
| Global event tap: Tab / Shift-Tab / Esc while a suggestion is visible | `KeyboardMonitor.swift` |
| Types accepted text with synthetic Unicode key events | `KeyboardMonitor.swift` (`TextInserter`) |
| Prompt building, output cleanup, word splitting | `Suggestion.swift` |
| llama.cpp wrapper: greedy decoding, KV-cache prefix reuse, token healing | `LlamaEngine.swift` |
| Background queue with cancellation of stale requests | `EngineService.swift` |
| Keystroke → refresh → suggestion → overlay state machine | `CompletionController.swift` |
| Click-through ghost-text panel | `OverlayWindow.swift` |
| Model download and checksum | `ModelManager.swift` |
| Menu bar and settings window | `AppDelegate.swift`, `SettingsView.swift` |

Suggestions only appear at the end of a line, with no text selected, and never
in secure text fields. Chromium/Electron apps get `AXManualAccessibility` set
so their text fields are visible to Accessibility.

## Known limitations

- Ghost text uses the field's font when the app reports it, otherwise the
  system font sized from the caret height.
- Wrapped continuation lines are drawn below the caret's line even when the
  field hasn't grown to make room yet, so they can overlap what's below it.
- Some apps (certain games, remote desktops, custom-drawn editors) don't expose
  their text through Accessibility; OpenTypist stays quiet there.
- No personalization or learning from your writing yet.

## Windows

A Windows version is planned. [docs/windows-port.md](docs/windows-port.md) is
the porting brief: what to build with, every behavior and measured threshold
to keep, the Windows-specific traps, and how it plugs into CI and releases.

## License

MIT, see [LICENSE](LICENSE). The Gemma model weights are distributed separately
under their own license (Apache 2.0).
