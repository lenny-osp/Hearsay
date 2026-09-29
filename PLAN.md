# Hearsay: native macOS port of whisper-tools

Status: Phase 0 spike done 2026-09-28, verdict GO. See section 15 for
results. `mac/Spike/` holds the throwaway spike; the app itself is not built yet.

Hearsay is a SwiftUI menu-bar/dock app that reproduces the macOS flow of
`whisper-tools/run_whisper.py` without Python, FFmpeg, or a shell.
The reference CLI lives in the sibling repo
`/Users/chihling/repositories/personal/whisper-tools`:

```text
mic or audio file -> Whisper (MLX, on-device) -> SRT + retained WAV
                  -> optional AI meeting notes -> named SRT + 2 Markdown files
```

The Python CLI stays in that repo as the behavioral reference. Its unit
tests in `whisper-tools/tests/test_run_whisper.py` are the spec for every naming and
output rule ported here.

---

## 1. Decisions already made

| Topic | Decision |
|---|---|
| Language / UI | Swift 6, SwiftUI, AppKit only where SwiftUI has no API (activation policy, CoreAudio device pick). |
| Inference | MLX. The Whisper module of `Blaizzy/mlx-audio-swift` (MIT, 1,526 lines, commit `01dec7c9`) is vendored into `mac/HearsayCore/Whisper/` and its decode loop is replaced with a timestamped decoder ported from `mlx_whisper` 0.4.3. Reason: section 15. Direct dependencies become `ml-explore/mlx-swift` and `huggingface/swift-transformers` only. |
| Models | Downloaded on demand from Hugging Face `mlx-community/whisper-*` repos into the app's own model directory. User picks the model. Nothing ships inside the bundle. |
| Audio I/O | AVFoundation. Mic via `AVCaptureSession` + `AVCaptureAudioDataOutput` (16 kHz mono Float32), chosen by CoreAudio UID; `AVAudioFile` for files. No FFmpeg. Changed 2026-09-28: the `AVAudioEngine` tap got no buffers after switching input device, because the input node kept reporting the previous device's rate. |
| System audio | Captured with ScreenCaptureKit (`SCStream`, audio only) and mixed with the mic, so Zoom/Teams/Meet calls are transcribed, not just the room. Decided 2026-09-28. |
| Live transcript | Yes in v1. 30 s chunks are transcribed while recording and shown as a live preview; the final SRT comes from one full pass after Stop. Decided 2026-09-28. |
| Output folder | Default `~/Documents/Hearsay`, user-configurable in Settings. Decided 2026-09-28. |
| Languages | Added 2026-09-28 (owner request): Auto, EN, ZH-TW, ZH-CN, DE, ES (the two Chinese choices replaced ZH plus a separate "Chinese output" setting, owner request 2026-09-28: both call Whisper with "zh", ZH-TW transcripts are converted to Traditional characters and ZH-CN to Simplified; when Auto detects Chinese it uses the Auto mode default language's Chinese variant, else ZH-TW; the setting was labeled "Preferred language" until 2026-09-29 (owner request; `preferredLanguage` in code); stored "zh" settings migrate by the old Chinese output setting). Auto is the picker default for new users. Detection only compares the four supported languages (probabilities renormalized over them), skips silent windows (below about -60 dBFS, or no-speech probability above 0.6; the turbo model's no-speech probability alone never flags silence, measured 2026-09-28), averages the restricted probabilities of up to three speech windows, and locks the language once per session. Below the confidence threshold it uses the **preferred language** from Settings > General (default English, owner choice; never changed automatically by what the user picks elsewhere) and says so, with one-click re-run in another language. When the user picked a language and detection is confident it is a different one, a banner offers to re-run in the detected language; nothing switches automatically. Meeting notes default to the transcript language; the confirm sheet can pick another of the four for that run only (added 2026-09-28, owner request). History SRTs with no stored language default to the language NaturalLanguage detects in the text (probability at least 0.6), else the language choice or preferred language. Mixed-language (code-switching) meetings are out of scope. |
| v1 extras | Global hotkey, pause/resume, an update check against GitHub Releases (section 4.6; replaced Sparkle 2026-09-28), crash recovery of an unfinished recording, custom prompt templates, and a setting that decides whether the WAV is kept at all. Decided 2026-09-28. |
| AI notes | Direct HTTPS to any OpenAI-compatible `/chat/completions` endpoint. Same JSON contract as the Python tool. Providers (2026-09-28, after the first live runs): GitHub Copilot CLI, Claude Code CLI, and Codex CLI use the owner's subscription logins (no API keys, no temperature); Antigravity CLI is a fourth CLI (Azure OpenAI removed 2026-09-28); Ollama and Custom are HTTP. GitHub Models was shut down on 2026-07-30, and the OpenAI and Anthropic API presets were replaced by the two CLIs. |
| Window mode | User setting: "Menu bar and Dock", "Menu bar only", "Dock only". Switched at runtime with `NSApp.setActivationPolicy`. |
| Platform floor | macOS 14 Sonoma, Apple Silicon only (MLX requirement). Intel is out of scope. |
| Sandbox | **Off** since 2026-09-28 (owner decision after the first live run). Needed so the app can run the GitHub Copilot CLI as a child process, exactly like the Python tool. Hardened runtime stays on; Developer ID + notarization is the distribution path. Mac App Store is out. |
| Distribution v1 | Developer ID signed + notarized DMG. |

## 2. Prerequisites on the dev machine

This Mac (macOS 27.0, Apple Silicon, Swift 6.4) currently has **Command Line
Tools only**. That is not enough:

1. **Install full Xcode** (26.x) from the App Store or developer.apple.com.
   `mlx-swift` compiles Metal shaders during the build. `swift build` on the
   command line cannot do that; the app must be built with Xcode or
   `xcodebuild`.
2. After install: `sudo xcode-select -s /Applications/Xcode.app` and accept
   the license once by launching Xcode. Done 2026-09-28: Xcode 27.0 (27A266a).
2b. The Metal compiler is a separate 839 MB component:
   `xcodebuild -downloadComponent MetalToolchain`. Done 2026-09-28.
2c. Every `xcodebuild` invocation needs `-skipPackagePluginValidation
   -skipMacroValidation`, because `mlx-swift` ships a build plugin that
   Xcode otherwise refuses to run from the command line.
3. Apple Developer account for signing and notarization (a free account
   builds and runs locally; Developer ID needs the paid one).
4. A short test WAV (16 kHz mono, 30 to 60 s, English and one Chinese) in
   `shared/fixtures/` for integration tests. Record it yourself; do not
   commit real meetings.

Existing local cache that helps the spike: `~/.cache/huggingface/hub/`
already holds `mlx-community--whisper-large-v3-mlx` (weights.npz, 3.08 GB).
See the npz caveat in section 5.

## 3. Repository layout

