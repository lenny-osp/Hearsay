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
- Windows (2026-09-30): `windows-release.yml` runs on the same tag, waits
  for this release, and adds `Hearsay-<version>-win-x64.zip`, its line in
  the same `SHA256SUMS.txt`, and a Windows section in the notes (18.4,
  "Updates and packaging").

### 4.8 History

`HistoryIndex.scan` groups the files in the output folder by stem: one
entry is `<stem>.srt`, `<stem>.md` (notes), `<stem>_transcript.md` and
`<stem>.wav`, whichever exist. Row actions: Open SRT / Notes / Transcript,
Reveal in Finder, Rename…, Generate or Regenerate Notes… (`replaceNamed`),
Move to Trash….

**Rename…** (added 2026-09-29, owner request; no Python counterpart). It
opens the naming sheet in rename mode ("Rename meeting", prefilled with the
entry's meeting name, button "Rename"). `OutputWriter.renameEntry`:

- The name goes through `FilenameSanitizer`; nothing usable is rejected
  inline (and throws `unusableMeetingName` in core).
- New stem `<timestamp>_<slug>`. The timestamp is the one in the old stem,
  else the first existing file's birth or modification time (the
  `renameRetained` rule, `Timestamps.sourceFileTimestamp`), else now.
- `-N` when any of the four entry names is taken by a file that is not the
  entry's own (`freeStem` with file identities). All four suffixes count,
  not only the kinds the entry has, so a foreign `<new>.wav` never joins
  the renamed entry. The same name (or the entry's own `-N` name) is a
  no-op.
- Every existing file is renamed; a failed move rolls back the completed
  ones and throws `renameFailed` with `unrestored`.
- Notes and structured transcript whose first section has a
  `**Meeting Name:**` line (`MeetingNameInserter.hasMeetingName`) get it
  rewritten with `MeetingNameInserter.insert` and the slug (the value
  `saveNamed` writes), atomically replacing the renamed file after the
  moves. On a failed write the rewritten files get their old text back and
  every move is rolled back (`writeFailed`). Markdown without that line is
  only renamed.
- Disabled for the entry whose notes the History notes flow is generating,
  and for the Record tab's recording while it is recording or
  transcribing (its `finishedRecording` / `finishedTranscript` stems).
  Not covered: a Record or File tab notes flow still waiting at its
  confirm or naming sheet for the same SRT (that state is private to those
  tabs); renaming then makes that flow report the SRT as missing.
- After success History rescans and selects the renamed entry; errors go
  to an alert with the error text (which says what was restored).

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
1, 10, 12 in part). Still open: 2 to 9, 11, 13 to 18.

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
18. History > Rename… (section 4.8): rename a meeting that has SRT, notes,
    transcript, and WAV. All four files get the new name with the same
    timestamp, the row stays selected, and the notes' **Meeting Name:**
    line shows the new name. Rename another meeting to the same name: it
    gets `-2`. While a recording is transcribing, and while History is
    generating notes for a meeting, its Rename… is disabled.
19. Windows, microphone on real hardware (added 2026-09-29): the dev
    machine ran the W3 capture work in an RDP session with no capture
    endpoint, so the live microphone path never ran. On the machine itself
    (or with RDP microphone redirection on), from the repo root run
    `HEARSAY_TEST_AUDIO_DEVICES=1 HEARSAY_TEST_RECORD_SECONDS=2 dotnet test
    windows\Hearsay.Tests -c Release --filter "AudioDeviceListTests|AudioRecordingTests" --logger "console;verbosity=detailed"`
    and check: the enumeration lists the microphone, the 2 s recording's
    sample count is within 10 % of 32,000, and the diagnostics show a real
    mix format and non-zero callbacks. Also confirm the proposed hotkey
    defaults Ctrl+Alt+Win+R / Ctrl+Alt+Win+P (18.4, Settings).
20. Mac, silence hallucination check (added 2026-09-29 from the Windows
    W1 spike): append 45 s of digital silence to `shared/fixtures/en-30s.wav`
    (for example with `afconvert` or `sox`) and run it through
    `HEARSAY_TRANSCRIBE_FILE` with `HEARSAY_LANGUAGE=en`. The Windows spike
    saw turbo invent "Thank you." in the silence, and turbo's no-speech
    probability is near zero on both platforms, so the Mac's
    `hallucination_silence_threshold` may not fire either. If the Mac
    invents text too, port the Windows silence gate (18.4) to the Mac and
    record it in section 6.
21. Windows, History tab on the desktop (added 2026-09-29): with a few
    finished meetings in the output folder, check Open SRT / Notes /
    Transcript open in the right apps, Reveal in Explorer selects all the
    entry's files, Move to Recycle Bin… asks, then the files appear in the
    Recycle Bin, and Models > Show in Explorer opens the models folder.
22. Windows, the CLIs (added 2026-09-29): with Copilot, Claude Code, Codex
    and agy installed, generate notes from `shared/fixtures/en-30s.expected.srt`
    with each preset. Claude Code and Codex receive the prompt on stdin on
    Windows (18.4 "W6 core"); confirm both produce notes. Then try a
    transcript over about 32,000 characters with Copilot: the run must
    refuse before starting with the message naming the other providers.
23. Windows, interface language and Restart Now (added 2026-09-30): in
    Settings > General change the interface language to Deutsch, press
    Restart Now (it asks first while recording); Hearsay quits and comes
    back in German with one instance. Then set it back. The relaunch was
    never run by an agent (it would start a second instance).
24. Windows, update install (added 2026-09-30, after the packaging
    decision): with a release zip, run the `HEARSAY_INSTALL_UPDATE` entry
    against a scratch copy of the install folder (an agent did this on
    2026-09-30 for 0.0.0 to 0.9.9: every step printed ok), then a real
    in-app update from a GitHub release: Settings > General > Check Now,
    Install Update, the progress window, Install and Relaunch; the helper
    swaps the folder after quit and relaunches, and deletes
    `.Hearsay-previous` after success; log in
    `%LOCALAPPDATA%\Hearsay\Updates\install.log`. Never run by an agent
    (a normal launch would join the running instance).
25. Windows, shortcut recorder (added 2026-09-30): in Settings > General
    click the Start / Stop button, press Ctrl+Shift+F9: it shows and fires
    from another app. Try Shift+A (hint), the Pause chord (hint), Win+E
    (the shell takes it; nothing happens), an AltGr chord on a German
    layout, Escape (keeps the old one) and Backspace (default). While it
    listens, the old chord must not start a recording.

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
  Hearsay.slnx        solution (the XML format .NET 10 creates; VS 2026 opens it)
  Directory.Build.props  shared build settings: net10.0, x64, nullable, warnings as errors
  Hearsay.App/        WinUI 3 app (views, tray, settings, help window)
  Hearsay.Core/       ported logic: naming, prompt, SRT, language decision, mixer
  Hearsay.Whisper/    whisper.cpp integration and model store
  Hearsay.Tests/      unit tests, driven by the shared vectors and fixtures
  Spike/              W1 spikes (WhisperSpike, TextSpike); models/ is git-ignored
  scripts/            make-icon.ps1 (the .ico files from shared/assets), make-notices.ps1 (THIRD_PARTY_NOTICES.md; -Check for CI); later build, translation import
  THIRD_PARTY_NOTICES.md
