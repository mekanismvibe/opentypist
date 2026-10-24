# OpenTypist

Open source, on-device autocomplete for macOS. As you type in almost any app
(Mail, Messages, Slack, Notes, browsers…), OpenTypist predicts the next few
words with a local language model and shows them right after the caret as
ghost text: the field's own font in a faded version of its text color.

- **Tab** accepts the next word
- **Shift-Tab** accepts the whole suggestion
- **Esc** dismisses it (or just keep typing)
- **Option-Tab** always sends a normal Tab

Everything runs locally with [llama.cpp](https://github.com/ggml-org/llama.cpp)
on the GPU (Metal). Nothing you type leaves your Mac, password fields are never
read, and the only data stored is a count of accepted words.

## Models

Nothing is downloaded until you pick a model in the setup guide or in
Settings → Model. OpenTypist reads your chip and memory and marks the model
that fits your Mac best. All of them are base (non-chat) models, 4-bit
quantized GGUFs from Hugging Face, pinned to a revision and checked against
their SHA-256:

| Model | Size | Recommended for |
| --- | --- | --- |
| Qwen 3 1.7B | 1.1 GB | 8 GB Macs, longest battery life |
| Gemma 4 E2B | 3.4 GB | 16 GB Macs (and base-chip Macs up to 24 GB); the default |
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
- Local stats: words accepted, keystrokes saved, accept rate
- Mid-word completion via token healing ("I'll be th" → "ere in about")
- KV-cache reuse between keystrokes; around 150–200 ms per keystroke on an M5

## Requirements

- macOS on Apple silicon. The binary targets macOS 14, but the bundled
  Homebrew libraries are built for the macOS version that built the app.
- Swift 5.9+ (the Xcode Command Line Tools are enough)
- `brew install llama.cpp`

## Build

```bash
./scripts/build-app.sh
```

This produces `build/OpenTypist.app` with llama.cpp, ggml, its Metal/CPU
backends and libomp copied into `Contents/Frameworks`, so the app doesn't need
Homebrew at runtime. Copy it to `/Applications` and launch it.

On first launch a setup guide walks through the permissions and lets you pick
and download a model. It can be reopened from the menu bar (Setup Guide…).

Without `CODESIGN_IDENTITY`, the app is ad-hoc signed with a designated
requirement of just its bundle identifier (`org.opentypist.OpenTypist`), so
permission grants survive rebuilds. The trade-off: macOS would extend those
grants to any ad-hoc-signed app using the same identifier. For anything you
distribute, set `CODESIGN_IDENTITY` to a real certificate.

If macOS's own inline predictions also show up, turn them off in System
Settings → Keyboard → Text Input → Edit → "Show inline predictive text".

### Testing the engine without the UI

```bash
swift build -c release
.build/release/OpenTypist --selftest --model /path/to/model.gguf
```

This prints suggestions for sample text and per-keystroke latency.
`OPENTYPIST_DEBUG=1` turns on llama.cpp logging.

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

## License

MIT, see [LICENSE](LICENSE). The Gemma model weights are distributed separately
under their own license (Apache 2.0).