```text
Hearsay/                      this repo
  PLAN.md                     this file
  .claude/agents/             subagent definitions used to build the app
  README.md                   user-facing: install, first run, outputs (write in Phase 6)
  mac/                        macOS app
    Hearsay.xcodeproj/          Xcode project (app target + test targets)
    Hearsay/                    app target
      HearsayApp.swift          @main, MenuBarExtra + WindowGroup + Settings scene
      AppDelegate.swift         activation policy, dock/menu-bar switching, quit handling
      Features/
        Recording/              RecordView, RecordViewModel, level meter view
        FileTranscription/      FileView (drop target, file picker), FileViewModel
        Models/                 ModelManagerView, ModelRow, download progress UI
        Notes/                  NotesView, NamingSheet, ConfirmSendSheet
        History/                HistoryView (past recordings, open in Finder)
        Recovery/               UnfinishedRecordingSheet
        Hotkeys/                HotkeyManager (global shortcuts)
        Updates/                UpdateService (GitHub release check, section 4.6)
        Settings/               General, Window mode, Output, AI provider, Models tabs
      Resources/
        Assets.xcassets         app icon, menu bar template icons (idle, recording)
        ModelCatalog.json       built-in list of downloadable models (see section 5)
      Hearsay.entitlements
      Info.plist
    HearsayCore/                Swift package, pure logic, no UI, fully unit-tested
      Sources/HearsayCore/
        Audio/
          AudioDeviceList.swift        CoreAudio enumeration of input devices
          MicrophoneRecorder.swift     AVCaptureSession -> 16 kHz mono PCM stream
          SystemAudioRecorder.swift    ScreenCaptureKit SCStream audio -> 16 kHz mono PCM stream
          AudioMixer.swift             aligns and sums mic + system streams into one mono stream
          LevelMeter.swift             port of StreamingLevelMeter (RMS dB, silence warning)
          WavWriter.swift              spooled WAV, same format as the Python tool
          RecordingSpool.swift         spool folder, move-or-delete after success, crash scan
          AudioFileLoader.swift        any AVFoundation-readable file -> 16 kHz mono Float32
        Transcription/
          WhisperEngine.swift          wraps MLXAudioSTT.WhisperModel
          LiveTranscriber.swift        chunk scheduler for the live preview (section 4.1)
          TranscriptionOptions.swift   language, initial prompt, thresholds
          Segment.swift                start, end, text
          SRTWriter.swift              segments -> SRT text
        ModelStore/
          ModelCatalog.swift           decode ModelCatalog.json
          ModelDownloader.swift        URLSession background download, resume, cancel
          ModelStore.swift             installed models, sizes, delete, active model
        Notes/
          MeetingPrompt.swift          port of build_meeting_prompt + template slot
          PromptTemplate.swift         user templates, built-in "General meeting"
          ChatCompletionsClient.swift  OpenAI-compatible POST, error mapping
          NotesResponse.swift          parse + validate {filename, markdown, transcript_markdown}
          ProviderPresets.swift        Copilot/Claude Code/Codex/Antigravity CLIs, Ollama, custom
        Naming/
          FilenameSanitizer.swift      port of sanitize_ai_filename
          MeetingNameInserter.swift    port of insert_meeting_name
          OutputWriter.swift           port of save_named_outputs + rename_transcription_outputs
          Timestamps.swift             yyyy-MM-dd_HH-mm-ss, parse from filename, file birth time
        Settings/
          AppSettings.swift            UserDefaults-backed, observable
          SecretStore.swift            Keychain for API tokens
          OutputLocation.swift         security-scoped bookmark for the output folder
      Tests/HearsayCoreTests/
        (one test file per source file above; ports of the Python tests)
    Scripts/
      build-release.sh           xcodebuild archive, sign, notarize, staple, DMG
  shared/                     resources both platforms use (section 18.2)
    fixtures/                  short test audio and the Python reference SRTs
    localization/              translator files, GLOSSARY.md
    help/<lang>/Help.html      in-app help; copied into the mac .lproj folders by Scripts/sync-shared.sh
    prompts/                   meeting-notes prompt text, languages.json, assembly.md (JSON contract)
    assets/icon-1024.png       app icon source, written by Scripts/make-icon.swift
    models/README.md           model catalog schema; each platform keeps its own list
    scripts/make-naming-tests.py  regenerates naming-tests.json from the Python tool
    naming-tests.json          naming-rule test vectors
    language-decision-tests.json  language-decision test vectors
  windows/                    Windows version (not started)
```

`HearsayCore` is a local Swift package so its tests run without Metal and
without Xcode's simulator machinery. The MLX-dependent `WhisperEngine` lives
there too but its integration test is gated behind an environment variable
and a downloaded model.

## 4. Runtime architecture

```text
+-------------------+      +-------------------+      +--------------------+
| MenuBarExtra      |      | Main window       |      | Settings window    |
| icon, elapsed,    |<---->| Record | File |   |<---->| General, Window,   |
| Start/Stop, Open  |      | Models | History  |      | Output, AI, Models |
+---------+---------+      +---------+---------+      +---------+----------+
          |                          |                          |
          v                          v                          v
+---------------------------------------------------------------------------+
| AppState (@Observable, main actor)                                         |
|  recordingSession?  transcriptionJob?  notesJob?  modelStore  settings     |
+------+-----------------+------------------+------------------+------------+
       |                 |                  |                  |
       v                 v                  v                  v
 MicrophoneRecorder  WhisperEngine    ChatCompletionsClient  ModelStore
 (AVAudioEngine)     (MLXAudioSTT)    (URLSession)           (URLSession dl)
       |                 |                  |
       v                 v                  v
   WavWriter         SRTWriter         OutputWriter
```

Concurrency: every engine is an `actor` or runs on a dedicated `Task`.
`WhisperEngine` holds the loaded model and serializes `transcribe` calls.
Model loading happens once per selected model and is cached until the user
switches models or memory pressure triggers unload.

### 4.1 Recording flow (Streaming mode, plus system audio and live preview)

1. User picks the input microphone (CoreAudio list, friendly names, default
   preselected), toggles "Also capture system audio" (default on), and picks
   the language (EN / ZH / Auto, default EN).
2. Start:
   - Mic: `AVAudioEngine` input node tap. Device set with
     `kAudioOutputUnitProperty_CurrentDevice` on the input audio unit.
   - System audio: `SCStream` with `capturesAudio = true`. There is no
     audio-only mode in any SDK up to macOS 27 (checked 2026-09-28), so the
     stream also carries a 2x2 video frame at 1 fps that is discarded.
     `excludesCurrentProcessAudio = true`. Requires the Screen & System Audio
     Recording permission; the first Start explains why and opens System
     Settings if denied. If denied, recording continues mic-only with a
     visible badge.
3. `AVAudioConverter` resamples each source to 16 kHz mono Float32.
   `AudioMixer` aligns the two streams on host time and sums them with soft
   clipping. Output: one 16 kHz mono stream.
4. The mixed stream goes to four consumers:
   - `WavWriter` appends s16le PCM to a spool file
     `~/Library/Application Support/Hearsay/Recording/<timestamp>.wav`
     (same format the Python tool keeps). It is always written, whatever the
     "Keep recording" setting says, so a crash never loses audio. On a
     successful transcription it is moved to `<outputDir>/<timestamp>.wav`
     when "Keep recording" is on, or deleted when it is off. On failure it
     is always kept and its path shown.
   - `LevelMeter` computes RMS dB every 0.2 s, drives the level bar, and
     raises the silence warning after 5 s below -55 dB. Constants copied
     from `run_whisper.py` lines 55-66. Two small per-source meters show
     whether mic and system audio are each alive.
   - `LiveTranscriber` cuts a chunk every 30 s, or earlier at a silence
     of 2 s or more once the chunk holds at least 10 s, and hands it to
     `WhisperEngine`. Segments come back with the chunk's start offset
     added and are appended to the live preview in the window and to the
     menu bar's "latest line". Chunks are queued, never dropped; if the
     engine falls behind, the preview lags but stays complete.
   - An in-memory sample accumulator for the final pass.
   Pause stops all four consumers and the capture taps; Resume restarts
   them. Paused time is excluded from timestamps, so the SRT has no gap.
5. Stop: close the WAV. Run one full-pass transcription over the whole
   recording; this is the final SRT because it does not suffer from chunk
   boundaries. The UI shows "Finalizing…" with progress. A "Use live
   preview instead" button skips the pass and writes the preview segments
   as the SRT.
6. Write `<outputDir>/<timestamp>.srt`. Show the transcript.
7. Continue to the notes flow (4.3).

Failure handling matches the Python rules: if the final pass fails, the
live preview SRT is kept if it exists, the WAV path is shown, and a
"Transcribe this file" button jumps to File mode.

### 4.2 File flow

1. Drop a file or choose one. Any format AVFoundation reads: wav, m4a, mp3,
   aac, aiff, caf, mp4/mov audio track.
2. `AudioFileLoader` decodes to 16 kHz mono Float32.
3. Transcribe, write SRT next to the chosen output directory using the
   Python rule for the base timestamp: timestamp embedded in the filename,
   else file birth time, else modification time (`run_whisper.py` lines
   1017-1036).
