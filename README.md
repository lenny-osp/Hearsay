# Hearsay

Hearsay records your meetings on your Mac and turns them into transcripts.
It captures your microphone and, if you like, the sound of the call itself
(Zoom, Teams, Meet), transcribes on the Mac with an on-device Whisper model,
and saves a subtitle file (SRT) plus the recording. For meeting notes it can
send the transcript to an AI provider you pick. Nothing leaves the Mac
unless you confirm that step.

## Requirements

- A Mac with Apple Silicon (M1 or later).
- macOS 14 Sonoma or later.
- To build: Xcode 26.6 or later with the Metal Toolchain component, and XcodeGen (CI builds with Xcode 26.6; the owner uses 27).
- Disk space for one speech model (74 MB to 3.08 GB).

## Install

Releases are published on this repository's GitHub Releases page, built by
GitHub Actions from a version tag (`.github/workflows/release.yml`):

1. Download `Hearsay-<version>.dmg` (and `SHA256SUMS.txt` if you want to
   check it with `shasum -a 256 -c SHA256SUMS.txt`).
2. Open the DMG and drag Hearsay to Applications.
3. The first launch: releases are not notarized yet (there is no Developer
   ID signing), so macOS blocks the app once. On macOS 14, right-click
   Hearsay in Applications and choose Open. On macOS 15 and later, open it
   once, then click Open Anyway in System Settings > Privacy & Security.

Or build from source:

1. Install Xcode 26.6 or later, launch it once to accept the license, and select it:
   `sudo xcode-select -s /Applications/Xcode.app`.
2. Install the Metal Toolchain (about 840 MB):
   `xcodebuild -downloadComponent MetalToolchain`.
3. Install XcodeGen: `brew install xcodegen`.
4. From the repository folder, run one of:
   - `mac/Scripts/run-debug.sh` builds Debug and opens
     `mac/.build/derived/Build/Products/Debug/Hearsay.app`.
   - `mac/Scripts/run-debug.sh --release` builds Release and opens
     `mac/.build/derived/Build/Products/Release/Hearsay.app`. Transcription is
     much faster; use this one day to day.

   Add `--no-open` to build without launching.

Hearsay is not sandboxed, because it runs the AI command-line tools you
have installed (they read their own logins from your home folder). It will
be distributed with Developer ID (signed and notarized), not through the Mac
App Store. The hardened runtime stays on, and macOS still asks before
Hearsay uses the microphone or system audio.

## First run

1. **Output folder.** Files go to `~/Documents/Hearsay`, created on first
   use. Settings > Output shows the folder; "Choose…" picks another one.
2. **Download a model** in the Models tab (Download, Cancel, Delete, Use).
   With no model installed, Hearsay offers the recommended one.

   | Model | Download | Note |
   |---|---|---|
   | whisper-tiny-fp16 | 74 MB | smoke test, low accuracy |
   | whisper-base-fp16 | 143 MB | |
   | whisper-small-fp16 | 481 MB | |
   | whisper-small-4bit | 139 MB | |
   | whisper-medium-fp16 | 1.52 GB | |
   | whisper-large-v3-turbo | 1.61 GB | recommended |
   | whisper-large-v3-turbo-8bit | 863 MB | |
   | whisper-large-v3-turbo-4bit | 463 MB | smallest good option |
   | whisper-large-v3-fp16 | 3.08 GB | same weights as whisper-tools |
   | whisper-large-v3-8bit | 1.64 GB | |
   | whisper-large-v3-4bit | 877 MB | |

   Models come from Hugging Face (`mlx-community`) and are stored in
   `~/Library/Application Support/Hearsay/Models/`. An interrupted download
   resumes on the next launch.
3. **Permissions.** On the first Start, macOS asks for Microphone access
   and, with "Also capture system audio" on, for Screen & System Audio
   Recording (only the audio is used). Without the second, Hearsay records
   the microphone only and says so.

## Main window

One window with five tabs: Record, File, Models, History, and Settings.
Settings is a tab, not a separate window; ⌘, opens it.

## Recording