```

Scaffolded 2026-09-29: `Hearsay.slnx`, `Hearsay.Core`, `Hearsay.Tests` (xUnit).
The tests find `shared/` through an assembly attribute stamped from
`Directory.Build.props` (`HearsaySharedDir`), so nothing in `windows/` is a
copy of a shared file; the prompt files are embedded into `Hearsay.Core` at
build time straight from `shared/prompts/`, and a test proves the embedded
bytes equal the files on disk.

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
| Whisper | `Whisper.net` 1.9.1 (whisper.cpp 1.8.5) with `Whisper.net.Runtime` and `Whisper.net.Runtime.Vulkan`, default load order Vulkan then CPU, `RuntimeOptions.LoadedLibrary` logged; the CUDA runtime package is added once tested on an NVIDIA machine. Default model `ggml-large-v3-turbo-q5_0` on every machine class. Language detection and the no-speech probability come from a direct P/Invoke into the bundled `whisper.dll` (one encoder run), not from `DetectLanguageWithProbability` (one call per candidate, no usable no-speech value). | Decided 2026-09-29 (W1 Whisper spike, `windows/Spike/WhisperSpike/REPORT.md`): turbo q5_0 reproduces the Mac's en/de/es output (similarity 1.000, every cue within 0.02 s) and scores 0.976 on zh (Simplified output, one dropped leading 按, possibly the quantization; f16 not measured). `ggml-small` fails 18.5 on every fixture (different cue splits, poor zh) and is no faster than turbo on Vulkan, so there is no "smaller model for CPU" option. Speed on the dev machine: turbo 2.0 to 2.9x real time on the Intel iGPU (warm 30 s window 8.8 s, cold 12.6 s, 1.25 GB), 0.28 to 0.69x on CPU (30 s window 37 to 49 s; 8 to 12 threads gain 20 to 25 %). Vulkan and CPU output are byte-identical except one 20 ms zh boundary. |
| Chinese script | Windows' own `C:\Windows\System32\icu.dll` (ICU 72 on this machine) through P/Invoke: `utrans_openU("Hans-Hant", UTRANS_FORWARD)` for Traditional, the same id with `UTRANS_REVERSE` for Simplified (what Swift's `reverse: true` does), `utrans_transUChars` to convert. No package. | Decided 2026-09-29 (W1 text spike, `windows/Spike/TextSpike/REPORT.md`): this is the same ICU transform the Mac uses and it reproduced the Mac's ZH-TW SRT for `zh-30s` byte for byte. OpenCC was rejected: every variant differs from ICU (on the fixture 里 vs 裏/裡; 231 to 242 of OpenCC's 3,980 table characters, including 为 台 干 只 群 周), `OpenCCNET` pulls vulnerable packages that fail restore with warnings as errors, and it costs about 1.5 s and 70 MB at startup. ELS transliteration and `LCMapStringEx` also differ from ICU. |
| Transcript text language (History SRTs with no stored language) | A rule-based detector in `Hearsay.Core` (no package, no ELS): Han-ideograph share for zh, Traditional-only vs Simplified-only characters (via the ICU transforms) to split ZH-TW and ZH-CN, function-word and diacritic scores for en, de, es; the five probabilities sum to 1 and the largest is the confidence. Same constants as the Mac: sample 4,000 characters, nil below 12 letters, confident at 0.6 or more, ties in picker order. | Decided 2026-09-29 (W1 text spike): 68/68 labelled samples right, never confident and wrong at 0.6. Windows ELS Language Detection also got 68/68 but exposes no confidence and returns bare `zh` for script-neutral Chinese, so it is not used. Implement in W6; tests assert the language and whether it is confident, never the number. |
| Meeting notes | same providers: Copilot CLI, Claude Code, Codex CLI (all support Windows), Antigravity CLI (confirm Windows availability first), Ollama, Custom | same JSON contract and prompt; tokens in Windows Credential Manager |
| Settings and state | `%APPDATA%\Hearsay\settings.json`; models in `%LOCALAPPDATA%\Hearsay\Models`; spool in `%LOCALAPPDATA%\Hearsay\Recording`; output default `%USERPROFILE%\Documents\Hearsay` | |
| Hotkeys, login, window modes | `RegisterHotKey`; `HKCU\...\Run` for launch at login; tray-only vs taskbar | |
| Tray status | Same setting as the Mac (`menuBarShowsStatus`, default on): the tray icon swaps to a red variant while recording and a pause variant while paused. The Windows notification area cannot show text next to an icon, so the elapsed time goes in the tooltip. Label is Windows-only ("Show recording status in the notification area"); add it to `shared/localization` | added 2026-09-29 |
| Localization | `windows/scripts/import-strings.py` generates `windows/Hearsay.App/Strings/<lang>/Resources.resw` (committed, so a build needs no Python; `--check` in CI) from `shared/localization/*.json`; `Strings.cs` is the single lookup over MRT Core (`ResourceManager` without package identity, language from `InterfaceLanguage.ResolveAtLaunch`); Restart Now relaunches. | Done 2026-09-30: 261 Mac keys reused (246 app, 15 core; all 96 core entries are also in the resw so `Strings.CoreText` translates Core's English), 40 Windows-only keys added under catalog `windows` with translations in all four languages; resw names are `<catalog>_` + 16 hex digits of the key's SHA-256 (keys are sentences). The Mac scripts skip catalog `windows` (`merge-translations.py validate`) and keep those entries on export (`export-strings.py`). Placeholders `%@` become `{0}`. Core errors carry their Mac key and values (`ILocalizedMessage` / `ILocalizedError`, `LocalizedMessage` in Core; `Strings.Localize` in the app), so formatted messages translate too; a test per type proves key plus values equal the English `Message`. Core's Windows-only texts (install location, zip and signer checks, the command-line limit, the joined login-status line, "Could not load the speech model: %@", and the inserted "The request timed out.", "HTTP %lld", "unknown", "none" as nested messages) have `windows` keys too (2026-09-30, 16 keys; `Strings.CoreMessageInWindowsCatalog` names them for import-strings.py). Still English: technical details inside a translated sentence (18.9) and hotkey key names. `windows/Hearsay.App.Tests` (net10.0-windows, references the app; copies `Hearsay.pri` to `resources.pri` so the resource manager works under testhost) covers the app-side logic. |
| Help | WebView2 rendering `shared/help/<lang>/Help.html`; `hearsay://open/<tab>` links handled the same way. One page per language serves both platforms: a platform-specific passage carries `data-platform="mac"` or `data-platform="windows"` on the smallest enclosing `<li>`, `<p>`, `<span>` or `<section>`, and the shared CSS hides it unless `<html>` has the matching class (`html:not(.windows) [data-platform="windows"]` and `html.windows [data-platform="mac"]` are `display: none`, so a page with no class shows the Mac text). The Windows `HelpWindow` adds class `windows` at each document's DOMContentLoaded through `ExecuteScriptAsync` and keeps the view transparent until then; `AddScriptToExecuteOnDocumentCreatedAsync` does not run with `IsScriptEnabled` off (WebView2 154), and turning page scripts on was rejected. | Decided 2026-09-30 (W7). Windows passages cover only what differs: notification area and taskbar, the three window modes, Settings > Privacy & security > Microphone (system audio needs no permission), Ctrl+Alt+Win+R / P and F1, `%USERPROFILE%\Documents\Hearsay`, Reveal in Explorer and the Recycle Bin, right-click, the install commands, the Copilot and Antigravity length limit (18.4 "W6 core"), Vulkan or CPU speed and live preview off (18.4 "Speed"), the File tab's types (18.4 "W5 app wiring"), the Windows model, restart wording. The Mac page renders the same visible text as before with no class (checked by extracting each page's visible text under the CSS rule, all five languages). The Mac still sets no class (18.9). |
| Packaging | **Decided 2026-09-30 (owner):** the unpackaged, self-contained `win-x64` app it already is (18.6, "Toolchain verified"), published as `Hearsay-<version>-win-x64.zip` (one `Hearsay\` folder inside) on the same GitHub Release and `SHA256SUMS.txt` as the DMG, with the in-app update of 18.4 "Updates and packaging": parity with the Mac's DMG plus in-app install. Signed with a self-signed code-signing certificate, "Hearsay Code Signing (self-signed)", held as a repository secret, as the Mac's is (4.7), so every release has the same signer and the update can check it; SmartScreen still shows "Windows protected your PC" (More info > Run anyway) on the first launch of a downloaded zip, which README and the release notes explain. If the owner buys Azure Trusted Signing, releases are signed with it instead and the prompt goes away as reputation builds; no code change (the signer rule accepts a renewed trusted certificate with the same subject). | Alternatives not chosen: **MSIX** needs a certificate the PC already trusts (a self-signed one must be imported into the machine's trusted store by an administrator, or the package sideloaded with Developer Mode, which AGENTS.md says not to ask users to change), and its App Installer updates would replace the Mac-style check. **Velopack** adds a framework (its NuGet package, CLI and release-feed layout) for delta updates and a Setup.exe, the kind of dependency 4.6 removed Sparkle to avoid. **Inno Setup** adds an installer the zip does not need. Release mechanics (`windows-release.yml`, `windows/scripts/make-release.ps1`, the secrets): 18.4 "Updates and packaging". |

### 18.4 Differences to accept

- **Output is not byte-identical.** whisper.cpp and MLX decode differently;
  acceptance compares text similarity and timestamps, not bytes.
- **Speed.** Measured 2026-09-29 (18.3, Whisper row): turbo q5_0 runs
  about 3x real time on an Intel iGPU through Vulkan and 0.3 to 0.7x on
  CPU. The default model is turbo q5_0 everywhere, because the smaller
  model fails acceptance. Live preview runs only when a warm 30 s window
  finishes in under 15 s (measured once per model load; the Record tab
  says "Live preview off: this computer is too slow for it" otherwise).
  On CPU the final pass takes about 1.5 to 3.5x the recording length; the
  app says so before the first recording on such a machine and uses 8 to
  12 threads. Recommend a GPU (Vulkan on Intel and AMD, CUDA on NVIDIA
  once tested).
- **Silence gate (hallucination).** whisper.cpp's no-speech probability is
  about 1e-10 on digital silence with turbo, and on 45 s of appended
  silence turbo invents text on every fixture ("Thank you.", "Vielen
  Dank.", "Gracias.", a YoYo TV subtitle credit for zh). The Mac's
  `hallucination_silence_threshold` cannot be ported (whisper.cpp has no
  such option) and the spike argues it never fires on the Mac either
  (turbo's no-speech is near zero there too; section 16 item 20 checks).
  Windows gates by audio level instead: a 30 s window whose RMS is below
  -60 dBFS is not transcribed, and a cue whose whole span is below that
  level is dropped; tests use the fixtures with 45 s of silence appended
  and require the original cues unchanged and no invented ones.
- **System audio echo.** Loopback captures what the speakers play; a
  microphone in the same room hears it too. Recommend a headset, and keep
  the mixer's per-source meters so the user sees both.
- **Bluetooth headsets** switch to call quality on Windows as on macOS.
- **Text-language confidence numbers differ** from `NLLanguageRecognizer`'s,
  so a borderline History SRT with no stored language may land on the other
  side of 0.6 and get the assumed language instead. Nothing else depends on
  the number.

W2 core port (2026-09-29; `windows/Hearsay.Core`, every shared vector
passes, 271 tests). Where Windows forced a difference from the Swift:

- **File identity** (`OutputWriter.FileIdentity`): Swift compares device
  and inode; Windows compares the normalized full path ignoring case (the
  Windows file id needs a handle per file, and NTFS, FAT and exFAT ignore
  case). Hard links, junctions, 8.3 names and per-directory case
  sensitivity are not recognized as the same file.
- **Birth time** (`Timestamps.SourceFileTimestamp`): the file's creation
  time, falling back to the last write only when creation time is the 1601
  sentinel; a missing file gives null.
- **Recycle Bin** (`ReplaceNamed`, `RecycleBin.cs`, 2026-09-30):
  `IFileOperation.DeleteItem` with `FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE`
  (plus `FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI |
  FOFX_EARLYFAILURE`); the progress sink's `PostDeleteItem` gives the
  recycled item (`C:\$Recycle.Bin\<SID>\$R<id>.md`), whose path is the
  handle. When a later step fails the old notes are moved back as on the
  Mac: `IFileOperation.MoveItem` of that item as a child of the Recycle Bin
  folder to the original folder and name, never over an existing file.
  `MoveItem` leaves the bin's `$I<id>` record (original path, deletion
  time) behind, so Hearsay deletes it when it names the restored path; an
  orphaned record is not shown in the bin. A file the bin cannot take
  (network share, oversized) still asks before it is deleted for good
  (`FOF_WANTNUKEWARNING`), gives no handle, and a rollback lists it as not
  restored, as before. A failed move back lists the `$R` path (the Mac lists
  the file's Trash URL). Operations run on an STA thread. Tested against
  the real bin with `HEARSAY_TEST_RECYCLE_BIN=1` (off by default). The
  user-facing text still says "Trash" so the translations are reused; W7
  decides on a Windows string.
- **History scan** skips files with the Hidden attribute as well as dot
  files; reparse points are not skipped because OneDrive placeholders in
  Documents are reparse points.
- **Resampler** (`MonoResampler`): the Mac wraps Apple's unpublished
  `AVAudioConverter`. Windows uses a Kaiser-windowed sinc filter (16 zero
  crossings, rolloff 0.9, beta 8), channel average for the downmix, no
  delay, output length `ceil(frames × 16000 / rate)`; 16 kHz input passes
  through with only the downmix. Same length, level and timing as the Mac,
  not the same samples. A 10 kHz tone is removed by more than 60 dB.
- **Mixer source errors**: Swift's `AsyncStream` cannot throw, so a failing
  recorder there ends its stream; in C# a source whose `IAsyncEnumerable`
  throws is treated as ended the same way. The W3 recorder must report the
  error itself before its stream ends.
- **Prompt template ids** are written as lowercase GUIDs (Swift writes
  uppercase); only matters if settings are ever shared across platforms.
- **JSON error detail** after "AI output is not valid JSON: " comes from
  System.Text.Json and reads differently from Foundation's; the prefix is
  the contract.
- **Fixture WAV layout**: `en`, `de`, `es-30s.wav` carry a 4,044-byte Apple
  `FLLR` chunk (data at byte 4096); only `zh-30s.wav` is a plain 44-byte
  header. `WavWriter` writes the canonical header, so only `zh` round-trips
  byte for byte; for the others the PCM data does.
- **Not ported yet, by design**: display strings are English until W7
  wires `.resw`.

W6 core (2026-09-29; `windows/Hearsay.Core/Notes`: `AIProviderStore` on the
app's `SettingsFile` and `ISecretStore`, `CliProcess`, `CliClient`,
`ChatCompletionsClient`, `AntigravityHousekeeping`, `NotesPipeline` with a
new `GenerateAndSaveAsync` that does the file part the Mac keeps in
`NotesFlowViewModel`). Where Windows differs from the Mac:

- **Command-line length.** Windows allows 32,766 characters (the Mac about
  1 MB). Claude Code and Codex get the prompt on stdin (Claude Code drops
  the trailing `-- <prompt>`; Codex gets `-- -`); the argv builders stay
  flag for flag the Mac's, the move happens in
  `CliArguments.WindowsInvocation`. Copilot and Antigravity have no
  documented stdin input and keep the prompt in argv; a transcript over the
  limit (roughly a 20-minute meeting) fails before the run starts with a
  message that names Claude Code, Codex or an HTTP provider instead. To
  check with the real CLIs (section 16 item 22): that `claude --print`
  with no prompt argument and `codex exec … -- -` read stdin on Windows,
  and whether Copilot or agy accept stdin after all.
- **npm `.cmd` shims** are started as `node.exe <script>` (or the `.exe`
  the shim points at), never through cmd.exe, which cannot pass newlines
  or `% & | ^` safely. A batch file that is not a shim runs only when every
  argument is cmd-safe; otherwise the launch fails pointing to Settings > AI.
- **Locating a CLI**: the Mac's login-shell lookup is an in-process search
  of `%USERPROFILE%\.local\bin`, `%LOCALAPPDATA%\agy\bin` (agy),
  `%LOCALAPPDATA%\Microsoft\WinGet\Links`, `%APPDATA%\npm`,
  `%ProgramFiles%\nodejs`, nvm-windows versions, then `PATH` with `.exe`,
  `.cmd`, `.bat`. Assumed program names: `copilot.exe` (winget) or
  `copilot.cmd` (npm), `claude.exe` (native installer,
  `irm https://claude.ai/install.ps1 | iex`) or `claude.cmd`, `codex.cmd`
  (npm), `agy.exe` (`%LOCALAPPDATA%\agy\bin`, the install command from
  antigravity.google/docs/cli/install). A configured path may be quoted,
  use `%VAR%` or `~`, or omit the extension.
- **Timeout and cancel kill the process tree** (a shim's node starts the
  real CLI as a child); no console window; environment keys compare
  ignoring case; `PATH` uses `;`.
- **agy data** lives under `%USERPROFILE%\.gemini` (projects in
  `config\projects`, conversations in `antigravity-cli`) per agy's docs,
  not `%APPDATA%`; only the program is in `%LOCALAPPDATA%`. "Another agy is
  running" is detected by image name; the index is read through Windows'
  own `winsqlite3.dll` (no package). Storage layout and the 1.2 version
  gate were verified on the Mac only; housekeeping deletes only files
  named after the conversation id unless agy is exactly 1.2.x and idle.
- **Settings**: `aiProviderConfiguration` and `promptTemplates` are JSON
  values inside `settings.json` (the Mac stores data blobs); GUIDs lowercase.
- **HTTP errors**: a timeout reads "The request timed out."; "Cannot reach
  <host>" on name-resolution or connection errors; an `error` object
  without `message` is shown as JSON.
- **Speed probe audio**: the engine's `MeasureWindowSeconds()` embeds a
  fixture; it must be `en-30s.wav` (synthesized `say` voice), not
  `zh-30s.wav` (the owner's voice), so the product ships no personal audio.
  The W5 engine landed with zh; the app wiring switched it (2026-09-30).

W6 app (2026-09-30; `Features/Settings/AISettingsView.cs`, `Features/Notes/`
NotesFlowViewModel, NotesFlowView, ConfirmSendSheet, TemplateEditorSheet,
NotesPanel, CommandLineCheck; History Generate/Regenerate wired; the flow
starts from the Record and File results as on the Mac). Where Windows
differs from the Mac:

- **Command-line refusal shown early**: `CommandLineCheck` builds the exact
  invocation and the confirm sheet shows the pipeline's `CommandLineTooLong`
  text with Send disabled (Copilot and Antigravity only; 18.4 "W6 core").
  With "Ask before sending" off the refusal appears as the flow's error.
- **Confirm sheet** keeps the Mac's Keep local (N) / Send (Enter); Escape
  answers Keep local plus timestamp names. "Name this meeting yourself?"
  puts its default, Keep Timestamp Names, on the left (Windows order).
- **Result** adds Open Notes and Open Transcript beside Reveal in Explorer;
  paths are shortened in the middle at 84 characters with the full path in
  the tooltip.
- **Wording**: "this PC", "Recycle Bin"; each CLI caption adds "To install
  it, run `<InstallCommand>` in a terminal." (new key).
- **No timeout field** (the Mac has none); `CliClient.Timeout` stays 600 s.
  Temperature is a NumberBox (empty = omitted). Extra headers save on focus
  loss (the Mac on Return).
- **`GenerateAndSaveAsync` is unused**: the naming sheet sits between
  generating and saving, so the flow calls `GenerateAsync` then
  `SaveNamed` / `ReplaceNamed` / `RenameRetained` like the Mac.
- **Untested live**: Check <tool>, Test connection, a real send, and
  `CredentialManagerSecretStore` from the AI page (the snapshot run uses
  the in-memory store). The locator finds `copilot.exe` (WinGet Links),
  `claude.exe` (`~\.local\bin`) and `agy.exe` on the dev machine, so
  section 16 item 22 can run here once the owner says so.

Chinese script and text language (2026-09-29, after the W1 text spike):
`ChineseScriptConverter` P/Invokes `icu.dll` (classic `DllImport`, exports
checked up front, a missing DLL or export throws
`PlatformNotSupportedException`; nothing falls back to the input text);
`TranscriptTextLanguage` is the rule detector (sample is the first 4,000
grapheme clusters, NFC-normalized; `detect(srtText:)` is `DetectSrt`);
`StoredTranscriptLanguage.Resolve` is in. 

W3 capture (2026-09-29; `windows/Hearsay.Core/Audio`, NAudio.Wasapi 3.1.0,
`WasapiRecorder` because 3.1.0 marks `WasapiCapture` obsolete). Where
Windows differs from the Mac:

- **Timestamps**: each chunk carries the QPC time of its first frame from
  the packet position WASAPI reports (closer to the Mac's presentation
  timestamps); "callback time minus buffer duration" is only the fallback
  for drivers that report position 0. Stamps are shifted back by the
  resampler's held-back output so chunks are contiguous.
- **Loopback is not continuous**: WASAPI loopback delivers nothing while
  no app plays (measured: 0 packets in 2 s of silence), where
  ScreenCaptureKit streams continuously. A 0.1 s timer fills any gap over
  0.2 s with silence at the capture rate, through the same resampler;
  Stop fills up to the stop time. Loopback also captures Hearsay's own
  output (the Mac excludes it).
- **Default output device changes** move loopback capture to the new
  default; after a capture error the endpoint is reopened on the current
  default within the same 5-per-10-s recovery limit. Only "no output
  device left" ends the recording (`NoOutputDevice`, a new string). System
  audio start is synchronous; there is no `starting` state and no
  permission case.
- **Microphone pause releases the audio client** (the mic-in-use indicator
  goes off); resume reopens the endpoint (a `WasapiRecorder` cannot restart)
  and a failed resume goes through capture-error recovery. System-audio
  pause keeps capture running and drops chunks, as on the Mac.
- **Recovery**: a capture error (for example `AUDCLNT_E_DEVICE_INVALIDATED`
  on a format change) reopens the endpoint. The chosen endpoint being
  removed or disabled ends the recording with `ConfigurationChanged`, no
  fallback to another microphone. WASAPI has no interruption notification,
  so `Interruptions` is always 0. `cannotSelectDevice` has no Windows case.
- **Permission** cannot be checked in advance; `E_ACCESSDENIED` at start
  becomes `PermissionDenied` with Windows wording pointing to Settings >
  Privacy & security > Microphone (new localization key, W7).
- **Device identity** is the endpoint id string; `ResolveSelection` ports
  `RecordingController.refreshDevices()` (chosen, else default, else first).
- **Untested on hardware**: the dev machine runs in an RDP session with no
  capture endpoint ("Remote Audio" output only), so the live microphone
  path, real int16/24-bit mix formats and QPC stamps on real packets have
  only run against the fake session. Run
  `HEARSAY_TEST_AUDIO_DEVICES=1 HEARSAY_TEST_RECORD_SECONDS=2 dotnet test
  windows\Hearsay.Tests -c Release --filter "AudioDeviceListTests|AudioRecordingTests"`
  on a machine with a microphone (section 16 item 19).

Settings (2026-09-29; `windows/Hearsay.Core/Settings`):

- **Storage** is one `settings.json` (UTF-8, no BOM, LF, indented) in the
  folder the app passes (`%APPDATA%\Hearsay`); unknown keys are kept,
  comments and trailing commas tolerated on read. Atomic write: temp file
  in the same folder, then `File.Move(overwrite: true)`, retried up to 5
  times on a sharing violation. A file that is not a JSON object is moved
  to `settings.corrupt.json` and settings start empty. A failed write does
  not throw; the value holds for the run and `SaveFailed` fires.
- **Key names** are the Mac's except `outputFolder` (a path) in place of
  `outputFolderBookmark`. The Mac-only permission hashes round-trip unused;
  `activeModelRepo` keeps its name for the Windows model id.
- **Hotkeys** store `{keyCode, modifiers}` with Win32 values (VK codes;
  MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4, MOD_WIN 8; MOD_NOREPEAT added at
  registration). Proposed defaults Ctrl+Alt+Win+R and Ctrl+Alt+Win+P
  (⌘→Win), to be confirmed by the owner. Core's `DisplayString` is
  English (log lines); the app shows `HotkeyDisplay.Text`, which translates
  the modifiers, Space and "Key n" (18.9 lists the rest).
- **Window mode** keeps the Mac's stored values; the menu bar item is the
  tray icon and the Dock icon the taskbar button.
- **Interface language**: `ResolveAtLaunch(stored, environment)`
  (`HEARSAY_UI_LANGUAGE` wins) and `NeedsRestart`; the app sets its
  resource language at launch (W7).
- **Output folder**: `MakeStoredPath` replaces `makeBookmark`; a chosen
  folder is used only when it is a full path to an existing folder, else
  the default (created on demand). Nothing is tracked across moves.
- **Secrets**: Credential Manager generic credentials, target
  `Hearsay/<account>`, `CRED_PERSIST_LOCAL_MACHINE`, UTF-8 blob (2,560-byte
  limit). Error text "Credential Manager error: <message>" (new key, W7).
- **Owed by W4** (done in the shell, 2026-09-29): subscribe to
  `AppSettings.SaveFailed` and show `SettingsFile.CorruptFileBackup`; share
  one `SettingsFile` per folder (two instances overwrite each other's
  keys); later stores (AI providers, prompt templates) take the same
  `SettingsFile`.

Model store (2026-09-29; `windows/Hearsay.Core/ModelStore`, catalog in
`Resources/ModelCatalog.json`, sizes from the Hugging Face API at
`ggerganov/whisper.cpp` commit 5359861c, 2026-09-29):

- **Entry id** is `repo/weightsFile` (one repo holds every GGUF), folder
  name is the id with `/` → `_`; `AppSettings.ActiveModelRepo` stores it.
  An entry's files are the weights file alone; an empty tokenizer list
  counts as installed and no `_tokenizer` folder is made.
- **Delete is async**: it cancels a running download and waits up to 30 s
  for the partial file to close (Windows cannot delete an open file).
- **Downloader**: `HttpClient`, follows redirects itself (max 10) so Hugging
  Face's `x-linked-size` and `x-repo-commit` are read and `Range` plus
  `Accept-Encoding: identity` are re-sent; a read stalled 60 s fails with
  "The request timed out."; cancel is a `CancellationToken`, progress an
  `IProgress`; no checksum (the Mac has none either; the API reports LFS
  sha256 if wanted later). Partial file `<name>.partial` as on the Mac.
- **Recommended** is `ggml-large-v3-turbo-q5_0.bin` provisionally, pending
  the W1 spike.

W4 shell (2026-09-29; `windows/Hearsay.App`, unpackaged self-contained
WinUI 3 on Windows App SDK 2.5.1, H.NotifyIcon.WinUI 2.4.1 for the tray
because the SDK has no notification-icon API; WebView2 comes with the SDK):

- **Tabs** are a top-mode `NavigationView` (Record, File, Models, History,
  Settings, Help as a footer item plus F1); Settings sections are a
  `SelectorBar` with the Mac's four sections (General, Window, Output, AI).
  The language picker sits on the Record and File tabs as on the Mac.
- **Tray**: native Win32 popup menu with the Mac's items; left click opens
  the main window (the Mac's click opens its panel); elapsed time only in
  the tooltip. Icons are built by `windows/scripts/make-icon.ps1` from
  `shared/assets/icon-1024.png` (macOS margin cropped), with a red dot for
  recording and an amber dot with pause bars for paused.
- **Window modes**: in taskbar-only mode closing the window quits, because
  a taskbar button needs a window (the Mac keeps running in the Dock).
- **Hotkeys**: `RegisterHotKey` on a hidden message-only window. The
  shortcut recorder (done 2026-09-30, `HotkeyRecorderView`) mirrors the
  Mac's: click, press a chord with Ctrl, Alt or Win; Escape cancels; the
  hotkeys are unregistered while it listens. Windows additions: Backspace
  or Delete alone restores that action's default, the other action's
  chord is refused, and a trial `RegisterHotKey` (`HotkeyManager.Probe`)
  refuses a chord Windows or another app owns ("already used by another
  app or Windows") before it is saved; each reason shows under the button.
  Modifier names are Windows' in each language (Strg, Umschalt, Mayús).
- **Single instance** through `AppInstance.FindOrRegisterForKey`; debug
  runs skip it. WebView2 profile in `%LOCALAPPDATA%\Hearsay\WebView2`.
- **Help**: the shared pages copied at build time; they describe macOS
  (Menu bar, System Settings); resolved 2026-09-30 with `data-platform`
  passages (18.3 Help row), so this bullet is history: W7 needed Windows text or conditional
  sections.
- **Debug entry** `HEARSAY_UI_SNAPSHOTS` renders 14 PNGs (tabs, Settings
  sections, the settings-problem banner, the help window, a help-link
  navigation) and smoke-tests the tray states and hotkey registration;
  other `HEARSAY_*` entries exit 2 with "not available yet" until W5.
- **No unit tests for app-side logic** (`HelpNavigation.Decide`,
  `TrayIcon.CurrentIconName`): `Hearsay.Tests` targets `net10.0` and cannot
  reference the Windows-targeted app; add a `Hearsay.App.Tests` project or
  move that logic into Core (W7 polish).
- **Not yet**: Restart Now after an interface-language change (the
  quit-while-recording prompt landed with the W5 app wiring;
  Acknowledgements with the polish of 2026-09-30, below; Software updates
  with the update UI of 2026-09-30, 18.4 "Updates and packaging").

Models and History tabs (2026-09-29; `Features/Models`, `Features/History`,
`Features/Notes/NamingSheet.cs` as a ContentDialog in name, regenerate and
rename modes):

- **Deleting a model asks first** (the Mac deletes straight from the
  context menu); the context menu opens on right click or Shift+F10.
- **Labels**: "Show in Explorer" (opens the models folder), "Reveal in
  Explorer" (selects all the entry's files with
  `SHOpenFolderAndSelectItems`), "Move to Recycle Bin…" for "Move to
  Trash…" (through `OutputWriter.MoveToRecycleBin`, the same call
  `ReplaceNamed` uses). A file type with no default app opens in Notepad.
- **Rename is disabled** for stems in `RecordingStatus.BusyFiles` (empty
  until W5 fills it) and, from W6, while History generates notes for the
  entry. Generate/Regenerate Notes… is disabled until W6.
- The naming texts keep the Mac's "press Return"; W7 decides on "Enter".
- The onboarding dialog is 640 wide (the default cut the label).
- Snapshots: 23 PNGs per language; the History samples are built in the
  scratch output folder from `shared/fixtures/en-30s.expected.srt` (copied
  to `DebugSamples\` at build time) and a WAV from `WavWriter`; the run
  proves rename, rejection, rollback on a locked file, and reselection
  against the real `OutputWriter.RenameEntry`. The real Recycle Bin,
  Explorer reveal and file opening were not exercised (they would touch the
  owner's desktop); section 16 item 21.

W5 Whisper engine (2026-09-29; `windows/Hearsay.Whisper`, Whisper.net 1.9.1
with the CPU and Vulkan runtimes). Where Windows differs from the Mac:

- **Engine**: one whisper.cpp context per loaded model for both detection
  and transcription, driven through the C API directly (21 exports, listed
  in `WhisperNative.ExportNames`): Whisper.net keeps its context handle
  private, so its `WhisperProcessor` could not share a context with the
  detector. Whisper.net only picks and loads the runtime (`RuntimeOptions`,
  default order Vulkan then CPU); the engine binds the `whisper.dll` that
  loader loaded (found among the process modules) and logs the runtime,
  its path and whisper.cpp's system info. The two by-value structs are
  checked against Whisper.net's own declarations by a test. Context: GPU
  on, flash attention off (Whisper.net's default, the configuration W1
  measured). Idle unload after 600 s as on the Mac.
- **Options** (`TranscriptionOptions`, Mac defaults): temperatures become
  whisper.cpp's start plus increment (the default `[0]` is no fallback);
  `compressionRatioThreshold` 2.4 maps to `entropy_thold` (token entropy,
  not the zlib ratio); non-speech tokens suppressed (`suppress_nst`, the
  Python `suppress_tokens="-1"`); timestamps on, `no_context` on, no
  prompt. Threads: max(4, min(12, logical cores)) on CPU, min(4, cores) on
  a GPU runtime.
- **Silence gate**: the grid of 30 s windows starts at sample 0 of the
  samples passed in (the whole recording, or one live chunk); consecutive
  non-silent windows are one `whisper_full` call, so whisper.cpp still
  seeks by timestamps inside a run. Cues with no samples (past the end)
  count as silent. With `language: nil` the engine detects on the first
  transcribed window, where the Mac uses the first 30 s (a silent first
  window is never transcribed here). The app path passes a fixed language
  after `LanguageDetection.Detect`, so this only affects direct callers.
- **Cancellation** is checked before each run and, through whisper.cpp's
  encoder-begin callback, before each 30 s window (as the Mac); the CPU
  backend also polls it inside a window (abort callback). Progress is
  whisper.cpp's integer percent per run, mapped to the fraction of the
  whole input (skipped windows count as done).
- **Speed probe**: `MeasureWindowSeconds()` transcribes
  `shared/fixtures/en-30s.wav` (embedded at build, looped to 30 s,
  language en; zh until 2026-09-30, see "Speed probe audio"); a 4 s
  warm-up (the clip's first 4 s) runs first when nothing ran since the
  load. With en: Vulkan 8.0 to 11.3 s per warm window, warm-up 6.0 to
  9.5 s.
  Measured here: Vulkan 10.1 to 12.6 s per warm window (warm-up 10.1 s;
  the W1 spike measured 8.8 s, and other agents were building on the
  machine), so live preview stays on; CPU runtime at 12 threads 55.8 s
  (warm-up 55.8 s), so live preview is off.
- **Acceptance on Vulkan** (turbo q5_0, fixed language): en, de, es
  similarity 1.000, worst cue delta 0.02 s; zh 0.964 against
  `zh-30s.truth.srt` after conversion to Traditional (the dropped leading
  按 and 臘漆 for 臘七 remain), worst delta 0.46 s (last cue end). Auto
  detection over the four: en 0.9999, zh 0.9991, de 0.9996, es 0.9998;
  top over all 100 within 0.001 of Python; no-speech 1e-12 to 1.5e-11.
  With 45 s of digital silence appended every fixture keeps its cues
  unchanged and gets no invented cue (the spike's "Thank you." / "Vielen
  Dank." / "Gracias." / YoYo credit are gone).

W5 app wiring (2026-09-30; `windows/Hearsay.App/Features/Recording`,
`FileTranscription`, `Transcription`, `Recovery`, `Debug`): the
`RecordingController`, the Record and File tabs, the recovery sheet, the
quit prompt and the `HEARSAY_TRANSCRIBE_FILE`, `HEARSAY_REPLAY_FILE`,
`HEARSAY_RECORD_SECONDS` entries, ported from the Mac files of the same
names. `RecordingStatus` is now fed by the controller (phases Starting,
Stopping and Transcribing added; the tray's state line shows
"Transcribing… N%"), and `BusyFiles` is the recording's files while it is
active or transcribing. Where Windows differs from the Mac:

- **Live preview gate** (18.4 "Speed"): the first recording with a model
  starts the speed probe (the engine's `MeasureWindowSeconds`, which loads
  the model); live chunks queue until it answers. The result is kept per
  model file for the process (not re-measured after an idle unload). Too
  slow: the queue is dropped, the Record tab shows "Live preview off: this
  computer is too slow for it", and, as on the Mac without live preview,
  Auto detects the language at Stop over the whole recording. The first
  recording after launch therefore spends about 15 to 20 s of engine time
  on the probe (Vulkan here) before the first live chunk. The CPU notice
  is in "App polish" below.
- **Starting**: there is no microphone or screen-recording permission to
  request; the capture endpoints are opened on the thread pool (MTA), so
  "Starting…" shows while WASAPI opens them. System audio that cannot start
  gives "System audio off: <reason>"; there is no "permission denied" badge
  and no Open System Settings button.
- **File decoding**: `AudioFileLoader` uses NAudio's `MediaFoundationReader`
  (in NAudio.Wasapi, already a Core dependency, so no package is added),
  float output at the file's rate, then Core's `MonoResampler`, the same
  filter capture uses. `AudioGraph` was rejected: a WinRT async graph per
  file and a different resampler from recordings. Media Foundation reads
  wav, m4a/aac, mp3, wma, flac, mp4/mov/m4v/3gp audio (and ogg/opus/webm
  where the Web Media Extensions are installed); it does not read AIFF or
  CAF, which the Mac takes. The accepted-types line reads "wav, m4a, mp3,
  aac, wma, flac, or the audio track of mp4 / mov" (new Windows key).
- **Result row**: "Reveal in Explorer" and "Open SRT" (through
  `ExplorerShell`, as History), plus "Generate Notes…", the hook W6 wires
  (`RecordView.GenerateNotes`, `FileView.GenerateNotes`); the automatic
  start after every transcript is `NotesRequested` on the controller and
  the File view model (the Mac's `notesRequest`).
- **Recovery sheet** is a ContentDialog with its three buttons in the
  content, so Escape cannot pick Delete (the Mac disables interactive
  dismiss). "Launch date" is the process start time. The date omits the
  weekday of .NET's long date pattern, like the Mac's `.long`.
- **Quit** while a session is active asks "Stop recording and quit?" in a
  dialog on the main window (shown first if hidden); while transcribing it
  cancels without asking, saves the live preview and keeps the WAV, as the
  Mac.
- **Debug entries**: `HEARSAY_MODEL_DIR` may name the `ggml-*.bin` itself
  or a folder holding it (the recommended model is used, else the only
  one). Every output (SRT, WAV, spool) is written inside the throwaway
  settings folder and removed with it, so `HEARSAY_TRANSCRIBE_FILE` and
  `HEARSAY_REPLAY_FILE` also print the SRT text to stdout between
  `--- srt ---` and `--- end ---`; debug stdout and stderr are UTF-8 when
  redirected. `HEARSAY_RECORD_SECONDS` writes its WAV in the throwaway
  folder (the Mac: the temp folder) and lists the devices when none
  matches. The replay never starts the notes flow; the Mac's
  `HEARSAY_REPLAY_SNAPSHOTS` is not ported.
- **UI snapshots**: 30-39 render the Record tab (idle, recording with
  meters and live text, the mismatch banner, the final pass, the Auto
  fallback notice, the result row), the File tab (idle, transcribing,
  finished with a suggestion) and the recovery sheet, all stubbed; the
  tray smoke test stubs its states instead of starting a recording.
- **Measured here** (Vulkan, turbo q5_0; `HEARSAY_TRANSCRIBE_FILE` is load
  + decode + detect + transcribe): en 26 s, zh 22 s, de 21 s, es 21 s;
  Auto picked en 0.9999, zh 0.9991 (ZH-TW, Traditional), de 0.9996,
  es 0.9998; en with DE fixed gives the "This sounds like English" banner.
  Replay of en-30s with silent system audio: one live chunk at Stop, final
  pass 28 s after Stop; a 76 s replay in Auto cut chunks at 30 and 60 s,
  detection at 30 s settled English, the final pass took 30 s. The live
  microphone path is still untested on hardware (W3, section 16 item 19).

Updates and packaging (2026-09-30; W7). **Decided 2026-09-30 (owner):**
the zip plus in-app update proposed in 18.3 (Packaging); a self-signed
code-signing certificate held as a repository secret, like the Mac's; every
`v*` tag carries both platforms (one version number, one release, one
`SHA256SUMS.txt`); the suggested install folder is
`%LOCALAPPDATA%\Programs` (README). The core is done and tested in
`windows/Hearsay.Core/Updates` against scratch folders and fake zips.

Release mechanics:

- **`.github/workflows/windows-release.yml`** runs on the same `v*` tag as
  `release.yml`, on `windows-latest`: the version from the tag with the
  Mac's rule (a suffix makes a pre-release), a Release build with
  `-p:Version=<version>`, the core and App tests, then
  `windows/scripts/make-release.ps1`.
- **`make-release.ps1 -Version <v> [-PfxPath <pfx> -PfxPassword <p>]`**
  (Windows PowerShell 5.1): `dotnet build windows\Hearsay.slnx -c Release
  -p:Version=<v> --artifacts-path dist\build`, a clean build of its own, so
  no stale dev-build file reaches the zip and a running dev copy (which
  locks `bin\`) does not block it. `dotnet build`, not `dotnet publish`:
  the app is already self-contained, and its build output is the complete
  app (300 files, 162.6 MB without the `.pdb` files, which are left out).
  It checks ProductName `Hearsay` and ProductVersion `<v>` (build metadata
  ignored), signs `Hearsay.exe` and the three `Hearsay*.dll` with `signtool
  sign /fd SHA256` (no timestamp unless `-Timestamp`: it adds nothing to a
  self-signed certificate), requires each signature to be the PFX's
  certificate and `signtool verify /pa` to pass or fail only on the
  untrusted root ("signed, chain untrusted", the expected result), zips
  `dist\Hearsay-<v>-win-x64.zip` entry by entry with forward slashes (one
  `Hearsay/` top folder; .NET Framework's `CreateFromDirectory` writes
  backslashes) and writes the `SHA256SUMS.txt` line in `shasum`'s format
  (two spaces, LF). The zip is about 53 MB. Without a PFX it makes an
  unsigned zip and says so. Checked 2026-09-30 with a throwaway
  certificate: the Core's `FileAppIdentityReader` reads the signed exe as
  `UntrustedRoot` with the PFX's SHA-256 thumbprint, and `VerifyApp`
  accepts it against a running build with that thumbprint and refuses it
  against another signer.
- **Secrets**: `WINDOWS_CERTIFICATE_PFX` (base64 of the PFX) and
  `WINDOWS_CERTIFICATE_PASSWORD`, made once on the owner's machine by
  `windows/scripts/make-signing-cert.ps1` ("CN=Hearsay Code Signing
  (self-signed)", RSA 3072, SHA-256, 10 years, in `Cert:\CurrentUser\My`;
  refuses to run when one with that subject exists, unless `-Force`, and
  refuses a PFX path inside the repository). The workflow decodes the PFX
  into `$RUNNER_TEMP` and deletes it in an `always()` step. Without the
  secrets the zip is unsigned, with a warning in the log, the job summary
  and the release notes; a signed installed copy refuses it as an update
  (the signer rule below), so users download it.
- **Coordination with the Mac release**: the Windows job waits (every
  minute, up to 45 minutes) until the release exists with
  `Hearsay-<v>.dmg` and `SHA256SUMS.txt`, the last file `release.yml`
  uploads; then it downloads `SHA256SUMS.txt`, replaces or adds the zip's
  line, and uploads the zip and the merged file with `gh release upload
  --clobber`. It adds a "Windows" section to the notes (download, extract,
  SmartScreen "More info > Run anyway", `Get-FileHash` against
  `SHA256SUMS.txt`) between `<!-- hearsay-windows:start/end -->` markers,
  before GitHub's generated "What's Changed", so a re-run replaces it. If
  the Mac release never appears it creates the release with `gh release
  create` and says so in the notes; a Mac job finishing later would
  overwrite the notes and `SHA256SUMS.txt` (softprops), so the Windows
  workflow is then re-run. Tested offline only (the steps' scripts
  against a simulated `gh`: both release states, a re-run, non-ASCII
  notes); the first real tag is the live test.

Mechanism, step by step against 4.6:

- **Check** (`UpdateChecker`): the Mac's request, 15 s timeout, version
  rule, errors and texts on `HttpClient`, plus a `User-Agent` header, which
  GitHub's API requires (URLSession adds one by itself). Same settings
  (`automaticUpdateChecks`, `lastUpdateCheck`) and schedule (10 s after
  launch, then hourly, due after 24 h); `CheckAsync` stores the time after
  a success or a 404, as the Mac's `UpdateService.fetchOutcome`. The slug
  is `UpdateChecker.HearsayRepository`, tested equal to
  `HEARSAY_UPDATE_REPOSITORY` in `mac/project.yml`: both platforms read the
  same "latest" release, so one tag should carry both the DMG and the zip.
  A Mac-only release appears on Windows as available with "The release has
  no Windows zip file or checksum list." and Download (release page).
- **Asset**: `Hearsay-<version>-win-x64.zip`, else the only asset ending in
  `-win-x64.zip` (no plain `.zip` fallback, which a macOS or source archive
  would match); `SHA256SUMS.txt` is the same file with one more line.
- **Location** (4.6 step 1): refused when Hearsay runs from inside the zip
  (Explorer's `Temp1_*.zip`, 7-Zip's `7zO*`, WinRAR's `Rar$*` folders; the
  Mac's translocation), when its folder has no `Hearsay.exe` (the Mac's "not
  an app bundle"), or when the folder or its parent cannot be written (a
  probe file, so ACLs decide; for example under Program Files). There is no
  disk-image case.
- **Download and verify** (steps 3, 4): into
  `%LOCALAPPDATA%\Hearsay\Updates\<version>\` (other versions removed, a
  cached zip whose checksum matches is reused), SHA-256 against its line in
  `SHA256SUMS.txt` (no line = failure), extracted with `ZipFile` (entries
  outside the folder refused; the Mac mounts the DMG), exactly one
  `Hearsay.exe`, at the root or in one top folder.
- **Identity and signer** (step 5): ProductName `Hearsay` (the Mac's bundle
  identifier) and ProductVersion without the `+<commit>` suffix equal to the
  release version, from the version resource; Authenticode through
  WinVerifyTrust (no revocation check, no network). When the running exe is
  signed, the new one must be intact and signed by the same certificate
  (SHA-256 thumbprint) or, when both chain to a trusted root, by a
  certificate with the same subject (Trusted Signing renews its short-lived
  certificates). When the running build is unsigned (local and CI builds),
  only an intact signature is required if the new one is signed at all (the
  Mac's ad-hoc case). Failures delete the cache folder.
- **Stage** (step 6): copied to `<parent of install>\.Hearsay-update-<v>`
  (hidden, same volume), `Zone.Identifier` streams removed (the Mac's
  quarantine), checked again.
- **Install** (step 7): Windows cannot replace a running exe, so instead of
  `replaceItemAt` the app starts `UpdateSwapHelper`, a Windows PowerShell 5.1
  script passed with `-EncodedCommand` (no script file, so the execution
  policy is not involved; hidden; working folder outside the install), and
  quits. The helper waits up to 120 s for the app's process (id and start
  time, so a reused id is not waited for), renames the install folder to
  `.Hearsay-previous` and the staged folder into its place (each retried
  for 30 s), clears the hidden attribute, deletes the old folder and the
  cache, and relaunches `Hearsay.exe`. On a failure nothing changes, or the
  old folder is renamed back; the old version is relaunched and
  `%LOCALAPPDATA%\Hearsay\Updates\install.log` (`SwapPlan.LogFileIn`) says
  why; exit codes 3 to 6. The install path stays the same, so
  shortcuts and the Run key keep working. Tested with the real script on
  scratch folders (swap, locked old and new folders with rollback, waiting
  for a process, a process that does not quit, a reused id); not tested
  with a signed build or a real relaunch, since no release exists. Risk: a
  launcher that runs Hearsay in a kill-on-close job object would end the
  helper with the app; Explorer and the Run key do not.
- **Debug**: `UpdateInstallDebug.RunAsync` is the core of
  `HEARSAY_INSTALL_UPDATE=<zip> HEARSAY_INSTALL_TARGET=<folder>
  [HEARSAY_INSTALL_VERSION=<v>]`: the steps above against a scratch folder,
  the swap through the real helper with no process to wait for and no
  relaunch; the running app's folder is refused. The App wires the variable.
- **Texts**: the Mac's keys where the text is the same (`UpdateTexts`,
  `UpdateCheckError`, most of `UpdatePackageError`). Windows-only keys for
  `shared/localization`: "Windows is running Hearsay straight from the zip
  file. …", "This copy of Hearsay is not in its own app folder, so it cannot
  update itself. …", "Hearsay cannot write to the folder %@. Move the
  Hearsay folder …", "The zip file could not be extracted: %@", "The zip
  file should contain one Hearsay.exe but contains %lld.", "The new version
  is signed by %1$@, not by %2$@ like this copy of Hearsay.", "The new app
  has the product name %@, not Hearsay.", "The release has no Windows zip
  file or checksum list.", "The last update could not be installed. Hearsay
  is still the previous version."
- **Decided** (2026-09-30, owner; see the top of this block): zip plus
  in-app update, the self-signed certificate as a repository secret (Azure
  Trusted Signing stays possible later without a code change), both
  platforms on every `v*` tag, and `%LOCALAPPDATA%\Programs\Hearsay` as
  the suggested install folder.

App polish (2026-09-30; `windows/Hearsay.App`, the Whisper project file,
`windows/THIRD_PARTY_NOTICES.md`):

- **Package weight.** The app references the Windows App SDK's split
  packages at the versions the 2.5.1 metapackage pins (Foundation 2.3.12,
  WinUI 2.3.9, InteractiveExperiences 2.1.9, DWrite 2.1.0, Runtime 2.5.1;
  Base 2.0.4 and WebView2 come through them), not the metapackage, so
  Windows ML, `Microsoft.WindowsAppSDK.AI`, Search and Widgets drop out.
  H.NotifyIcon.WinUI 2.4.1 depends on the metapackage (>= 1.6.250108002),
  which would bring the 1.6 SDK back, so the app prunes it
  (`PrunePackageReference`, NuGet package pruning in the .NET 10 SDK); the
  tray works on the split packages. `Whisper.net.Runtime.Metal` (a macOS
  shader file) is pruned the same way in Hearsay.Whisper and the app.
  Whisper.net's runtime packages have no RID filtering, so
  `Hearsay.Whisper/TrimWhisperRuntimes.targets` drops every `runtimes\<rid>`
  item but `win-x64` and `vulkan\win-x64` before target paths are assigned
  (imported by Hearsay.Whisper and the app). Result on a clean Release
  build: 404 files, 306.1 MB before; 302 files, 170.3 MB after; the notices
  list 20 packages instead of 27 (9,742 lines instead of 16,025).
- **Models path** is shortened in the middle (`MiddleTrimmedText`, the
  Mac's `.truncationMode(.middle)`) with the whole path in a tooltip;
  "Show in Explorer" keeps its column. The path is no longer selectable
  text (the Mac's is); the tooltip shows it whole.
- **Settings > AI labels**: the label column is as wide as the widest label
  in the interface language (150 to 230), so "Esfuerzo de razonamiento:"
  fits and the controls stay aligned.
- **Tray tooltip** while transcribing: "Hearsay: Transcribing… 42%" (the
  Mac's menu bar percentage), plain "Hearsay" with the status setting off.
- **CPU notice** (18.4 "Speed"): when the Record tab activates with a model
  installed, the app loads the whisper.cpp runtime library (no model;
  `WhisperRuntime.EnsureLoaded`, on the thread pool) to learn whether it is
  the CPU one; on CPU the Record tab says "This computer transcribes on its
  processor (CPU): after you stop, the transcript takes about 1.5 to 3.5
  times as long as the recording." until the first recording of the run
  starts. The Mac has no such notice. The runtime DLL is therefore loaded
  at launch on a machine with a model, not at the first recording.
- **Acknowledgements** in Settings > General, after Shortcuts: the Mac's
  text and "Show Licenses…", which opens the licenses page in a help
  window: the Mac's `LicensesSheet` text ("Hearsay", LICENSE, a rule of 72
  "=", THIRD_PARTY_NOTICES.md) as monospaced text. The build writes it to
  `help\Licenses.html` (an inline MSBuild task in Hearsay.App.csproj, so the
  page follows the committed notices without a runtime copy); the Mac's
  Done button is the window's close button.
- **Snapshots**: 56 (Record tab with the CPU notice, stubbed) and 57 (the
  licenses page); the tray smoke test checks the transcribing tooltip.

Update UI (2026-09-30; `Features/Updates/`: UpdateService with states
Idle / Checking / UpToDate / Available / Downloading / Verifying / Ready /
Installing / Failed, UpdateInstaller on the Core steps, UpdatePrompts,
UpdateProgressWindow, SoftwareUpdatesSection in Settings > General between
Shortcuts and Acknowledgements; `HEARSAY_INSTALL_UPDATE` runs before any
window opens). Checks 10 s after launch and hourly, only when
`lastUpdateCheck` is 24 h old, and on Check Now. Where Windows differs
from the Mac:

- "Hearsay x is ready to install." with Later and Install and Relaunch is
  shown inside the progress window (the Mac closes the panel and shows an
  alert). The failure dialog adds Show in Explorer while the zip is still
  in the cache. The Settings card uses the Mac's Check Now and Install
  Update keys; there is no app-menu Check for Updates… (a tray-menu entry
  is a 18.9 candidate).
- The version line "Version %@ (%@)" shows the commit the SDK appends as
  the build (7 digits; "0" without one); the Mac shows its build number.
- "Verifying…" starts at the download's last progress report because
  `UpdateInstall.PrepareAsync` has no step callback.
- After a relaunch the app reads `%LOCALAPPDATA%\Hearsay\Updates\install.log`
  (up to 10 s for the helper's "exit N") and on failure shows
  `UpdateTexts.PreviousInstallFailed`, then renames the log to
  `install-previous.log`.
- An install blocked by a recording, final pass, busy engine or File mode
  discards the staged copy and goes back to Available before the helper
  starts, so the quit never has to ask.
- Untested: the GUI path against a real release and a signed build
  (section 16 item 24); the state machine (47 tests) and the Core steps
  through the debug entry are tested.

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
| W1. Spike | Whisper.net transcribes the four fixtures; measure similarity, timestamps, speed on CPU and GPU; pick the default model. **Done 2026-09-29** (both halves; 18.3 Whisper and Chinese script rows, 18.4, 18.8) | 2 to 3 days |
| W2. Core | Port naming, prompt, SRT, language decision, mixer; all shared vectors pass. **Done 2026-09-29** (four parallel agents, one day): naming + OutputWriter incl. Rename, HistoryIndex, prompt + reply contract + presets, SRT, LanguageDecision + SessionLanguage + LiveChunker, AudioMixer + LevelMeter + MonoResampler + WavWriter + RecordingSpool; 271 tests, every Swift test ported one for one except those needing AVFoundation or a device (listed in the test files) | 4 to 5 days |
| W3. Audio | WASAPI mic and loopback capture, resampling, spool WAV, no-audio watchdog. **Done 2026-09-29** except the hardware run (18.4, "Untested on hardware"); with it the settings layer (AppSettings, hotkeys, interface language, output folder, Credential Manager) and the ICU script converter and text-language detector; 487 tests | 4 to 5 days |
| W4. Shell | WinUI window with the five tabs, tray icon, window modes, hotkeys, settings, model store and downloads. **Shell, Settings, tray, hotkeys, help window, UI snapshots and the model store core done 2026-09-29** (18.4); the Models and History tabs follow | 6 to 8 days |
| W5. Transcription | live preview, final pass, File mode, Auto detection with banners, Chinese conversion. **Engine done 2026-09-29** (`windows/Hearsay.Whisper`: one context, detection, silence gate, speed probe; 18.4 "W5 Whisper engine"); **app wiring done 2026-09-30** (Record and File tabs, recording controller, recovery sheet, quit prompt, debug entries; 18.4 "W5 app wiring") | 5 to 6 days |
| W6. Notes | CLI providers on Windows, Ollama, Custom, confirm and naming sheets, History with regenerate and Rename (section 4.8; port `OutputWriter.renameEntry` and its tests). **Core done 2026-09-29, app done 2026-09-30** (18.4 "W6 core" and "W6 app"); live runs with the real CLIs are section 16 item 22 | 4 to 5 days |
| W7. Polish and ship | interface languages from shared translations, help window, crash recovery, MSIX, signing, updates, README. **Done 2026-09-30**: interface languages, Windows help passages, crash recovery (with W5), the update check, install and in-app update UI, `HEARSAY_INSTALL_UPDATE`, third-party notices, Windows CI, the release tooling (`windows-release.yml`, `make-release.ps1`, `make-signing-cert.ps1`) and the README section. Open: the owner creates the signing certificate and adds the two secrets (18.4 "Updates and packaging"), the first real tag is the live test of the workflow, and the 18.9 list | 5 to 6 days |

Total: about 6 to 8 weeks of agent time.

### 18.7 Risks

| Risk | Mitigation |
|---|---|
| whisper.cpp quality on Chinese below MLX | measured 2026-09-29: turbo q5_0 scores 0.976 on `zh-30s` and drops one leading character; if real meetings show more, measure the f16 turbo (1.6 GB) or `large-v3-q5_0` (1.1 GB) before switching |
| CPU-only machines too slow for live preview | measured 2026-09-29: yes on CPU (30 s window 37 to 49 s); live preview is disabled when a warm 30 s window takes over 15 s (18.4) |
| Antigravity CLI has no Windows build | ship without it; the preset is hidden when the binary is absent |
| Loopback capture silent with exclusive-mode apps | document; offer "microphone only" |
| Two code bases drift | shared vectors and fixtures are the contract; a behavior change must update the shared files first |
| CUDA path untested (dev machine has no NVIDIA GPU) | test on an NVIDIA machine before W7; until then the CPU and Vulkan paths are the only measured ones |
| Line endings rewritten by a Windows checkout | root `.gitattributes` forces LF (18.2); the prompt and vector tests fail loudly on CRLF |

### 18.8 Porting gaps (found 2026-09-29)

macOS behavior that has no direct Windows equivalent yet. Each needs a
decision recorded here before the phase named.

| Gap | macOS today | Windows options | Decide by |
|---|---|---|---|
| Hallucination filter | `hallucination_silence_threshold` 2.0 (section 6), ported from `mlx_whisper` | **Decided 2026-09-29:** not portable; Windows uses the audio-level silence gate in 18.4 instead. Silero VAD (`WhisperVadProcessor`) stays a fallback option if the gate proves insufficient on real meetings. | done |
| Language detection | probabilities of the four supported languages, renormalized, averaged over up to three speech windows, plus no-speech probability | **Decided 2026-09-29:** Whisper.net's `DetectLanguageWithProbability` gives one probability per call and `SegmentData.NoSpeechProbability` is unusable, so the engine P/Invokes the bundled `whisper.dll` (`whisper_lang_auto_detect`, softmax of the SOT logits for `<|nospeech|>`), one encoder run per window; the spike's values match Whisper.net's within 1e-4 and the Python references within 0.001. Every fixture detects above 0.99. The engine owns one whisper context for both detection and transcription (the spike's prototype loaded the model twice). The Mac's RMS gate (0.001) is ported with it. | done |
| Chinese script conversion | ICU `Hans-Hant` transform through `String.applyingTransform` | **Decided 2026-09-29:** Windows' `icu.dll` exposes the same transform and matches the Mac byte for byte on `zh-30s`; OpenCC rejected (18.3). Follow-up: a Mac-generated `shared/` vector file of Simplified↔Traditional pairs that both test suites run, so an ICU change on either OS is caught. Note `zh-30s.expected.srt` is the raw model output (mostly Simplified); the Mac's ZH-TW rendering of it equals `zh-30s.truth.srt` with 臘七→臘漆 and 裏→里. | done |
| Transcript text language | `NLLanguageRecognizer` over the four languages, confidence at least 0.6 | **Decided 2026-09-29:** rule-based detector (18.3), implement in W6 with tests from the fixtures' SRTs and the Mac's `TranscriptTextLanguageTests` inputs; also try longer synthetic transcripts before W6 closes | done |

### 18.9 Windows polish list (found during review, not yet scheduled)

Like section 17, for the Windows app. Add here rather than leaving
findings only in a chat report.

- **Live preview override** (owner, 2026-09-30, first hands-on run on the
  dev machine): the Record tab said "Live preview off: this computer is
  too slow for it" although the same machine measures 8 to 12.6 s per
  30 s window on Vulkan. The speed probe (18.4 "Speed", 15 s limit) ran
  once at the first recording, while agents were building and testing on
  the machine, and its result is cached for the process, so a busy moment
  sticks until relaunch. Add a Windows-only setting in Settings > General,
  "Live preview: Automatic / Always on / Off" (`livePreviewMode`; the Mac
  has none because Apple Silicon is always fast enough), where Always on
  ignores the probe and lets the preview lag on a slow machine; and re-run
  the probe when a recording starts more than a few minutes after the
  cached result, or when it was measured under load. First confirm from
  the log (`whisper: runtime …` and `whisper: speed probe …` lines,
  visible when Hearsay.exe is started with stdout redirected) whether the
  GUI launch loaded Vulkan or fell back to CPU.
- **Core texts still English** (the Windows-only Core texts got `windows`
  keys on 2026-09-30, 18.3 Localization row): the technical detail inside
  a translated sentence (the whisper.cpp or runtime message after "Could
  not load the speech model:", "whisper_full failed (n)" after
  "Transcription failed:", system error messages, `SignatureInvalid`'s
  "invalid" / "not signed"). The app shows `error.Message` instead of
  `Strings.Describe` for the `WhisperEngineException` of
  `WhisperModelLocation.Active` in `FileViewModel` and `RecordingController`
  (five `catch` blocks), so "No model installed" and "not fully downloaded"
  stay English there. The update texts (`UpdateTexts.*Message`,
  `PreviousInstallFailedMessage`), `InstallLocationException`,
  `CliInstallation.LocalizedLoginStatus` and `ModelStore.CatalogErrorMessage`
  are ready for the app to show through `Strings.Localize` / `Describe`.
- **Shortcut recorder follow-ups** (the recorder itself done 2026-09-30,
  18.4 "W4 shell" Hotkeys): chords the shell or another app takes before
  any window sees them (Win+E, Win+L, a chord another app registered)
  never reach the recorder, so the trial `RegisterHotKey` mostly catches
  chords that do reach it but are reserved (F12 and the like); only the
  debug smoke test's probe of Hearsay's own chord (1409) was run, no real
  shell chord was tried. The other named keys (Page Up, Delete, Insert,
  Home, End, Enter, Backspace, Tab, Esc, Num n) stay English: Windows'
  German menus write Entf, Einfg, Pos1, Ende, Bild auf, and those need
  checking on a German Windows before they get keys. Hotkeys stay
  suspended while the window is in the background with a recorder
  listening (it stops on focus loss inside the window, as the Mac's does
  only on disappear). The Shortcuts card needs the owner's hands-on check
  with a real keyboard (section 16): Alt chords, AltGr (reads as
  Ctrl+Alt) on a German layout, and the Win key opening Start on release.
- **Mac help platform class** (18.3, Help row): `HelpWebView.makeWebView()`
  in `mac/Hearsay/Features/Help/HelpView.swift` should add one user script,
  `configuration.userContentController.addUserScript(WKUserScript(source:
  "document.documentElement.classList.add('mac')", injectionTime:
  .atDocumentStart, forMainFrameOnly: true))` (app scripts run with
  `allowsContentJavaScript` off). Until then the Mac relies on the CSS
  default, which shows the Mac passages when `<html>` has no class.
- **Help follow-ups** (W7): the Windows page has no Updates line (the Mac's
  names "the Hearsay menu"; add a Windows one when the Windows update UI
  lands). The Windows-only
  labels the pages quote (window modes, the notification-area status
  toggle, Reveal in Explorer, Move to Recycle Bin…) use Microsoft's terms
  in each language and must match the `shared/localization` entries once
  they are added. A test that the five pages keep identical `<style>`
  blocks, ids and hrefs would catch drift (today a script run by hand).
- **Strings**: "press Return" vs "Enter"; "Trash" vs "Recycle Bin" in the
  reused error text; the new Windows-only keys listed in 18.4 need
  `shared/localization` entries.
- **Shared script-conversion vectors** (18.8, Chinese script row): a
  Mac-generated file both suites run.
- **W5 leftovers**: the Mac's `HEARSAY_REPLAY_SNAPSHOTS`.
- **Polish follow-ups** (found 2026-09-30): `MiddleTrimmedText.Shorten`
  (Models path) is pure and wants a test in `Hearsay.App.Tests`; the CPU
  notice's real path (`TranscriptionEngine.IsCpuRuntimeAsync` returning
  true) was only seen stubbed, since this machine loads Vulkan: check it
  on a PC without a Vulkan driver; `Hearsay.App.Tests` copies
  every Whisper.net native build into its output (it does not import
  `TrimWhisperRuntimes.targets`), harmless for tests.