4. Continue to the notes flow.

### 4.3 Notes flow (shared, same as Python)

1. **Confirm send** sheet, unless settings say "never ask" (parity with
   `AI_CONFIRM`). Shows provider, model, transcript size. Decline keeps the
   SRT and offers manual naming (parity with `name_retained_outputs`).
2. `MeetingPrompt.build(transcript, language, template)` starts from a
   verbatim port of `build_meeting_prompt`. That port is the built-in
   "General meeting" template. Users add their own templates (name plus
   instruction text that replaces the notes section of the prompt) in
   Settings; the JSON-shape and filename rules at the end of the prompt are
   fixed and always appended so `NotesResponse` keeps working. The confirm
   sheet has a template picker, default "General meeting". System message
   text also copied.
3. `ChatCompletionsClient` POSTs `{model, messages, reasoning_effort?}`.
   Token precedence and env names are irrelevant in a GUI; the token lives in
   Keychain per provider preset.
4. `NotesResponse.parse` enforces: JSON object, all three fields non-empty
   strings. Anything else is an error shown with the raw body excerpt.
5. `FilenameSanitizer` normalizes the suggested name. Naming sheet shows it
   as an editable default; empty or unusable input is rejected inline.
6. `MeetingNameInserter` adds `**Meeting Name:** <name>` under the first
   heading in both documents.
7. `OutputWriter.saveNamed` renames the SRT and the same-basename WAV to
   `<timestamp>_<slug>` and writes `<stem>.md` and `<stem>_transcript.md`
   atomically, numeric suffix on collision, rollback on failure.

### 4.4 Menu bar and Dock behavior

- `MenuBarExtra("Hearsay", systemImage:…)` with `.menuBarExtraStyle(.window)`
  showing: state line (Idle / Recording 00:12:34 / Transcribing), level bar
  while recording, Start/Stop button, "Open Hearsay", "Models…",
  "Settings…", "Quit".
- Setting `windowMode`:
  - `.menuBarAndDock`: policy `.regular`, menu bar item visible.
  - `.menuBarOnly`: policy `.accessory`, closing the last window hides the
    app instead of quitting, menu bar item is the only entry point.
  - `.dockOnly`: policy `.regular`, `MenuBarExtra` `isInserted` = false.
- Switching modes takes effect immediately without relaunch. When leaving
  Dock-only mode with no window open, open the main window so the user is
  not stranded.
- Recording continues while the window is closed. Quit while recording asks
  to stop and save first.
- Settings live in the main window's last tab (after History) since
  2026-09-28; there is no separate Settings window. ⌘, opens the main window
  on that tab, also in Menu bar only mode. The menu bar menu has no
  Settings row.
- Menu bar icon changes to a filled red variant with the elapsed time
  while recording, a paused variant while paused, and the percentage while
  transcribing. Since 2026-09-29 (owner request) Settings > Window has
  "Show recording status in the menu bar" (`menuBarShowsStatus`, default
  on); off keeps the plain waveform in every state. The toggle is disabled
  in Dock only mode. The Windows plan (section 18) mirrors it for the tray
  icon.
- Global hotkey (default ⌃⌥⌘R, editable in Settings) toggles
  Start/Stop; ⌃⌥⌘P toggles Pause. Implemented with a Carbon
  `RegisterEventHotKey` wrapper or the `KeyboardShortcuts` package; both
  work in the sandbox without Accessibility permission.

### 4.5 Crash recovery

On launch, `RecordingSpool` scans the spool folder. A WAV whose header
still says length 0, or that has no matching SRT, is an unfinished
recording. Hearsay patches the header from the file size, then shows a
sheet: "A recording from <date> was not finished. Transcribe it now, keep
it, or delete it?" Transcribe runs the normal File flow. The spool folder
is inside the app container, so no permission is needed.

### 4.6 Updates

Changed 2026-09-28: a plain check against GitHub Releases replaces Sparkle.
Reasons: there is no Developer ID or EdDSA signing key yet, so Sparkle could
not verify or install an update anyway, and a check that only opens the
release page needs no third-party framework.

- `HearsayCore/Updates/UpdateChecker` (actor) calls
  `GET https://api.github.com/repos/<slug>/releases/latest` with
  `Accept: application/vnd.github+json`, a 15 s timeout, and no
  authentication, and reads `tag_name`, `html_url`, `published_at`, `body`,
  and `assets` (`name`, `browser_download_url`, `size`; missing = none).
  GitHub never returns drafts or pre-releases as "latest". Errors: offline
  (any `URLError`), HTTP status, no release yet (404), unreadable JSON.
- `isNewer(remote, than: current)`: numeric components compared as numbers,
  a leading `v` and `+build` metadata ignored, missing components are 0, a
  pre-release suffix is older than the same version without one.
- The slug is the Info.plist key `HearsayUpdateRepository`, set in one
  place: `HEARSAY_UPDATE_REPOSITORY` in `mac/project.yml`.
- `UpdateService` (app): with "Automatically check for updates" on
  (default), 10 s after launch and then hourly it checks if the last
  successful check (`lastUpdateCheck`) is 24 h old or more; errors are
  silent and an alert appears only for a newer version. The app menu's
  "Check for Updates…" and Settings > General > Software updates > Check
  Now always show a result alert. A newer version offers "Install Update"
  (default), "View on GitHub", and "Later"; when the release lacks the DMG
  or `SHA256SUMS.txt`, or this copy cannot replace itself, the alert says
  why and offers "Download" (release page) and "Later". Only the requests
  themselves are sent.
- Install (added 2026-09-29, owner request), `UpdateInstaller` (app) with
  the file steps in `HearsayCore/Updates/UpdateInstall` and
  `UpdatePackage` (app):
  1. Location check on the running bundle: not translocated
     (`/AppTranslocation/`), not on a disk image (`/Volumes/`), a `.app`
     folder, parent folder writable. Otherwise an alert says to move
     Hearsay to Applications (buttons View on GitHub, Cancel).
  2. Asset choice: `Hearsay-<version>.dmg`, else the only `.dmg`; the
     checksum list is `SHA256SUMS.txt` (`<hex>  <name>` or `<hex> *<name>`).
  3. Download into `~/Library/Caches/tw.og1o.hearsay/Updates/<version>/`
     (other version folders removed first) with a progress window
     (received / total, Cancel removes the partial file). A DMG already in
     the cache whose checksum matches is not downloaded again.
  4. Verify: the DMG's SHA-256 must equal its line in `SHA256SUMS.txt` (no
     line = failure). Mount with `hdiutil attach -nobrowse -readonly
     -noverify -mountpoint <cache>/<version>/mnt`; exactly one `.app` at the
     root.
  5. Signature rule: when the running app's designated requirement names a
     certificate (self-signed or Developer ID builds), the new app must pass
     `SecStaticCodeCheckValidity` with that requirement (all architectures,
     nested code). When the running build is ad-hoc (`cdhash` requirement),
     only a valid signature is required and the skipped certificate check
     is logged. Both: `CFBundleIdentifier` is `tw.og1o.hearsay` and
     `CFBundleShortVersionString` is the release version. A failure deletes
     the download and shows "Hearsay could not verify the downloaded
     update." with the reason.
  6. Stage: copy to `<parent of Hearsay.app>/.Hearsay-update-<version>.app`
     (same volume, hidden), clear quarantine on the whole tree, check the
     copy's signature again, detach the DMG.
  7. "Hearsay <version> is ready to install." with "Install and Relaunch"
     and "Later". Later deletes the staged copy and keeps the verified DMG.
     Install refuses ("Finish the recording first.") while a recording
     session, its final pass, or any Whisper job (File mode) runs; notes
     generation and model downloads are not checked. Then
     `FileManager.replaceItemAt` swaps the bundles (on failure the old app
     stays and keeps running), the cache folder is deleted, and the usual
     restart path (`AppDelegate.restart`) opens the new bundle and quits.
     `restart` defers `NSApp.terminate` with `RunLoop.main.perform`:
     called from the installer's main-actor Task, the terminate-later
     reply Task could not run and 0.2.2 and 0.2.3 hung on "Installing"
     after the swap (found by the owner 2026-09-29, fixed the same day).
  - Debug: `HEARSAY_INSTALL_UPDATE=<dmg> HEARSAY_INSTALL_TARGET=<app>` runs
    steps 1 and 4 to 6 and the replacement against a scratch bundle.