- Pick the microphone and the language, leave "Also capture system audio"
  on to include the other side of a call, and press Start. Meters show
  whether the microphone and system audio are alive; a warning appears
  after 5 seconds of silence.
- If nothing arrives from the microphone within 3 seconds, recording stops
  with "No audio from <device>".
- AirPods and other Bluetooth headsets switch to a low-quality call mode
  while recording. In a noisy room, use the Mac's built-in microphone.
- Pause leaves that time out of the transcript.
- The Permissions row at the top of the Record tab shows both permissions.
  A build that is not signed with a Developer ID loses the system audio
  permission on every update; Hearsay then removes the old entry and shows
  "Fix…": turn Hearsay on again in System Settings > Privacy & Security >
  Screen & System Audio Recording.
- **Live preview:** new text about every 30 seconds while you record.
- **Final pass:** after Stop, Hearsay transcribes the whole recording once
  more for the SRT. "Use live preview instead" skips it and saves the
  preview.
- If the final pass fails, the preview SRT (if any) and the WAV are kept.

## Languages

- The picker offers Auto, EN, ZH-TW, ZH-CN, DE, and ES. ZH-TW writes
  Traditional characters, ZH-CN Simplified.
- Auto listens to up to the first 90 seconds of speech. If it can't tell,
  it uses Settings > General > Auto mode default language and says so, with buttons
  to transcribe again in another language.
- If you picked a language and the meeting sounds like another one, a
  banner says "This sounds like …" with "Transcribe again".

## File mode

Drop an audio or video file on the File tab, or click "Choose…". Hearsay
reads wav, m4a, mp3, aac, aiff, caf, and the audio of mp4 and mov. The SRT
is named after the timestamp in the file name, otherwise the file's date.

## Meeting notes

1. **Provider**, in Settings > AI:
   - **GitHub Copilot CLI**, **Claude Code CLI**, **Codex CLI**, and
     **Antigravity CLI** run the program installed on your Mac with your
     own subscription login. No API key; requests count against that
     subscription's limits. Each "Check" button shows whether the program
     is found and logged in. Every run uses an empty temporary folder.
     - Copilot: the same arguments as whisper-tools. Default
       `gpt-5.6-luna`, effort `max`.
     - Claude Code: no tools, none of your settings, hooks, CLAUDE.md, or
       MCP servers, no saved session. Default `claude-sonnet-5`, effort
       `high`.
     - Codex: read-only sandbox, without your `config.toml`, no saved
       session. Default `gpt-6-luna`, effort `max`.
     - Antigravity: print mode in a Hearsay project whose rules deny
       shell, file, fetch, and MCP tools; Hearsay deletes the run from agy's
       history afterwards (fully only while no other agy session is open).
       Default `gemini-3.8-flash-high`.
   - **Ollama / LM Studio** runs locally, no key.
   - **Custom** is any OpenAI-compatible `/chat/completions` endpoint: URL,
     model, and token (stored in the Keychain; Bearer or `api-key` header).

   "Test connection" sends a one-line prompt.
2. **Confirm.** After a transcript is saved, Hearsay asks "Send transcript
   for meeting notes?" with the provider, model, template, and notes
   language (English, 繁體中文, 简体中文, Deutsch, Español; defaults to the
   transcript language). "Keep local" sends nothing. Turn the question off
   with "Ask before sending a transcript".
3. **Prompt templates.** The built-in "General meeting" template is the
   whisper-tools prompt. Add your own in Settings > AI; the file-name and
   JSON rules are always added.
4. **Name.** Edit the suggested name and press Save.

Output files, in the output folder:

| File | Content |
|---|---|
| `<timestamp>_<name>.srt` | transcript with timings |
| `<timestamp>_<name>.md` | meeting notes |
| `<timestamp>_<name>_transcript.md` | structured transcript |
| `<timestamp>_<name>.wav` | recording (16 kHz mono) |

The timestamp looks like `2026-09-28_14-05-00`. To delete the WAV after a
successful transcription, use Settings > Output.

## History

Lists the meetings in the output folder. Open SRT, Open Notes, Open
Transcript, Reveal in Finder, and:

- **Generate Notes…** for a saved transcript, or **Regenerate Notes…**; the
  old notes go to the Trash when the new ones are saved.