- The version comes from `MARKETING_VERSION` / `CURRENT_PROJECT_VERSION` in
  `mac/project.yml` (0.1.0 and 1 for local builds; the release workflow sets
  both). About Hearsay and Settings > General show it.

### 4.7 CI and releases

Added 2026-09-28. Both workflows run on GitHub's `macos-15` Apple Silicon
runner with the newest stable Xcode installed there
(`.github/actions/setup-mac`), install XcodeGen and the Metal Toolchain when
missing, cache SwiftPM checkouts, and never download a model.

- `ci.yml` (pull requests, pushes to `main`): `swift test` for HearsayCore;
  a Release build through `Scripts/run-debug.sh --release --no-open --ci`;
  the HearsayWhisper unit tests (integration tests skip without a model);
  `export-strings.py` plus `merge-translations.py --check` for all four
  languages, failing if `strings-en.json` drifted; `make-notices.sh`,
  failing if `THIRD_PARTY_NOTICES.md` drifted.
- `release.yml` (tags `v*`): version from the tag, build number from the
  run number, Release build, Info.plist check, `Scripts/make-dmg.sh`
  (compressed DMG with Hearsay.app and an Applications link),
  `SHA256SUMS.txt`, and a GitHub release with generated notes. A tag with a
  suffix (`v0.3.0-beta.1`) is published as a pre-release.
- Signing: since v0.2.0 (2026-09-28) releases are signed with the owner's
  self-signed certificate "Hearsay Code Signing (self-signed)" from the
  `MACOS_CERTIFICATE_P12` secret (designated requirement `identifier
  "tw.og1o.hearsay" and certificate root = H"6cbba158…"`, the same as local
  builds), so TCC grants survive updates. Gatekeeper still treats it as
  unsigned: the notes explain the first launch (macOS 14: right-click >
  Open; macOS 15 and later: Privacy & Security > Open Anyway). Without the
  secret the build is ad-hoc. Notarization and stapling run only when the
  Apple ID secrets listed in `.github/workflows/README.md` exist too.

## 5. Model catalog and download

Built-in `ModelCatalog.json`, editable later without a code change. Every
entry below was checked against the Hugging Face API on 2026-09-28 and ships
`config.json` plus one `.safetensors` file in the mlx-whisper layout:

| id (HF repo) | Download | Note |
|---|---|---|
| `mlx-community/whisper-tiny-fp16` | 74 MB | smoke test, low accuracy |
| `mlx-community/whisper-base-fp16` | 143 MB | |
| `mlx-community/whisper-small-fp16` | 481 MB | |
| `mlx-community/whisper-small-4bit` | 139 MB | |
| `mlx-community/whisper-medium-fp16` | 1.52 GB | |
| `mlx-community/whisper-large-v3-turbo` | 1.61 GB | recommended default |
| `mlx-community/whisper-large-v3-turbo-8bit` | 863 MB | |
| `mlx-community/whisper-large-v3-turbo-4bit` | 463 MB | smallest good option |
| `mlx-community/whisper-large-v3-fp16` | 3.08 GB | same weights as the Python tool's model |
| `mlx-community/whisper-large-v3-8bit` | 1.64 GB | |
| `mlx-community/whisper-large-v3-4bit` | 877 MB | |

The tokenizer is not in these repos. Seven small files (about 4 MB) come
from `openai/whisper-large-v3` (`tokenizer.json`, `tokenizer_config.json`,
`generation_config.json`, `vocab.json`, `merges.txt`, `added_tokens.json`,
`special_tokens_map.json`) and are stored once, shared by every model.

Catalog entry fields: `repo`, `displayName`, `sizeBytes`, `files[]`,
`quantization`, `multilingual`, `recommended`.

**npz caveat, confirmed.** `mlx-community/whisper-{tiny,base,small,medium,
large-v3}-mlx` (the Python default among them) ship `weights.npz`, which
`mlx-swift` cannot load. They are excluded. The `-fp16`, `-4bit`, and
`-8bit` variants carry the same weights as safetensors.

Download design:

- `ModelDownloader` uses `URLSession` background configuration, one task per
  file (`config.json`, `weights.safetensors`, tokenizer files fetched from the
  sibling `openai/whisper-*` repo as `mlx-audio-swift` does).
- URL pattern: `https://huggingface.co/<repo>/resolve/main/<file>`.
- Resume with HTTP Range on relaunch. Progress per file and total.
- Verify size against the HF `Content-Length`, and the sha256 from the LFS
  pointer when available. Store a `manifest.json` per model with the commit
  hash so "Update available" can be shown later.
- Location: `~/Library/Application Support/Hearsay/Models/<repo>/`.
  Settings lets the user move it. Sandbox-safe because it is the app's own
  container.
- Model manager UI: list with installed / not installed, size, Download,
  Cancel, Delete, "Use this model". Deleting the active model falls back to
  none and disables Start until another is chosen.
- First launch with no model: onboarding step that recommends
  `whisper-large-v3-turbo` and starts the download on one click.

Loading: `WhisperModel.fromDirectory(URL)` accepts any local folder that
holds `config.json`, a `.safetensors` file, and `tokenizer.json`. Confirmed
in the spike. Quantized configs (`quantization` key in `config.json`) load
through the same path.

## 6. Whisper option parity

The vendored decoder ports these from `mlx_whisper` 0.4.3
(`transcribe.py`, `decoding.py`); the upstream Swift module has none of
them:

| Python | Hearsay decoder |
|---|---|
| timestamp tokens, one cue per sentence | port the timestamp rules from `decoding.py` (`ApplyTimestampRules`) and segment splitting from `transcribe.py`; seek by the last timestamp instead of fixed 30 s windows |
| `--condition-on-previous-text False` | supported; default off, like the Python tool |
| `--hallucination-silence-threshold 2.0` | ported as a documented no-op: in `mlx_whisper` 0.4.3 every use of it sits inside `if word_timestamps:`, so the Python tool never applies it either |
| `--language en\|zh` | supported |
| `--initial-prompt` for zh | supported as an advanced setting, but **off by default**. Measured 2026-09-28 on `shared/fixtures/zh-30s.wav` with the turbo model: the Python tool's English prompt ("The following is a sentence in Traditional Chinese.") made turbo echo the prompt and produce no transcript, and made large-v3 output Simplified characters. A Traditional Chinese prompt ("以下是繁體中文的句子。") gave Traditional script but rounded every timestamp to whole seconds and appended a hallucinated closing line. No prompt gave Traditional script, natural cue boundaries, and no hallucination. |
| temperature fallback on compression ratio / logprob | ported, but **off by default** (`temperatures = [0]`): the `mlx_whisper` CLI that whisper-tools calls decodes at temperature 0 with no fallback, and parity with it is the acceptance test. The fallback list is an advanced option. |
| no-speech threshold | port; skips silent windows |

Not ported in v1: word-level timestamps (`timing.py`, needs cross-attention
alignment heads), beam search.

## 7. AI provider presets

| Preset | Base URL | Notes |
|---|---|---|
| GitHub Copilot CLI | runs the installed `copilot` binary with the Python tool's argv; default `gpt-5.6-luna`, effort max | uses the CLI's own login |
| Claude Code CLI | `claude --print` with no tools, no user settings, no session persistence; default `claude-sonnet-5`, effort high (owner choice 2026-09-28) | Claude subscription login; no temperature |
| Codex CLI | `codex exec --sandbox read-only --ephemeral --ignore-user-config`, reply read from `--output-last-message`; default `gpt-6-luna`, effort max (owner choice 2026-09-28) | ChatGPT subscription login; no temperature |
| Antigravity CLI | `agy --output-format json --json-schema <tmp>/reply-schema.json --disable-slash-commands --sandbox --print-timeout 570s --log-file <tmp>/agy.log --project hearsay-notes [--model M] [--effort E] --print=<system message, no-tools line, prompt>`, cwd `<tmp>/hearsay-notes` (empty), reply from `structured_output`; default `gemini-3.8-flash-high` (owner choice 2026-09-28), `--effort` only for ids without a -low/-medium/-high suffix. Isolation (owner decisions 2026-09-28, verified with agy 1.2.12): before each run `AntigravityHousekeeping` makes sure the `hearsay-notes` project file in `~/.gemini/config/projects/` has `permissionGrants.permissionGrants.deny` = `command(*)`, `read_file(*)`, `write_file(*)`, `read_url(*)`, `execute_url(*)`, `mcp(*)` (merged, other keys kept; created when missing), which overrides the user's global allow list (`ls ..`, `cat` outside, `mkdir ../x`, view_file, write_to_file, read_url, a subagent's command: all denied); `search_web` cannot be denied. After each run the conversation is deleted by its `conversation_id` (files named after it under `~/.gemini/antigravity-cli`, its entries in `cache/*.json` and `jetbox_summaries_proto.pb`, its `conversation_summaries.db` row, and its subagents); `implicit/*.pb` (one unlabeled file per run) stays. The index edits run only for agy 1.2.x (version from `--version`, cached per launch) and only when no other `agy` process is running (`proc_listallpids`); otherwise only the id-named files go. Each edited store is first copied to `<store>.hearsay-backup` and replaced via temp file + `fsync` + `rename`, and not at all if it changed since it was read; the database is backed up with `VACUUM INTO` and edited in a transaction committed only after `PRAGMA quick_check` = ok | Google account login; `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `GOOGLE_APPLICATION_CREDENTIALS` removed; no temperature. Azure OpenAI preset removed 2026-09-28 (migrates to Custom) |
| Ollama / LM Studio | `http://localhost:11434/v1/chat/completions` | local, no token |
| Custom | any OpenAI-compatible URL | header name selectable |

Fields per provider: base URL, model, token (Keychain), reasoning effort
(optional, omitted when empty), extra headers. "Test connection" button sends
a one-line prompt.

## 8. Settings

- Window mode (section 4.4).
- Output folder (default `~/Documents/Hearsay`, chosen through
  `NSOpenPanel`, stored as a bookmark). With the sandbox off (section 1)
  the default is the real `~/Documents/Hearsay`, created on first use.
- Default language, default input device, capture system audio by
  default, default model.
- Ask before sending to AI: always / never.
- Launch at login (`SMAppService`).
- Keep recording (WAV) after a successful transcription: on (parity) /
  off. Off deletes the spool file; failures always keep it.
- Global hotkeys for Start/Stop and Pause.
- Prompt templates: list, add, edit, delete, set default.
- Check for updates automatically: on (default) / off, with Check Now and
  the version (section 4.6).

## 9. Entitlements and privacy

```text
com.apple.security.app-sandbox                       false   (see section 1)
com.apple.security.device.audio-input                true    (hardened runtime needs it)
NSMicrophoneUsageDescription  "Hearsay records meetings you start and transcribes them on this Mac."
NSAudioCaptureUsageDescription "Hearsay captures the audio of your calls so both sides of a meeting are transcribed."
```

System audio needs the Screen & System Audio Recording TCC permission.

**Ad-hoc signing and TCC (2026-09-28).** TCC keys a grant on the app's
code-signing requirement. An ad-hoc signature's designated requirement is
its cdhash (`cdhash H"…"`), which changes with every build, so after an
update `CGPreflightScreenCaptureAccess()` returns false while System
Settings still lists Hearsay as on, and macOS shows no prompt. Hearsay
stores the designated requirement (`SecCodeCopyDesignatedRequirement`,
falling back to the cdhash from `kSecCodeInfoUnique`) whenever it sees a
permission granted (`screenAudioGrantedCodeHash`,
`microphoneGrantedCodeHash`). `StaleGrantDetector` (HearsayCore) calls a
denial under a different requirement a stale grant. For system audio,
`PermissionMonitor` then runs `/usr/bin/tccutil reset ScreenCapture
tw.og1o.hearsay` once per requirement (`screenAudioResetCodeHash`; no
administrator rights needed; never in debug runs) and shows the
re-approval sheet; "Open System Settings" first calls
`CGRequestScreenCaptureAccess()` so Hearsay is listed again. The microphone
is never reset (macOS asks again by itself for `.notDetermined`); a stale
`.denied` only shows "Fix…" and the Microphone pane. Using the designated
requirement rather than the raw cdhash means builds signed with a stable
certificate (the local self-signed identity in `run-debug.sh`, a release
certificate secret, or a Developer ID) never look stale. The Record tab's
Permissions row shows both permissions; `PermissionMonitor` refreshes at
launch, whenever Hearsay becomes active, and every 2 s while the sheet is
open.

Transcripts never leave the machine unless the user confirms the AI step.
That sentence goes in the README and in the confirm sheet.

## 10. Phases and estimates

Estimates assume one developer working with an AI assistant, evenings and
weekends counted as half days.

| Phase | Deliverable | Estimate |
|---|---|---|
| 0. Spike | Done 2026-09-28, section 15. | done |
| 1. Skeleton | Done 2026-09-28. XcodeGen project, HearsayCore package, Settings window, window modes verified at launch. | done |
| 2. Models | Done 2026-09-28 except loading into the engine (Phase 4b). Real download and resume verified against Hugging Face. | done |
| 3. Audio | Done. Mic recording verified live 2026-09-28 after fixing the configuration-change stop. System audio mix still to be checked (section 16). | done |
| 4a. Decoder | Done 2026-09-28. Byte-identical SRT to Python on both fixtures; 38 tests. Run tests with `TEST_RUNNER_HEARSAY_MODEL_DIR=<model dir>`. | done |
| 4b. Transcription | Done. Live preview, final pass, and File mode verified live by the owner 2026-09-28. | done |
| 5. Notes | Done 2026-09-28 in code; runs from History > Generate notes. Not yet tried against a live provider. | done, unverified live |
| 6. Ship | Done 2026-09-28: v0.1.0 published from GitHub Actions (public repo lenny-osp/Hearsay; CI on macos-26 with Xcode 26.6; DMG plus SHA256; GitHub-releases update check; sections 4.6, 4.7). v0.2.0 published the same day: self-signed release signing (section 4.7), Permissions row and stale-grant recovery (section 9). Users of the ad-hoc v0.1.0 re-grant Screen & System Audio Recording once; later updates keep the grant. Remaining: Developer ID signing and notarization once the owner has an Apple Developer account (workflow steps are ready). | done |

Total: about 8 to 9 weeks of calendar time.

## 11. Testing

- `HearsayCoreTests`: port `whisper-tools/tests/test_run_whisper.py` cases for
  sanitize, insert name, save outputs, rename with rollback, timestamp
  parsing, level meter math, prompt text, response parsing, SRT formatting.
  These run on any Mac in seconds.
- Integration (opt-in, `HEARSAY_MODEL_DIR` set): transcribe
  `shared/fixtures/en-30s.wav` and `shared/fixtures/zh-30s.wav`, assert a known phrase
  appears and segment count is within range.
- Manual checklist per release: each window mode, mic permission denial,
  no-model state, download cancel mid-way, network failure during notes,
  disk full during output write, quit while recording.