- **Move to Trash…** moves all of a meeting's files to the Trash.

## Menu bar, window, and startup

- The menu bar item shows the state and elapsed time, starts, pauses, and
  stops recording, and turns red while recording (Settings > Window > Show
  recording status in the menu bar turns that off). Recording continues
  with the window closed.
- ⌃⌥⌘R starts or stops, ⌃⌥⌘P pauses or resumes, in any app. Change them in
  Settings > General.
- Settings > Window: "Menu bar and Dock", "Menu bar only", or "Dock only".
- Settings > General: "Launch Hearsay at login".
- **Crash recovery:** the recording is written to disk as it goes. After a
  crash, the next launch offers Transcribe, Keep, or Delete.

## Updates

Hearsay asks GitHub once a day whether a newer release exists and, if so,
offers to install it (download, checksum and signature check, relaunch) or
to open its release page. Nothing is installed without your click and
nothing else is sent. Turn it off, or check now, in Settings > General >
Software updates; the app menu also has **Check for Updates…**. The version
is shown there and in About Hearsay.

## Interface language and help

- Settings > General > Interface language: English (default), Deutsch,
  Español, 繁體中文, 简体中文. Hearsay restarts to apply it.
- Help > Hearsay Help (⌘?) opens the help in the interface language.

## Relation to whisper-tools

Hearsay is the native macOS version of the `whisper-tools` Python CLI
(`run_whisper.py`):

- Same output names, so files from both tools sit side by side.
- Same meeting-notes prompt; the English and Traditional Chinese prompts
  are identical to the Python tool's.
- The GitHub Copilot CLI provider works as in the Python tool.
- Chinese is transcribed without an initial prompt: the Python tool's
  English prompt made turbo echo it and large-v3 write Simplified
  characters.
- Hearsay adds German, Spanish, and Simplified Chinese.

## Development

- `PLAN.md` is the design record.
- `HearsayCore` tests: `cd mac/HearsayCore && swift test`.
- `HearsayWhisper` tests (MLX needs `xcodebuild`, not `swift build`):
  `cd mac/HearsayWhisper && xcodebuild -scheme HearsayWhisper -destination 'platform=macOS,arch=arm64' -skipPackagePluginValidation -skipMacroValidation test`.
  Set `TEST_RUNNER_HEARSAY_MODEL_DIR=/abs/path/to/model` to run the
  integration tests.
- Translations: after a build, `mac/Scripts/export-strings.py` writes
  `shared/localization/strings-en.json`; translators add `shared/localization/<lang>.json`
  (see `shared/localization/GLOSSARY.md`); `mac/Scripts/merge-translations.py` merges
  them into the string catalogs.
- Help pages: edit `shared/help/<lang>/Help.html`; `mac/Scripts/sync-shared.sh`
  (run by `run-debug.sh` and `generate-project.sh`) copies them into the app.
- `shared/` also holds what the planned Windows version reuses: the
  meeting-notes prompt (`shared/prompts/`), naming and language-decision test
  vectors (`shared/*-tests.json`, run by the `HearsayCore` tests), the icon
  source (`shared/assets/`), and the test audio (`shared/fixtures/`).
- `mac/Scripts/make-notices.sh` regenerates `mac/THIRD_PARTY_NOTICES.md`.
- CI (`.github/workflows/ci.yml`) builds and tests every pull request; a
  pushed `v*` tag publishes a release (`release.yml`, which uses
  `mac/Scripts/make-dmg.sh`). See `.github/workflows/README.md`.

## License

Hearsay is released under the MIT License, Copyright (c) 2026 Chihling Wang;
see [LICENSE](LICENSE). It bundles open-source libraries and includes code
copied or ported from other MIT-licensed projects; their licenses are in
[mac/THIRD_PARTY_NOTICES.md](mac/THIRD_PARTY_NOTICES.md) (regenerate it with
`mac/Scripts/make-notices.sh`) and in the app under Settings > General >
Acknowledgements.

## Support

If Hearsay is useful to you, you can support it on
[Buy Me a Coffee](https://buymeacoffee.com/chihlingw).