## 12. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Vendored Whisper module drifts from upstream | Miss upstream fixes | Record the source commit in the file headers; diff against upstream every few months. |
| Timestamped decoder port has subtle bugs | Cues drift or overlap | Compare against the Python SRT for every fixture in tests; keep the Python CLI installed as the oracle. |
| Memory: large-v3 fp16 needs ~4 GB unified memory while loaded | 8 GB Macs struggle | Recommend turbo; unload model after 10 min idle. |
| Full Xcode needed | Blocks day one | Section 2, step 1. |
| Copilot CLI needs the sandbox off | No Mac App Store | Accepted 2026-09-28. GitHub Models is gone too (shut down 2026-07-30), so Copilot CLI is the zero-key path; the others need an API key or a local Ollama. |
| npz-only repos | Catalog entry silently unusable | Catalog lists safetensors repos only; spike verifies each. |
| ScreenCaptureKit audio always needs a video config | Wasted CPU, "Screen & System Audio Recording" permission wording | 2x2 frame at 1 fps, frames dropped. Verified against the macOS 27 SDK: no audio-only option exists. |
| Mic and system audio drift apart over an hour | Echo-like doubling in the mix | Align on host timestamps, resample the slower stream, test with a 60 min run. |
| Live chunks plus final pass double the compute | Battery, heat on laptops | Turbo model default; the final pass is skippable; unload model after idle. |

## 13. Out of scope for v1 (v2 candidates)

Considered on 2026-09-28 and deferred:

- Calendar event title as the default meeting name (EventKit).
- Auto language detection instead of the EN / ZH choice.
- Re-running notes on a past SRT from History with another provider.
- Copy to clipboard and macOS share sheet for the notes.

Not planned:

- Speaker diarization.
- Intel Macs.
- Windows (the PowerShell/Python path stays as is).
- Editing the transcript inside the app.

## 14. Open questions for the owner

1. Should Hearsay read the same `AI_*` environment variables when launched
   from a terminal, or are GUI settings enough? (Assumed: GUI only.)

Resolved 2026-09-28: output folder defaults to `~/Documents/Hearsay`
(configurable); Copilot CLI is dropped (and GitHub Models no longer exists);
system audio capture and live preview are both in v1.

## 15. Phase 0 spike results (2026-09-28)

Setup: `mac/Spike/` Swift package, executable `hearsay-spike`, depends on
`mlx-audio-swift` at commit `01dec7c9`. Built with `xcodebuild` after
installing the Metal Toolchain. Fixture: `shared/fixtures/en-30s.wav`, 18.9 s of
synthesized English speech. Machine: this Mac, Apple Silicon, macOS 27.0.

| Check | Result |
|---|---|
| Build links MLX and compiles Metal kernels | Yes, after `-downloadComponent MetalToolchain` and the two `-skip...Validation` flags |
| Load a model from our own folder | Yes, `WhisperModel.fromDirectory(URL)` |
| Quantized model (turbo-4bit, 463 MB) | Loads and transcribes; same text |
| Library's own HF download path | Works, writes to `~/.cache/huggingface/hub/mlx-audio/` |
| Model load time, turbo fp16 | 0.3 to 0.5 s |
| Transcribe 18.9 s, cold | 4.6 s (RTF 0.24) |
| Transcribe 18.9 s, warm | 1.0 s (RTF 0.05). A 60 min meeting is about 3 to 4 min |
| Text accuracy vs Python large-v3 | Identical wording |
| Python baseline, large-v3 fp16, whole run | 7.4 s including model load |
| Per-sentence timestamps | **No.** One segment per 30 s chunk. The decoder prompts `<\|notimestamps\|>` and masks every timestamp token |
| Initial prompt, condition-on-previous, hallucination threshold, no-speech, temperature fallback | **Not exposed.** The decode loop is greedy with fixed 30 s windows |
| Language auto-detect | Runs implicitly but the detected language is not reported |

Decision: vendor the Whisper module and write the decoder ourselves
(section 1, section 6). The model, layers, weight sanitizing, mel
spectrogram, and tokenizer wrapper are reused as is. Only
`transcribeChunk` and the chunking loop are replaced.

Reproduce:

```bash
cd mac/Spike && xcodebuild -scheme hearsay-spike -destination 'platform=macOS,arch=arm64' \
  -configuration Release -derivedDataPath .build/derived \
  -skipPackagePluginValidation -skipMacroValidation build
cd .. && Spike/.build/derived/Build/Products/Release/hearsay-spike \
  Spike/models/mlx-community_whisper-large-v3-turbo ../shared/fixtures/en-30s.wav en
```

`mac/Spike/models/` is git-ignored; re-download with the URLs in section 5.

## 16. Hands-on checklist for the owner (after Phase 4b)

Things no agent could verify because they need permissions or a person.
Verified 2026-09-28: recording, live preview, final pass, File mode (items
1, 10, 12 in part). Still open: 2 to 9, 11, 13 to 17.

1. First Start: grant Microphone, then Screen & System Audio Recording;
   relaunch if system audio stays off after granting.
2. Record 60 s with a call playing; confirm both small meters move and the
   WAV in the output folder contains both sides.
3. Close the main window while recording; the menu bar item keeps counting.
4. Press ⌃⌥⌘P and ⌃⌥⌘R with the app in the background.
5. Quit while recording; expect the "Stop recording and quit?" alert.
6. Switch all three window modes in Settings > Window while running.
7. Settings > AI: enter a token, "Test connection", then History >
   Generate notes on a real SRT.
8. Kill the app (`kill -9`) mid-recording, relaunch, expect the recovery
   sheet.
9. A 60 minute recording with mic plus system audio, then listen for
   drift near the end.
10. Watch the live preview fill in during a recording, and check the
    final SRT after Stop replaces it with sentence-level cues.
11. Press "Use live preview instead" during the final pass; the SRT
    should be written at once.
12. Drop an m4a on the File tab; the SRT lands in the output folder and
    the confirm-send sheet appears.
13. Updates (section 4.6): Hearsay > Check for Updates… before any release
    exists shows "No releases have been published yet."; after the first
    `v*` tag, a build with a lower version offers "Install Update" and
    "View on GitHub" (which opens the release page). About Hearsay shows
    the version.
14. Releases (section 4.7): push a test tag, check the Actions run, mount
    the DMG, drag Hearsay to Applications, and open it the first time as
    the release notes describe.
15. Permissions row, fresh grant (section 9): on a Mac (or user) that
    never granted Hearsay, the Record tab shows "Microphone: Not asked
    yet" with Allow and "System audio: Not granted" with Open System
    Settings. Allow shows the macOS prompt; Open System Settings shows the
    Screen & System Audio Recording prompt and pane. After turning Hearsay
    on (and relaunching if needed), the row collapses to one green line.
16. Update then re-grant (section 9): with system audio granted, install an
    ad-hoc signed build over it (`HEARSAY_SIGNING_IDENTITY=- Scripts/run-debug.sh
    --release`) and open it. Expect the sheet "System audio permission
    needs re-approval" once, "System audio: Needs re-approval after update"
    with Fix… on the Record tab, and in Console (subsystem
    `tw.og1o.hearsay`, category `permissions`) one `tccutil reset` line with
    status 0. Turn Hearsay on again; the sheet turns to "Granted" within
    2 s (or after Relaunch Hearsay). Quit and reopen: no second reset.
17. In-app update install (section 4.6), on the next release, from
    Hearsay in /Applications: Check for Updates > Install Update. Expect the
    progress window (Downloading Hearsay <v>… with received / total, Cancel
    works), then "Hearsay <v> is ready to install."; Install and Relaunch
    quits and opens the new version (About Hearsay shows it), and the
    Microphone and System audio permissions are kept.
    2026-09-29, 0.2.2 to 0.2.3: download, verification, and the swap
    worked; the relaunch hung on "Installing" (fixed, see 4.6 step 7).
    Builds up to 0.2.3 still hang once on their next update: force quit
    Hearsay and open it again; it is already the new version. Check again
    on the first update from a build that has the fix.

## 17. Polish list (found during review, not yet scheduled)

- **Decided 2026-09-28: Antigravity CLI isolation.** agy honors the
  owner's own `~/.gemini/antigravity-cli/settings.json` allow list, so
  Hearsay keeps deny rules in its `hearsay-notes` project and deletes each
  run's conversation afterwards (section 7). Probe leftovers (project `w`
  and the probe conversations) were deleted. Left for the owner: the agy
  log files from testing under `~/.gemini/antigravity-cli/log/`
  (`cli-20260928_164808`, `_171101`, `_171214`, `_174131`) and about 48
  `implicit/*.pb` files from today's runs, which carry no conversation id.

- Unit tests that use `UserDefaults(suiteName:)` leave a plist per test in
  `~/Library/Preferences` Fixed 2026-09-28: `ScratchDefaults` names suites by a path inside a temp
  folder, so cfprefsd never writes them to `~/Library/Preferences`. The
  app's debug suites (`DebugDefaults`, `RecordingReplay`) can still leave an
  empty `tw.og1o.hearsay.debug-*.plist`; same fix applies.
- The old sandbox container `~/Library/Containers/tw.og1o.hearsay/` still
  exists, so the `defaults` command reads its stale copy of the settings,
  not the app's real `~/Library/Preferences/tw.og1o.hearsay.plist`. Use
  `plutil -p` on the real file when checking settings, and consider asking
  the owner to delete the container.

- Windows version: planned in section 18 (owner request 2026-09-28).

- Permission grants survive updates only with a stable signing identity.
  Any stable certificate, even a self-signed one, gives a designated
  requirement of identifier plus certificate, which stays the same across
  builds, so TCC keeps the grants and the re-approval sheet (section 9)
  never appears. Done 2026-09-28: `run-debug.sh` signs local builds with
  "Hearsay Code Signing (self-signed)" and the same certificate is the
  `MACOS_CERTIFICATE_P12` secret, so v0.2.0 and later releases carry it.
  The stale-grant recovery stays for the one-time move from v0.1.0 and
  for any future certificate change (owner decision 2026-09-28).
- Done 2026-09-29: the restart-failed alert (`AppDelegate.replyToTerminate`)
  now says "Quit Hearsay and open it again." for both the language restart
  and the update relaunch.

- **To do (owner request 2026-09-28): "Reduce background noise" switch** on
  the Record tab. Routes only the mic through Apple voice processing so
  Control Center > Mic Mode > Voice Isolation becomes available. Warn in the
  UI that it also suppresses other people in the room; system audio is not
  affected. About half a day.

- `RecordingController.activate()` still observes `AVAudioEngineConfigurationChange`; harmless, remove.
- `SystemAudioRecorder` has its own copy of the sample-buffer converter; share `PCMSampleBufferConverter`.
- Cancelling the naming sheet after notes came back discards the notes
  (Python parity). Better: save under the AI-suggested name.
- `RecordingController` should expose its active spool URL so the recovery
  scan can exclude it instead of using the launch date.
- Shortcut labels assume a US keyboard layout.
- `NSAlert` for quit is modal; a SwiftUI confirmation would fit better.
- Stale sandbox comments in `RecordingSpool.swift` line 14 and
  `HotkeyManager.swift` line 7.
- Data left by the sandboxed builds was moved by hand on 2026-09-28 from
  `~/Library/Containers/tw.og1o.hearsay/` to `~/Library/Application
  Support/Hearsay/` and `defaults`; no in-app migration exists.
- `SystemAudioRecorder`: a stream error that arrives while still `.starting`
  is lost; `start()` returns normally. Store the failure and throw it.
- The final pass waits for the live-preview queue to drain; a lagging
  preview delays it by its backlog.
- On quit during a final pass the live preview is saved and the WAV is
  always kept; live chunks still queued are dropped (the WAV has them).
- Debug launch path (`HEARSAY_TRANSCRIBE_FILE`) needs files inside the app
  container because of the sandbox; document or drop before release.

## 18. Windows version

Planned 2026-09-28 (owner request). Work started 2026-09-29 on the dev
machine in 18.6; no code yet. This section is the design record for that work; expand it
in place as decisions are made.

### 18.1 Goal

A native Windows app with the same user-visible behavior as the macOS
app: record microphone plus system audio, live preview, final pass, File
mode, the same language picker (Auto / EN / ZH-TW / ZH-CN / DE / ES), the
same meeting-notes flow, output names, History, tray icon, hotkeys,
interface languages, and help. Same acceptance suite where the platforms
can agree (section 18.5).

### 18.2 Layout and sharing

```text
windows/
  Hearsay.sln
  Hearsay.App/        WinUI 3 app (views, tray, settings, help window)
  Hearsay.Core/       ported logic: naming, prompt, SRT, language decision, mixer
  Hearsay.Whisper/    whisper.cpp integration and model store
  Hearsay.Tests/      unit tests, driven by the shared vectors and fixtures
  scripts/            build, translation import, notices
  THIRD_PARTY_NOTICES.md
```

Shared resources (`shared/`) and what Windows does with them:

| Shared item | Exists today | Windows use |
|---|---|---|
| `fixtures/` audio and expected SRTs | yes | acceptance tests (tolerant comparison, 18.5) |
| `localization/` glossary and translations | yes | import into `.resw` with the same English keys, so the four translations are reused; new Windows-only keys are added to the JSON files |
| prompt text and JSON contract (`prompts/`: `general-meeting.txt`, `response-rules.txt`, `system-message.txt`, `languages.json`, `assembly.md`) | yes | loaded verbatim; a test proves the English and Traditional Chinese prompts equal the Python tool's |
| naming-rule test vectors (`naming-tests.json`, generated from the Python tool by `scripts/make-naming-tests.py`) | yes | both platforms run the same cases |
| language-decision vectors (`language-decision-tests.json`) | yes | both platforms run the same cases |
| help pages with embedded CSS (`help/<lang>/Help.html`) | yes | rendered in a WebView2 window |
| model catalog schema (`models/README.md`) | yes | Windows lists GGUF files from `ggerganov/whisper.cpp` on Hugging Face |
| app icon source (`assets/icon-1024.png`) | yes | build `.ico` from it |

Extraction done 2026-09-28 (W0). The macOS app keeps its compiled-in copies
and proves they match: `NotesTests.swift` compares the Swift prompt
constants byte for byte with `shared/prompts/`; `NamingTests.swift` and
`LanguageDecisionTests.swift` run every shared vector. The help pages are
edited only in `shared/help/`; `mac/Scripts/sync-shared.sh` (run by
`run-debug.sh` and `generate-project.sh`) copies them to the git-ignored
`mac/Hearsay/Resources/<lang>.lproj/Help.html`, so the bundled pages are
byte-identical to before. `mac/Scripts/make-icon.swift` writes
`shared/assets/icon-1024.png` from the same render as the AppIcon set; the
AppIcon PNGs stay where Xcode needs them. The mac model catalog stays in
`HearsayCore/Resources/ModelCatalog.json`.

Line endings (2026-09-29). The first Windows checkout (`core.autocrlf=true`,
Git for Windows' default) wrote CRLF into every text file, including the
byte-compared prompts, expected SRTs, vectors, and help pages. A root
`.gitattributes` now stores and checks out all text files as LF on every
platform (`.sln`, `.bat`, `.cmd` as CRLF); the index was already LF, so no
committed file changed. An existing Windows checkout must be refreshed
once after pulling this.

### 18.3 Stack

| Part | Choice | Notes |
|---|---|---|
| Language, UI | C# on .NET 10 (LTS), WinUI 3 (Windows App SDK) | .NET 10 pinned 2026-09-29 (installed on the dev machine; was ".NET 8 or later"); tray icon through the Windows App SDK notification-icon APIs or `H.NotifyIcon` |
| Audio capture | WASAPI via NAudio: `WasapiCapture` for the mic, `WasapiLoopbackCapture` for system audio | loopback needs no permission prompt; resample to 16 kHz mono like the Mac; port `AudioMixer` rules |
| Whisper | `Whisper.net` (whisper.cpp) with GGUF models; CUDA runtime package on NVIDIA, Vulkan on AMD and Intel, CPU fallback | whisper.cpp has its own timestamp decoder; language detection is built in; W1 checks the gaps in 18.8 |
| Chinese script | OpenCC (`OpenCCNET`) for Traditional and Simplified conversion, unless W1 finds the macOS transform usable (18.8) | .NET exposes no Hans-Hant transliteration |
| Transcript text language (History SRTs with no stored language) | open, see 18.8 | macOS uses Apple's NaturalLanguage |
| Meeting notes | same providers: Copilot CLI, Claude Code, Codex CLI (all support Windows), Antigravity CLI (confirm Windows availability first), Ollama, Custom | same JSON contract and prompt; tokens in Windows Credential Manager |
| Settings and state | `%APPDATA%\Hearsay\settings.json`; models in `%LOCALAPPDATA%\Hearsay\Models`; spool in `%LOCALAPPDATA%\Hearsay\Recording`; output default `%USERPROFILE%\Documents\Hearsay` | |
| Hotkeys, login, window modes | `RegisterHotKey`; `HKCU\...\Run` for launch at login; tray-only vs taskbar | |
| Tray status | Same setting as the Mac (`menuBarShowsStatus`, default on): the tray icon swaps to a red variant while recording and a pause variant while paused. The Windows notification area cannot show text next to an icon, so the elapsed time goes in the tooltip. Label is Windows-only ("Show recording status in the notification area"); add it to `shared/localization` | added 2026-09-29 |
| Localization | `.resw` generated from `shared/localization` by a script; interface language setting applies at next launch | |
| Help | WebView2 rendering `shared/help/<lang>/Help.html`; `hearsay://open/<tab>` links handled the same way | |
| Packaging | MSIX (or Inno Setup) with code signing through Azure Trusted Signing; updates through MSIX or Velopack | unsigned builds trigger SmartScreen |

### 18.4 Differences to accept

- **Output is not byte-identical.** whisper.cpp and MLX decode differently;
  acceptance compares text similarity and timestamps, not bytes.
- **Speed.** Without a GPU, `large-v3-turbo` on CPU is 5 to 20 times slower
  than on Apple Silicon. Default to a smaller quantized model on CPU-only
  machines and recommend NVIDIA for the turbo model.
- **System audio echo.** Loopback captures what the speakers play; a
  microphone in the same room hears it too. Recommend a headset, and keep
  the mixer's per-source meters so the user sees both.
- **Bluetooth headsets** switch to call quality on Windows as on macOS.

### 18.5 Acceptance

For each `shared/fixtures/<lang>-30s.wav`, transcribed with the Windows
default model and the fixed language:
- normalized text similarity to the expected SRT at least 0.9 (en, de, es)
  and character similarity at least 0.9 for zh after script conversion;
- every cue start and end within 0.5 s of the expected cue it overlaps most;
- Auto detection picks the right language with confidence above 0.9.
Naming, prompt, SRT formatting, and language-decision tests use the shared
vectors and must match exactly.

### 18.6 Phases

Estimates assume one Windows machine with Claude Code, Visual Studio 2026
(".NET desktop development" and "WinUI application development"
workloads; "Desktop development with C++" only if whisper.cpp is built
from source instead of Whisper.net's prebuilt runtimes), the .NET 10 SDK,
Git, and, if present, the CUDA Toolkit. Agents work the same way as on
macOS (`.claude/agents/windows-implementer.md`): one work item each, no
commits, a reviewer builds and tests.

Dev machine (inventoried 2026-09-29): Windows 11 Pro x64, Intel Core
i5-1235U (2 performance + 8 efficiency cores, 15 W), 16 GB RAM, Intel UHD
integrated graphics, no NVIDIA GPU, .NET SDK 10.0.401, Visual Studio
Community 2026 18.10, WebView2 runtime, Python 3.14. This is the plan's
worst supported case: W1 measures CPU and Vulkan on the integrated GPU
here, and the CUDA path needs another machine before it ships.

Toolchain verified 2026-09-29 with a throwaway project: a minimal WinUI 3
app (`net10.0-windows10.0.26100.0`, Windows App SDK 2.5.1 from NuGet,
unpackaged, self-contained, warnings as errors) builds with both
`dotnet build` and Visual Studio's MSBuild 18.10 and launches without
Developer Mode. MSVC 19.51 and the CMake bundled with Visual Studio work
through `Launch-VsDevShell.ps1`. WinUI project templates exist only in
Visual Studio, not in `dotnet new`; projects are written by hand or
created in the IDE.

| Phase | Deliverable | Estimate |
|---|---|---|
| W0. Shared extraction (done on macOS) | `shared/prompts`, `shared/naming-tests.json`, `shared/help`, `shared/assets`, model catalog split; macOS tests unchanged | 2 days |
| W1. Spike | Whisper.net transcribes the four fixtures; measure similarity, timestamps, speed on CPU and GPU; pick the default model | 2 to 3 days |
| W2. Core | Port naming, prompt, SRT, language decision, mixer; all shared vectors pass | 4 to 5 days |
| W3. Audio | WASAPI mic and loopback capture, resampling, spool WAV, no-audio watchdog | 4 to 5 days |
| W4. Shell | WinUI window with the five tabs, tray icon, window modes, hotkeys, settings, model store and downloads | 6 to 8 days |
| W5. Transcription | live preview, final pass, File mode, Auto detection with banners, Chinese conversion | 5 to 6 days |
| W6. Notes | CLI providers on Windows, Ollama, Custom, confirm and naming sheets, History with regenerate | 4 to 5 days |
| W7. Polish and ship | interface languages from shared translations, help window, crash recovery, MSIX, signing, updates, README | 5 to 6 days |

Total: about 6 to 8 weeks of agent time.

### 18.7 Risks

| Risk | Mitigation |
|---|---|
| whisper.cpp quality on Chinese below MLX | measure in W1 on `zh-30s.wav`; consider the `large-v3` (non-turbo) GGUF for zh |
| Antigravity CLI has no Windows build | ship without it; the preset is hidden when the binary is absent |
| CPU-only machines too slow for live preview | disable live preview below a measured speed threshold and say so |
| Loopback capture silent with exclusive-mode apps | document; offer "microphone only" |
| Two code bases drift | shared vectors and fixtures are the contract; a behavior change must update the shared files first |
| CUDA path untested (dev machine has no NVIDIA GPU) | test on an NVIDIA machine before W7; until then the CPU and Vulkan paths are the only measured ones |
| Line endings rewritten by a Windows checkout | root `.gitattributes` forces LF (18.2); the prompt and vector tests fail loudly on CRLF |

### 18.8 Porting gaps (found 2026-09-29)

macOS behavior that has no direct Windows equivalent yet. Each needs a
decision recorded here before the phase named.

| Gap | macOS today | Windows options | Decide by |
|---|---|---|---|
| Hallucination filter | `hallucination_silence_threshold` 2.0 (section 6), ported from `mlx_whisper` | whisper.cpp has no such option; measure on the fixtures without it, port the Python rule on top of whisper.cpp segments if the output needs it | W1 |
| Language detection | probabilities of the four supported languages, renormalized, averaged over up to three speech windows, plus no-speech probability | confirm Whisper.net exposes per-language probabilities (whisper.cpp `whisper_lang_auto_detect` returns them) and a no-speech probability; the shared language-decision vectors assume both | W1 |
| Chinese script conversion | ICU `Hans-Hant` transform through `String.applyingTransform` | OpenCC's tables are not ICU's, so the same Simplified text can come out with different Traditional characters and ZH-TW text would differ between the platforms (measure on `zh-30s`); check whether Windows' own `icu.dll` exposes the same transform (`utrans_*`), which would match macOS exactly | W1 |
| Transcript text language | `NLLanguageRecognizer` over the four languages, confidence at least 0.6 | no built-in Windows API; a small rule (CJK share for zh, stop-word scores for en, de, es) or a library; must pass tests built from the fixtures' SRTs | W6 |
