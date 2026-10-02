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
| Back-to-back recordings | 2026-09-30 (owner request): Stop hands the recording to a background transcription queue, so the next recording can start at once; Stop & Start Next button and shortcut; final-pass timing "right away (background)" (Mac default) or "when no recording is running" (Windows default); notes sheets wait while recording. Section 4.9, Windows 18.10. |
| Inference | MLX. The Whisper module of `Blaizzy/mlx-audio-swift` (MIT, 1,526 lines, commit `01dec7c9`) is vendored into `mac/HearsayCore/Whisper/` and its decode loop is replaced with a timestamped decoder ported from `mlx_whisper` 0.4.3. Reason: section 15. Direct dependencies become `ml-explore/mlx-swift` and `huggingface/swift-transformers` only. |
| Models | Downloaded on demand from Hugging Face `mlx-community/whisper-*` repos into the app's own model directory. User picks the model. Nothing ships inside the bundle. |
| Audio I/O | AVFoundation. Mic via `AVCaptureSession` + `AVCaptureAudioDataOutput` (16 kHz mono Float32), chosen by CoreAudio UID; `AVAudioFile` for files. No FFmpeg. Changed 2026-09-28: the `AVAudioEngine` tap got no buffers after switching input device, because the input node kept reporting the previous device's rate. |
| System audio | Captured with ScreenCaptureKit (`SCStream`, audio only) and mixed with the mic, so Zoom/Teams/Meet calls are transcribed, not just the room. Decided 2026-09-28. |
| Live transcript | Yes in v1. 30 s chunks are transcribed while recording and shown as a live preview; the final SRT comes from one full pass after Stop. Decided 2026-09-28. Can be turned off in Settings (4.12, owner request 2026-10-02); Auto then detects the language at Stop. |
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
     visible badge (with "No microphone (system audio only)" the start
     fails instead, 4.13).
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
   boundaries. Since 2026-09-30 the pass runs in the background
   transcription queue (4.9), so the next recording can start at once. The UI shows "Finalizing…" with progress. A "Use live
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

### 4.9 Back-to-back recordings (background transcription queue)

Decided 2026-09-30 (owner request): a finished recording no longer blocks
the next one. Before, `RecordingController` held one session from Start
to the end of the final pass (`canStart` was false while transcribing),
so a second meeting straight after the first could not be recorded.
The owner chose items 1, 2, and 4 below and item 3 with two timings only;
"skip the final pass and keep the live preview" was rejected because a
machine whose live preview cannot keep up (or has it off) would end up
with no complete transcript.

1. **Queue.** Stop closes the WAV and hands the session to
   `TranscriptionQueue` as a job; the Record tab is ready for Start at
   once. A job owns what the session owned after Stop: the spool WAV, its
   live segments (and the live tail still being transcribed), its
   language tracker and Chinese script, and its banners (language
   mismatch, Auto fallback, errors). Jobs run first in, first out, one at
   a time, on the one loaded model. The samples are not held in memory:
   a job reads its WAV from the spool when it runs.
2. **Stop & Start Next.** A button on the Record tab and in the menu bar
   panel while recording or paused, and a global shortcut (default
   ⌃⌥⌘N, editable in Settings > General like the others). It stops the
   session (which becomes a job) and starts a new one with the same
   input device, system-audio choice, and language choice (Auto detects
   again for the new session). The gap is the capture restart; measure it
   and write it here.
3. **Final-pass timing** (Settings > General > Transcription, key
   `finalPassTiming`):
   - `immediate` "Right away (in the background)", the Mac default: a job
     starts as soon as it is first in line and the engine has no
     foreground work.
   - `whenIdle` "When no recording is running", the Windows default:
     jobs start only while no session is active (Starting, Recording,
     Paused, Stopping). A job running when a session starts suspends at
     its next window and resumes after the session stops.
   Either way the current session's live chunks and language detection,
   File-mode transcriptions, and History re-runs are foreground work: a
   running job suspends at the next 30 s window while any foreground job
   waits, and resumes when none does. A suspended job keeps its place at
   the head of the line.
4. **Notes do not interrupt.** When a job finishes while a session is
   active, or while a notes sheet or another job's notes flow is on
   screen, no sheet opens: the job's row offers "Generate Notes…" and
   History offers it as usual. Otherwise the notes flow opens as today.
   There is no system notification (it would need a new permission).
5. **Single meeting unchanged.** With one recording and nothing queued
   the Record tab looks and behaves as before: progress, "Use live
   preview instead", the finished card, the notes sheet.
   (Not under "When I start them": there every recording is a queue
   row, 4.11, 2026-10-02.)

**Resumable decoding (Mac).** `Transcriber` gains a checkpoint: the
state its loop carries between windows (`seek`, `allTokens`,
`allSegments`, `promptResetSince`, the resolved language, the initial
prompt length). `shouldYield`, checked where `shouldCancel` is (before a
window, and only after at least one window of this call, so a resumed
job always advances), returns `.suspended(checkpoint)`; calling again
with the checkpoint continues. Suspending and resuming must give the same
segments and text as one uninterrupted call: tested on a multi-window
input made from the fixtures, yielding after every window. The fixture
SRTs stay byte-identical (section 6 unchanged). `WhisperEngine` counts
waiting foreground calls in a lock-protected counter outside the actor
(a foreground call increments it before awaiting the actor), so the
decoder's `shouldYield` can read it without entering the actor.

**Queue rules** (`TranscriptionQueuePolicy` in HearsayCore, pure, with
the vectors in `shared/transcription-queue-tests.json` that Windows runs
too):
- `next(timing, sessionActive, foregroundWaiting, jobs)` returns run
  or resume the first job that is waiting or suspended, suspend the
  running job, or wait.
- `presentsNotes(sessionActive, notesOnScreen)` for a finished job.
- Quit: with jobs waiting, running, or suspended, Quit asks "N
  recordings are not transcribed yet. Hearsay continues with them the
  next time it opens." (Quit / Cancel). Update install refuses while jobs
  exist, like a recording.

**Persistence and recovery.** The queue is written to
`<spool>/queue.json` (job id, WAV name, stop time, language choice,
settled language and script, keep-recording choice, state) and each
job's live segments to `<spool>/<id>.live.srt`, updated as they change.
On launch the jobs in `queue.json` are queued again without asking (a
job that was running starts over). A spool WAV that is in no job and has
no SRT is still an unfinished recording (4.5, unchanged). A job that
fails keeps today's failure rules (4.1): WAV moved to the output folder,
the live preview saved as the SRT, Retry on its row.

**UI.** The Record tab shows the current session on top and, below it,
the queue: one row per job with its name and time, the state (Waiting,
Transcribing N%, Paused while recording, Done, Failed), and its actions
(Use Live Preview Instead, Open Transcript, Generate Notes…, Retry,
Show in Finder, dismiss for Done and Failed rows). The menu bar label
shows the recording status as today and, with no session, the running
job's percentage; the menu bar panel shows one queue line and
Stop & Start Next.

**Debug.** `HEARSAY_REPLAY_FILE=<a.wav>,<b.wav>` replays the files as
consecutive sessions joined by Stop & Start Next and prints each queue
event (queued, running, suspended, resumed, done with the SRT path), so
the queue is checked headless.

**As built (Mac, 2026-09-30).** `TranscriptionQueue`
(`Features/Transcription/TranscriptionQueue.swift`) is owned by
`AppDelegate`; each session's live chunks go through a `LiveSink`, which
moves to the job at Stop with the live task still running.
- Measured gap: in the replay, the next session reaches Recording 5 to
  10 ms after Stop & Start Next (the handover itself; the new session
  starts inside it, so the session-active flag never drops). That run
  uses the replay's fake microphone, so the real device restart
  (AVAudioEngine start to first buffer, and the SCStream restart for
  system audio) comes on top and is not measured yet; check it in item 26
  of section 16.
- The mel spectrogram is recomputed on every step (about 0.1 s for 60
  minutes of audio), so there is no mel cache.
- The final pass reads the spool WAV (16-bit), not the float samples of
  the session, so its input is the same as File mode's for that WAV.
- Auto still undecided at Stop: the whole-recording detection runs right
  after Stop as foreground work, whatever the timing, so the live tail
  (which waits for the language) finishes; a detection attempt still in
  flight at Stop is dropped. A fixed choice's background check runs as
  the job's first step.
- The decoder decodes one window before it can yield, so the queue checks
  `mayRun` again just before each step and puts the job back when a
  session started or foreground work arrived meanwhile.
- With no session and one job (and a timing other than `manual`, 4.11), the
  Record tab shows that job as the single meeting always was (live preview, progress, "Use live preview instead",
  the saved files, the language banner, the notes sheet); otherwise the
  "Transcription queue" section lists every job. A plain Start dismisses
  Done rows whose notes flow already opened (as the finished card was
  cleared); rows offering "Generate Notes…" and Failed rows stay, and
  Stop & Start Next dismisses nothing. Waiting and suspended rows say
  "Paused while recording" while whenIdle holds them. Row buttons reuse
  the existing labels "Try Again", "Reveal in Finder" (folder icon), and
  "Open Transcript" (opens the SRT).
- A capture failure with audio stays on the Record tab as before (WAV and
  live preview kept in the output folder); "Try Again" queues that WAV.
  A job whose WAV is no longer in the spool (a retry after a failure) is
  not written to `queue.json`, so it is not continued after a relaunch;
  its files stay in the output folder. Done and failed entries are
  removed at the next launch.
- Quit: "Stop & Quit" hands the recording to the queue and quits without
  transcribing; with jobs pending, the alert reads "Recordings not
  transcribed yet: N" (count-neutral, the catalogs have no plural rules)
  with "Hearsay continues with them the next time it opens.", Quit /
  Cancel. A running step is cancelled at its next window; the WAVs and
  `queue.json` stay.
- History's Rename is off for a meeting whose notes History is
  generating, and for output-folder stems a job works on: a pending job's
  WAV or saved live preview (a retry) and a job running "Transcribe
  again".
- Notes hand-off: a finished job leaves at most one notes request for the
  Record tab. The request is checked again when the tab takes it (no
  session, no notes flow of any tab on screen); a request that cannot open
  then, one that is still pending when a session starts, and one replaced
  by a newer job's request all turn into "Generate Notes…" on their row.
- Re-run samples: when "Keep the recording" is off, only the newest
  finished job with a language banner keeps its samples in memory for
  "Transcribe again" (about 230 MB per hour); older ones lose the re-run
  and their row says "Could not transcribe again: the recording was not
  kept."
- A checkpoint remembers the model it was decoded with; when another model
  is active at resume, the job starts over (progress back to 0).
- A cancelled language detection (quit, "Use live preview instead")
  settles nothing; only a real detection failure falls back to the Auto
  mode default language.
- While quitting, no new session starts (a Stop & Start Next in progress
  included), no step reads its WAV, and a pass that already finished is
  still written (file writes only; the live tail is not awaited).
- Settings > General > Transcription labels the timing "Transcribe
  finished recordings". The help passage is marked `data-platform="mac"`
  and has a `data-platform="windows"` twin (18.10, WI-4); the README says
  the Windows differences in its Windows section.

### 4.10 Automatic recording of Microsoft Teams meetings

Owner request 2026-10-01: Hearsay should start recording by itself when a
Microsoft Teams meeting starts. Feasible on both platforms without a Teams
account, the Graph API, window-title scraping or any new dependency, so it
is a shared design; **Windows was built first (18.11), the Mac port
followed (2026-10-02, "As built (Mac, 2026-10-02)" below)**. Same setting
key, same rules, same strings.

**Signal.** When Teams joins a meeting or call it opens a capture stream
on its microphone and keeps it running until the call ends, even while
muted (Teams mutes in software; the operating system's microphone
indicator stays on). The operating system exposes who is recording:
Windows as WASAPI audio sessions (`IAudioSessionManager2`,
`IAudioSessionControl2::GetProcessId`, session state), macOS 14.2 and
later as CoreAudio process objects (`kAudioHardwarePropertyProcessObjectList`,
`kAudioProcessPropertyBundleID`, `kAudioProcessPropertyIsRunningInput`).
Teams is `ms-teams.exe` (new Teams, package `MSTeams_8wekyb3d8bbwe`) or
`Teams.exe` (classic) on Windows, bundle `com.microsoft.teams2` (new) or
`com.microsoft.teams` (classic) on the Mac. The detector reads only this
signal; it never looks at Teams' windows, account, or presence.

**Rules** (`MeetingDetector`, pure, ported one for one with its tests):

1. Started: a Teams microphone stream has been active at every observation
   for 2 s (`StartDelay`). Two seconds filter out a device test or a ring
   that is not answered without delaying a real meeting noticeably.
2. Ended: no Teams microphone stream has been active for 15 s (`EndGrace`),
   measured from the last observation that saw one. Teams closes and
   reopens its stream when the user switches microphone or headset; a
   shorter gap must not stop the recording. After Ended, a new Started
   needs the full delay again.
3. Every input device is watched, not only the default: Teams records from
   the communications default, which can differ from the device Hearsay
   records from.
4. Windows hedge: a stream whose own process is not Teams also counts
   when its parent process is Teams (in case the media stack ever opens
   the stream from a helper). The Mac has the bundle id directly.

**Behavior** (`MeetingAutoRecord`, the coordinator between the detector
and `RecordingController`):

- Setting "Record Microsoft Teams meetings automatically"
  (`autoRecordTeamsMeetings`, default off) in Settings > General, in a
  "Meetings" card after Startup, with the caption that the recording uses
  the microphone, system audio and language chosen on the Record tab.
  Off: nothing is probed.
- Second setting (owner, 2026-10-01) in the same card, enabled only while
  the first is on: "Ask which language to use before each automatic
  recording" (`autoRecordAsksLanguage`, default off). Off: the recording
  uses the Record tab's language choice, and Auto detects it. On: when a
  meeting starts, Hearsay brings the main window forward and asks "Record
  this meeting?" with the Record tab's language picker (preselected with
  the current choice), Record and Don't record. Record sets the Record
  tab's language choice to the picked value (it persists, as if picked
  there) and starts the session; Don't record means nothing until the
  next meeting; a meeting that ends while the question is open closes it.
  Never two questions at once, never one while the user is recording.
- Started with no session active: `Start()` as if the user pressed it
  (same device, system-audio and language choices), a notification
  "Recording started / Microsoft Teams meeting" (Windows: a tray balloon
  when the tray icon is shown), and a notice line on the Record tab
  "Recording started automatically for a Microsoft Teams meeting." while
  that session runs.
- Started while the user is already recording: nothing; that session stays
  manual and the meeting's end does not stop it.
- Ended while the automatic session still runs: Stop, exactly as a manual
  Stop (the recording joins the transcription queue, 4.9), and a
  notification "Recording stopped / The Microsoft Teams meeting ended."
- The user stops an automatic session during the meeting: nothing restarts
  until the detector reports Ended and then Started again. Pause does not
  interfere (the meeting's end still stops a paused automatic session).
  Stop & Start Next keeps the new session automatic.
- Start fails (no model, no device, spool error): the Record tab shows the
  usual failure, a notification "Recording not started / Hearsay could not
  start recording for the Microsoft Teams meeting.", no retry until the
  next meeting.
- Turning the setting off while an automatic session runs leaves it
  running; it only stops watching. Debug runs never auto-record.

**As built (Mac, 2026-10-02).**
- **Core** (`mac/HearsayCore`, `swift test`): `MeetingDetector` (rules 1 and
  2, `startDelay` 2 s, `endGrace` 15 s, `nextDeadline`; 18 tests mirroring
  the Windows ones), `MeetingAudioProbe` and `MeetingAudioObserver` (the
  process-object API, macOS 14.2 and later; 3 tests), and
  `MeetingAutoRecordRules`, the decision core of the behavior list above as
  a pure state machine (23 tests). The app holds no rule of its own.
- **Probe.** `MeetingAudioProbe.captureProcesses()` lists every process
  object with its pid, bundle id (`kAudioProcessPropertyBundleID`),
  executable path (`proc_pidpath`) and `kAudioProcessPropertyIsRunningInput`.
  Rule 4's parent hedge is replaced by matching on the Mac: bundle id
  `com.microsoft.teams` or `com.microsoft.teams2`, either also followed by
  `.` and more (new Teams does its calls in a helper, ModuleHost), and, when
  the bundle id does not match, an executable inside an app bundle named
  "Microsoft Teams…". Rule 3 holds for free: the list covers every input
  device.
- **A 2 s poll, listeners and a deadline.** `MeetingAutoRecord`
  (`Features/Recording`, a thin shell on `RecordingController`) reads the
  process list every 2 s while the setting is on, as Windows does, and
  feeds the detector. `MeetingAudioObserver` listens on the process list,
  the device list and every device's `kAudioDevicePropertyDeviceIsRunningSomewhere`
  and only says "something changed", so a change is noticed sooner; a
  one-shot task also re-observes at `detector.nextDeadline`, so the 2 s
  start and the 15 s end fire on time. Fixed 2026-10-02 (owner report: only
  the first meeting after a launch was recorded): the first build relied on
  a listener on each process's `kAudioProcessPropertyIsRunningInput` and had
  no poll. Measured on macOS 27, CoreAudio never calls that listener
  although the property changes (a 0.5 s poll saw 0 → 1, the listener
  stayed silent), so a meeting was noticed only through an unrelated
  process-list change, its end was missed, and the detector stayed in the
  meeting until Hearsay quit. The device listener alone is not enough
  either: while Hearsay records from the microphone Teams uses, the device
  keeps running when Teams stops. Checked with a harness on the same probe,
  detector and 2 s poll, with Hearsay's own record debug entry (silence from
  the Teams virtual device) standing in for Teams: two cycles printed
  started, ended, started, ended. A failed probe is
  logged once (`os.Logger`, category `meetings`) until one succeeds again,
  and counts as no Teams stream, as on Windows. Before macOS 14.2 nothing
  is watched and the Meetings section is hidden.
- **Controller hook.** `RecordingController.phaseObserver` is called after
  every phase change; the shell maps recording, paused and stopping to one
  state so Stop & Start Next never shows an idle gap and the next session
  stays automatic. After performing a start it sends the state once more,
  so a start that did nothing is noticed.
- **Record tab notice.** `RecordingController.automaticStartNotice`, shown
  above the system-audio notice; set and cleared by the coordinator (also
  cleared by a plain Start). Mac difference: it is cleared as soon as the
  setting is turned off during an automatic session (Windows keeps it until
  the session ends); the session itself keeps running either way.
- **Notifications.** `UNUserNotificationCenter`, with the three titles and
  texts of this section. The permission (alert, no sound) is requested when
  the user turns the setting on, and at launch only when the setting is
  already on and the system has never been asked; without it only the
  Record tab notice shows. A delegate presents banners while Hearsay is
  frontmost. Failures are logged, never shown.
- **Language prompt.** A SwiftUI sheet on the main window (the window comes
  forward on the Record tab; `MeetingPromptModel` is the state between the
  coordinator and `MainView`), not a modal alert, so the run loop is never
  blocked. Title "Microsoft Teams meeting started", "Record this meeting?",
  a picker preselected with the Record tab's choice (Auto, then each
  language by its own name, as the Auto mode default language picker), and
  Record (default) and Don't record (Escape, closing the window and the
  sheet going away count as Don't record). "Record" on the button is the
  new catalog key "Record meeting" (English "Record"; translations Aufnehmen,
  Grabar, 錄音, 录音) because the Mac's "Record" key is the tab's name. The
  meeting ending closes the sheet. If another sheet (the permission
  guidance, the unfinished-recording sheet) is open, SwiftUI shows this one
  after it.
- **Settings.** Settings > General > Meetings between Startup and
  Transcription, the two toggles with captions, the second disabled while
  the first is off; hidden on macOS before 14.2.
- **Debug.** `HEARSAY_WATCH_MEETINGS=<seconds>` (`Features/Debug/MeetingWatchDebug.swift`,
  AGENTS.md table) lists the audio processes (pid, bundle id, path, running
  input) and one line per change, and prints `meeting started` /
  `meeting ended`; it polls once a second (a debug tool), records nothing,
  and exits 0 (1 before macOS 14.2). Debug runs never create the
  coordinator.
- **Strings, help, README.** The 15 Windows strings moved from the catalog
  `windows` to the app catalog (the Windows lookups follow, except "Record"
  of the prompt, which stays `windows`); the help item is one shared item
  with a Mac-only sentence (macOS 14.2, one-time notification permission);
  README has a "Teams meetings" paragraph in "Recording".
- **Snapshots.** 36 (Settings > General with the Meetings section, first
  switch on), 37 and 38 (the prompt, Auto and ZH-TW preselected), 39 (the
  Record tab with the notice).
- **Untested.** Detection against a real Teams call: no agent could join
  one, so the bundle ids and helper names are unconfirmed. Section 16 item
  31 is the owner's check with `HEARSAY_WATCH_MEETINGS`; if Teams' input
  stream belongs to a process the matching does not recognize, extend
  `MeetingDetector.isTeams`. The live GUI was not driven (the owner runs
  the Release app).

Other meeting apps (Zoom, Meet in a browser) are out of scope: a browser's
microphone stream does not say which site uses it.

### 4.11 Manual final passes ("When I start them")

Owner request 2026-10-01: some users' computers are too slow to transcribe
while they work on something else. A third value of the 4.9 final-pass
timing lets recordings wait in the queue until the user starts them. A
shared design; **Windows was built first (18.12), the Mac port followed
(2026-10-02, "As built (Mac)" below)**. Same setting key, same rules, same
strings.

**Setting.** `finalPassTiming` gains `manual`, row "When I start them" in
Settings > General > Transcription > "Transcribe finished recordings",
after the two existing rows. The defaults do not change (Mac `immediate`,
Windows `whenIdle`). A stored value a build does not know still reads as
that platform's default.

**Released and held.** Each queued job has an in-memory `released` flag
that only `manual` reads. A job is *held* when the timing is `manual`, it
is pending (waiting, running or suspended) and it is not released.
- Stop (and Stop & Start Next) queues the recording unreleased; with
  `manual` it is held at once. Auto language detection at Stop and the
  live tail still run as foreground work, as in 4.9: they are short, and
  the live preview needs them to finish.
- **Transcribe** on a held row releases that job. **Transcribe All**
  releases every held job. Released jobs run first in, first out, one at
  a time; releasing a later job first does not let it jump an earlier
  released one.
- Released jobs follow `whenIdle`: they run only while no session is
  active and no foreground work waits. A recording that starts (by hand
  or for a Teams meeting, 4.10) suspends the running job at its next
  window, and it resumes after the session stops. Row text "Paused while
  recording", as today.
- **Hold** on a released job (waiting, running or suspended) clears its
  flag. A running job suspends at its next window and keeps its
  checkpoint, so Transcribe later continues where it stopped.
- "Try Again" on a failed row and on a capture failure's card queues the
  job released: the user asked for it.
- Changing the timing to `manual` releases jobs that already started
  (running or suspended); waiting jobs become held. Changing away from
  `manual` ignores the flags, and the other timings' rules apply at once.
- **Not persisted.** `queue.json` does not change. After a relaunch every
  restored job is unreleased, so with `manual` the app never starts a
  pass by itself.

**Queue rules** (`TranscriptionQueuePolicy`, vectors in the `manual`
section of `shared/transcription-queue-tests.json`, which both platforms
run):
- `next` with `manual`: running is allowed when no session is active and
  no foreground work waits. A running job that is not released, or while
  running is not allowed, gets `suspend`. With nothing running and
  running allowed: `resume` the first released suspended job, else
  `start` the first released waiting job, else `none`.
- `shouldYield` with `manual` takes the running job's `released`: true
  when it is not released, or when `whenIdle` would yield.
- `blocksUpdateInstall` takes the timing: held jobs do not block (they
  are in `queue.json` and come back held after the relaunch); any other
  pending job still blocks.
- `quitNeedsConfirmation` still counts every pending job. When all of
  them are held, the alert's second line reads "They stay in the queue
  until you transcribe them." instead of "Hearsay continues with them
  the next time it opens."

**UI.**
- Queue rows: a held job's state reads "Not transcribed yet". A held
  suspended job reads "On hold · N%" and keeps its progress. Held rows
  offer **Transcribe** first. Released pending rows offer **Hold** while
  the timing is `manual`. The other row actions are unchanged ("Use Live
  Preview Instead" stays, the cheap way out for a slow machine).
- The queue card's header gets **Transcribe All** while at least one job
  is held.
- Single meeting (one job, no session): a held job shows its live
  preview, "Not transcribed yet", **Transcribe** and "Use live preview
  instead". Once released it looks as today, with progress, plus
  **Hold**. *Replaced 2026-10-02 by "Queue list always under manual"
  below.*
- Tray or menu bar: the queue line reads "Not transcribed yet · in
  queue: N" when every pending job is held. The menu gets "Transcribe
  All", enabled while a job is held.
- Help and README: one sentence each, next to the timing passage.

**Queue list always under manual** (owner request 2026-10-02). With one
recording the single-meeting view showed "Not transcribed yet" and
Transcribe but no name or time, so the user could not tell which
recording it was. While the timing is `manual` there is no single meeting
(`featuredJob` / `FeaturedJob` is nil): every finished recording is a row
of the "Transcription queue" card (title = meeting name or time, state,
buttons), even the only one, in every state (held, released, running,
done, failed). The other timings keep 4.9 item 5. The rule reads the
timing, so a change at runtime re-renders at once. The state text at the
top right of the Record tab reads as it does for the queue list ("Ready"
with no session), never "Finalizing…" for a held job. The live preview of
a single held job is no longer shown (a row has none); "Use live preview
instead" stays on the row. Pure rule:
`TranscriptionQueuePolicy.showsSingleMeeting(timing, sessionActive, jobCount)`.

**Move to Trash on a held row** (owner request 2026-10-02). A held row
(timing `manual`, not released, state waiting or suspended, no step
running, not saving its live preview) offers **Move to Trash…** (Windows:
**Move to Recycle Bin…**) after its other buttons. It asks first: "Move
this recording to the Trash?" (Windows: "…Recycle Bin?"), "It is not
transcribed, and its live preview is discarded.", **Move to Trash**
(destructive, default; Windows **Move to Recycle Bin**) and Cancel.
Confirming (`TranscriptionQueue.trash(job)` / `Trash(job)`): cancels the
job's language detection, closes and drops its live sink, moves the job's
own WAV to the Trash (Windows: the Recycle Bin) and nothing else (a
retry's WAV in the output folder: that WAV only; its saved live preview
stays), removes `<id>.live.srt`, removes the job, saves `queue.json`,
and evaluates the queue again. A WAV that is already gone (moved or
deleted outside the queue) has nothing to trash: the job just leaves the
queue, as above, without an error (owner report 2026-10-02: a held job
whose WAV had landed in the output folder could not be removed, its row
showing "…the file does not exist"). If the WAV exists but cannot be
moved, the job stays and its row shows "Could not move the recording to
the Trash: %@" (Windows: "…Recycle Bin: %@") with the system reason. The queue guards
too: a no-op unless the job is held and idle
(`TranscriptionQueuePolicy.canTrash(timing, job, busy)`). Queue event
`trashed` for the replay. The menu bar or tray line, Transcribe All and
the quit alert count only the jobs left.

**As built (Mac, 2026-10-02).**
- Core: `FinalPassTiming.manual` (raw `manual`, after `whenIdle`; the Mac
  default stays `immediate`, and a stored value the build does not know still
  reads as the default through `init(rawValue:)`). `TranscriptionQueuePolicy`
  is the C# one for one: `Job.released` (default false), `mayRun`,
  `isHeld(timing:job:)`, `next`, `shouldYield(..., runningReleased:)`,
  `blocksUpdateInstall(timing:jobs:)` (the timing-less overload stays for the
  old vectors), `allPendingHeld(timing:jobs:)`, plus
  `releasedAfterTimingChange(from:to:state:)`, the pure rule for a timing
  switch. `TranscriptionQueuePolicyTests` decodes and runs the `manual`
  section of the shared vectors (33 `next`, 12 `shouldYield`, 12
  `blocksUpdateInstall`, 11 `allPendingHeld`).
- `TranscriptionQueue`/`TranscriptionJob`: in-memory `isReleased`, `isHeld(_:)`,
  `heldCount`, `allPendingHeld`, `canHold(_:)`, `release`, `releaseAll`, `hold`,
  events `held` and `released` (only under `manual`; `held` also for a job
  queued or restored held and for waiting jobs held by a switch to `manual`).
  The decoder's hold flag is (whenIdle or manual) with a session active, or
  manual with a running job that is not released and not saving its live
  preview; it is recomputed from a `didSet` on a job's `state` and
  `isReleased`, and on session and timing changes. The pre-step check is
  `shouldYield(..., runningReleased:)`; held jobs are not polled for;
  `isPausedForSession` is false and `activeJob` skips a held suspended job;
  `blocksUpdateInstall` passes the timing; a job saving its live preview counts
  as running and released. `retry`, `enqueueRetry` and
  `useLivePreviewInstead` release the job. A timing change applies
  `releasedAfterTimingChange` (the previous timing is tracked) and evaluates at
  once. `queue.json` is unchanged; restored jobs are unreleased.
- UI: the picker shows the third row (it iterates `allCases`) and the caption
  is the Windows one. Record tab rows, the queue card header ("Transcribe
  All"), the single meeting and the state text follow 18.12. Menu bar panel:
  the queue line "Not transcribed yet · in queue: N" when every pending job is
  held (before the other rules); "Transcribe All" appears only while a job is
  held (Mac difference: the SwiftUI panel reflows, so the button is left out
  instead of disabled, as Stop & Start Next is). The menu bar label shows no
  activity for a held suspended job. Quit alert: "They stay in the queue until
  you transcribe them." when every pending job is held. Update install uses
  the timing-aware rule.
- Replay: `HEARSAY_REPLAY_TIMING=manual` and `HEARSAY_REPLAY_RELEASE=<s>` as on
  Windows (AGENTS.md). Measured on this Mac (`en-30s.wav` then `de-30s.wav`,
  Auto, whisper-large-v3-turbo fp16): manual with release 2 s: both jobs held at
  queue time (19.1 s and 41.1 s), no pass until Transcribe All at 43.1 s, then
  both SRTs done at 43.6 s and 44.1 s with the same first lines as an immediate
  run; manual without release: no pass, both jobs listed as held, exit 0;
  immediate and whenIdle replays behave as before.
- Strings: the nine 4.11 keys moved from catalog `windows` to the app catalog
  ("When I start them" to core) with their translations; "Either way, the next
  recording can start at once." is gone (neither platform uses it). Help and
  README: the Mac passages got the sentence their Windows twins have.
- Snapshots `29` to `35`: held queue, its menu bar panel, a released job with
  Hold among held jobs, the single held meeting, on hold, released, and Settings
  > General with `manual` selected.
- **Changed 2026-10-02** (the two rules above). Core:
  `TranscriptionQueuePolicy.showsSingleMeeting` and `canTrash`, with
  exhaustive tests in `TranscriptionQueuePolicyTests` (not in the shared
  vectors). `TranscriptionQueue.featuredJob` uses the first, so it is nil
  under manual; `canTrash(_:)` (busy = step task, live-preview save task, or
  saving the live preview) and `trash(_:)` (`FileManager.trashItem`, under
  the output folder's bookmark for a retry's WAV), `TranscriptionJob.trashError`
  (cleared by Transcribe and Transcribe All), event `trashed(id:recording:trashedAs:)`.
  `RecordView`: the held branches of the single-meeting card and of the state
  text are gone (unreachable); `QueueRow` has the button (reusing History's
  "Move to Trash…" and "Move to Trash"), the alert and the error line. Three
  new app-catalog keys, translated with History's Trash wording: "Move this
  recording to the Trash?", "It is not transcribed, and its live preview is
  discarded.", "Could not move the recording to the Trash: %@". Help: the Mac
  sentence about "When I start them" adds "Move to Trash…" (the Windows twin
  is unchanged until Windows has it). Replay: `HEARSAY_REPLAY_TRASH=1`
  (manual only) trashes the first held job right after the last Stop,
  refusing unless its WAV is in the replay's spool under the temporary
  folder, checks the WAV left the spool, and removes its copy from the Trash
  again; the final listing numbers jobs as the events do. Snapshots `32` to
  `34` are now `32-record-queue-single-held` (a row with Transcribe, "Use
  live preview instead" and Move to Trash…), `33-record-queue-single-on-hold`
  and `34-record-queue-single-released` (Hold, no trash); new
  `43-record-queue-trash-failed` (a held row with the error line). Verified:
  `swift test`; the manual replays (`en-30s.wav`, `de-30s.wav`, Auto) without
  release (both held, exit 0), with release 2 s (both done), and with
  `HEARSAY_REPLAY_TRASH=1` (job 1's WAV moved to the Trash and gone from the
  spool, `queue.json` kept for job 2, exit 0); snapshots in en and zh-Hant.
  Not verified: the alert with a real pointer (the owner's item 30).

### 4.12 Live preview setting

Owner request 2026-10-02: a setting that turns the live preview off, so a
recording runs no live chunks at all (less load while the user works on
something else, nothing to read along). A shared design; **the Mac was
built first (2026-10-02)**, Windows follows (18.9 "Live preview
override").

**Setting.** Key `livePreviewMode`, string values `automatic` (default),
`on`, `off`; a stored value a build does not know reads as `automatic`.
On the Mac `automatic` and `on` behave the same (Apple Silicon needs no
speed probe); only Windows tells them apart. The Mac shows a switch, first
in Settings > General > Transcription: "Show the live preview while
recording" (on stores `automatic`, off stores `off`; a stored `on` shows
as on, and turning it off and on again stores `automatic`), with the
caption "Transcribes as you record, so you can read along. When off, the
transcript is made only after you stop, and Auto detects the language
then. Applies from the next recording."

**Off.** The mode is read once at each session's Start (Stop & Start Next
reads it again); a change during a session does not affect it. With the
preview off the session opens no live queue, cuts no live chunks, and
runs no language detection during the recording (both need the live model
location). The Record tab's live area says "Live preview is off. Turn it
on in Settings > General."; nothing says "Detecting language…" during the
session, and the menu bar shows no live line. Stop hands the job over with
`liveEnabled` false, so "Use live preview instead" is not offered, and an
undecided Auto language is detected over the whole recording at Stop
(4.9, `settleEarly`). The fixed-language path (and its background check),
the final pass, and the failure rules are unchanged; a capture failure
keeps the WAV and has no live preview to save.

**As built (Mac, 2026-10-02).**
- `LivePreviewMode` and `AppSettings.livePreviewMode` in HearsayCore
  (`Settings/AppSettings.swift`), with tests for the default, the round
  trip, an unknown value, the shared raw values, and the toggle mapping.
- `RecordingController.startLivePreview()` checks the mode before the model
  location; `isLivePreviewTurnedOff` marks the session, and the off notice
  goes through `liveNotice` (nothing else overwrites it while no live chunk
  runs). The Record tab shows it as the only line of the "Live preview"
  section, without the empty transcript box. The notice is not handed to
  the queue job: after Stop it explains nothing the job shows.
- `LivePreviewToggle` in `SettingsView.swift`; the three strings are in the
  app catalog with translations in all four languages.
- Replay: `HEARSAY_REPLAY_LIVE=off` (AGENTS.md). Measured on this Mac
  (`en-30s.wav` then `de-30s.wav`, whisper-large-v3-turbo fp16, timing
  immediate): Auto with the preview off ran 0 live jobs; job 1 settled `en`
  3.1 s and job 2 `de` 0.5 s after their Stop, and both SRTs' first lines
  equal the run with the preview on (2 live jobs). EN with the preview off:
  0 live jobs, both jobs `chosen` en, job 2 suggests de as before.
- Snapshots `40-settings-general-live-preview-off`,
  `41-record-live-preview-off` (a sample recording in Auto, through
  `RecordingController.showSampleRecording(elapsed:)`, snapshots only) and
  `42-menu-bar-live-preview-off`.
- Help: one Mac sentence in the live preview step of each Help.html;
  README: one sentence under "Live preview".
- Not verified: the live GUI (the owner runs the Release app), section 16
  item 32.

### 4.13 System audio only

Owner request 2026-10-02: record only the sound the computer plays (a
video, a webinar, a call heard through the speakers) without the user's
microphone. A shared design; **the Mac was built first (2026-10-02)**,
Windows follows (18.9 "System audio only").

**Rules.**
1. The Microphone picker gets a last row, after a divider, "No microphone
   (system audio only)". Like the device choice it is not persisted: each
   launch starts with the default microphone (a forgotten "off" would
   silently lose the user's own voice).
2. While it is selected, "Also capture system audio" shows on and
   disabled; the stored `captureSystemAudio` is not changed (choosing a
   microphone again shows the stored value). Effective system audio = true.
3. No input device at all: the picker selects the no-microphone row by
   itself (it replaces the old "No input device" row), and Start records
   system audio only. When a device appears later and the row was chosen
   automatically, the picker switches back to the default device; a row
   the user picked stays. The choice never changes during a session.
4. Start without a microphone requests no microphone permission, makes no
   microphone recorder, and mixes system audio alone. If system audio
   cannot start (permission denied or an error), the start fails with
   "Nothing to record: the microphone is off and system audio could not
   start: <reason>"; the permission case offers "Open System Settings"
   (Screen & System Audio Recording). Nothing is left in the spool.
5. During a system-only session pause and resume act on system audio
   alone; the no-audio watchdog (a microphone check) does not run; system
   audio ending on its own ends the recording like an unplugged microphone
   (WAV kept, 4.1 failure rules) instead of continuing mic-only; a
   recording with no samples at Stop fails with "No audio arrived from
   system audio." The main meter shows the system level and the Mic meter
   is hidden; the silence warning reads "Silent for Ns — check that
   something is playing".
6. Stop & Start Next keeps system-only for the next session (also an
   automatic no-microphone choice); Teams auto-recording (4.10), the
   hotkeys and the menu bar Start use the same choice. The menu bar panel
   names no device, so it needs no change.
7. Debug replay: `HEARSAY_REPLAY_MIC=off` feeds the replay WAV as the
   system source and makes no microphone.

**As built (Mac, 2026-10-02).**
- Core: `MicrophoneSelection` and `MicrophoneChoice`
  (`HearsayCore/Audio/MicrophoneSelection.swift`): the choice, the
  automatic flag, `update(deviceUIDs:defaultUID:keepingNoMicrophone:)`
  (rules 1, 3, 6) and `capturesSystemAudio(stored:)` (rule 2); 11 tests
  in `MicrophoneSelectionTests`. `MixerTests.mixStreamsWithSystemOnly`
  checks that system audio alone passes through unchanged, has no mic
  level, and that a gap in its stream is filled with silence.
- `RecordingController`: `microphone` replaces `selectedDeviceUID`;
  `refreshDevices()` updates the choice only while no session is active,
  and once more when a session ends; `performStart(continuing:)` settles
  the choice first (Stop & Start Next keeps "no microphone"), then asks
  for the microphone permission only with a microphone.
  `startSystemAudio()` returns started / denied / failed and the caller
  decides: a notice with a microphone, `failNothingToRecord` without one
  (`failureOpensScreenCaptureSettings` shows "Open System Settings" in the
  error). `sessionRecordsMicrophone` drives the meters and the silence
  warning. `CaptureSources.makeSystemAudio` takes `withoutMicrophone`, so
  the replay knows to feed its WAV there.
- `RecordView`: the picker row (tag `MicrophoneChoice.noMicrophone`), the
  locked switch, no Mic meter (and no empty meter row before a
  no-microphone session).
- Strings: four new app keys with translations ("No microphone (system
  audio only)", the start error, the zero-sample error, the silence
  warning). "No input device" left the Mac; it moved to the catalog
  `windows` (Windows still shows it until its 18.9 item, `Strings.cs`
  `Win(...)`, resw regenerated).
- System audio during silence: not measured in this change (needs the
  Screen & System Audio Recording permission and real playback).
  18.4 records that ScreenCaptureKit streams continuously; if it ever
  delivers nothing during silence, the mixer fills the gap when the next
  buffer arrives (tested), so timestamps stay right, but the elapsed time
  and meters would pause meanwhile and a silent tail before Stop would be
  missing.
- Verified: `swift test` (547 tests), the Release build with no warnings,
  replays of `en-30s.wav` then `de-30s.wav` in Auto with
  `HEARSAY_REPLAY_MIC=off` and without it: identical results (2 live
  jobs, job 1 `en` and job 2 `de` detected, same final-pass cue counts and
  first lines, 0.011 s session gap), snapshots `44-record-no-microphone`
  and `45-record-system-audio-only` in English and Traditional Chinese.
- Not verified: a real system-only recording through ScreenCaptureKit, the
  denied-permission start error, and the device auto-switch with real
  hardware (section 16 item 33).

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
- Final-pass timing: right away in the background (Mac default) / when
  no recording is running (Windows default), section 4.9 / when I start
  them (`manual`), section 4.11 (Windows 18.12, Mac 4.11 "As built (Mac)").
- Global shortcut for Stop & Start Next (default ⌃⌥⌘N), section 4.9.
- Record Microsoft Teams meetings automatically (`autoRecordTeamsMeetings`,
  default off) and ask which language to use before each automatic
  recording (`autoRecordAsksLanguage`, default off), section 4.10. Windows
  first (18.11), Mac 2026-10-02 (4.10 "As built (Mac, 2026-10-02)"); the
  Mac hides it before macOS 14.2.
- Show the live preview while recording (`livePreviewMode`, default
  `automatic`; the Mac's switch stores `automatic` or `off`), section 4.12.
  Mac 2026-10-02; Windows adds Always on (18.9).

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

26. Mac, back-to-back recordings (section 4.9, added 2026-09-30): record
    a minute, press Stop & Start Next (button, then ⌃⌥⌘N), and record
    another minute. The first recording shows in the queue as
    Transcribing while the second records, its live preview keeps up,
    and no notes sheet opens until you stop. Stop the second; it finishes
    after the first and its notes sheet opens. Switch Settings > General
    to "When no recording is running" and repeat: the first job waits
    ("Paused while recording") until the second stops. Quit with a job
    waiting, reopen, and check it resumes.

27. Windows, back-to-back recordings (section 18.10, added 2026-09-30): with
    a real microphone record a minute, press Stop & Start Next (button, then
    Ctrl+Alt+Win+N), and record another minute; the gap between the two
    recordings is the device restart (WASAPI start to the first buffer, and
    the loopback capture with "Also capture system audio"): note it in
    18.10. In "When no recording is running" (the default) the first
    recording waits until the second stops; switch to "Right away" and
    check the live preview of the second keeps up while the first
    transcribes. Quit with a job waiting ("Recordings not transcribed yet:
    N"), reopen, and check it continues. Right-click the tray icon while a
    job waits and while recording: the queue line ("Transcription paused
    while recording · in queue: N") sits under the state line, and Stop &
    Start Next is enabled only while recording or paused (WI-4 could only
    check the menu's inputs).

28. Windows, Teams meetings (section 4.10 and 18.11, added 2026-10-01): no
    agent could join a Teams meeting, so detection against real Teams is
    unverified. First, with Teams signed in, run
    `$env:HEARSAY_WATCH_MEETINGS=90; .\Hearsay.exe` from a second copy of
    the build (never the installed one), start a Teams call or "Meet now",
    and check the output lists an Active session for `ms-teams` and prints
    `meeting started` about 2 s later and `meeting ended` about 15 s after
    hanging up. If the session's process is not `ms-teams` (or its
    parent), note the name in 18.11 and the detector's name list grows.
    Then turn on Settings > General > Meetings in the installed app, join
    a meeting: the tray balloon and the Record tab notice appear, both
    meters move, hanging up stops the recording after the grace period and
    the transcription queue takes it. Switch headsets during a meeting:
    the recording must not stop. Stop manually mid-meeting: nothing
    restarts until the next meeting.

29. Windows, "When I start them" (section 4.11 and 18.12, added 2026-10-01):
    in Settings > General > Transcription choose "When I start them", then
    record twice with Stop & Start Next. Both rows on the Record tab say
    "Not transcribed yet", "Transcribe All" sits in the card's header, and
    nothing is transcribed (the CPU stays idle) even after the recording
    stops. Click Transcribe on the second row: it runs, the first stays
    held (releasing the later one first still does not let it jump an
    earlier released one). Click Hold on the running row mid-way: it reads
    "On hold · N%" and stops at its next window; Transcribe continues where
    it stopped. Click Transcribe All: both finish in order. Start a new
    recording while a released job runs: it reads "Paused while recording"
    and resumes after Stop. Quit with held jobs: the alert says "They stay
    in the queue until you transcribe them."; reopen and check they are
    still held (nothing starts by itself). Right-click the tray icon: the
    line reads "Not transcribed yet · in queue: N", Transcribe All is
    enabled only while a job is held, and it releases them. With one
    recording only, the single meeting shows "Not transcribed yet",
    Transcribe and "Use live preview instead", then Hold once released.

30. Mac, "When I start them" (section 4.11, added 2026-10-02): the same walk
    as item 29 in the Mac app: Settings > General > Transcription > "When I
    start them"; record twice with Stop & Start Next; both rows read "Not
    transcribed yet" and nothing is transcribed; Transcribe on the second row
    only runs it; Hold mid-way reads "On hold · N%"; Transcribe All finishes
    both; start a recording while a released job runs ("Paused while
    recording"). The menu bar panel line reads "Not transcribed yet · in
    queue: N" and Transcribe All shows only while a job is held. Quit with
    held jobs: "They stay in the queue until you transcribe them."; reopen
    and check they are still held. Switch the timing back to "Right away"
    with held jobs: they start at once.
    Added 2026-10-02: with "When I start them" and a single recording, the
    Record tab shows it as a row of the "Transcription queue" card with its
    time (or meeting name), "Not transcribed yet", Transcribe and Move to
    Trash… (no separate single-meeting card); switching to "Right away"
    with that one recording shows the single meeting again. Click Move to
    Trash… on a held row: the alert "Move this recording to the Trash?"
    appears; Cancel keeps it; Move to Trash removes the row, the WAV is in
    the Finder's Trash, the menu bar line and the quit alert count one less,
    and after a relaunch the recording does not come back. Move to Trash…
    is not offered on a released or running row.

31. Mac, Teams meetings (section 4.10, added 2026-10-02): no agent could
    join a Teams meeting, so detection against real Teams is unverified.
    First, with Teams signed in, run
    `HEARSAY_WATCH_MEETINGS=90 .build/derived/Build/Products/Release/Hearsay.app/Contents/MacOS/Hearsay`
    from a second copy of the build (never the running one), start a Teams
    call or "Meet now", and check the output lists a Teams process (bundle id
    `com.microsoft.teams2` or a helper below it) as `running input` marked
    `Teams`, prints `meeting started` about 2 s later and `meeting ended`
    about 15 s after hanging up. If the process listed as running input has
    another name, note it in 4.10 and extend `MeetingDetector.isTeams`. Then
    turn on Settings > General > Meetings > "Record Microsoft Teams meetings
    automatically" in the real app (macOS asks once for permission to show
    notifications; allow it), join a meeting: the notification and the Record
    tab notice appear, both meters move, hanging up stops the recording
    after the grace period and the transcription queue takes it. Switch
    headsets during the meeting: the recording must not stop. Stop manually
    mid-meeting: nothing restarts until the next meeting. Turn on "Ask which
    language to use before each automatic recording" and join another
    meeting: the main window comes forward with the "Microsoft Teams meeting
    started" sheet; Don't record leaves the meeting alone, Record starts it
    in the picked language (the Record tab's choice changes to it); Escape
    counts as Don't record; with the sheet open, hang up and check it
    closes. The second toggle is greyed out while the first is off.

32. Mac, live preview setting (section 4.12, added 2026-10-02): turn off
    Settings > General > Transcription > "Show the live preview while
    recording" in the real app and record a minute in Auto: the live area
    says "Live preview is off. Turn it on in Settings > General.", no
    "Detecting language…" appears, the menu bar panel shows no live line,
    and after Stop the row has no "Use live preview instead" and settles
    the language before its pass. Turn the switch on during a recording:
    the running session stays without preview; Stop & Start Next starts the
    next one with it.

33. Mac, system audio only (section 4.13, added 2026-10-02): on the Record
    tab pick "No microphone (system audio only)": "Also capture system
    audio" shows on and greyed out. Play a video and record a minute: only
    the System meter shows and moves, the transcript has the video's
    speech and none of the room. Pause and Resume once (the paused time is
    left out), then Stop & Start Next: the next session is system-only
    too. Pick the microphone again: the switch shows its old value. Quit
    and reopen: the default microphone is selected again. Without any
    microphone (no built-in one, or all input devices off in Audio MIDI
    Setup), the picker shows the no-microphone row by itself, Start
    records system audio, and connecting a headset selects it again. Turn
    Hearsay off in System Settings > Privacy & Security > Screen & System
    Audio Recording, pick the row and press Start: the error "Nothing to
    record: …" with "Open System Settings", and nothing new in the spool.
    Also check whether the elapsed time keeps counting while nothing
    plays (4.13, "System audio during silence").

## 17. Polish list (found during review, not yet scheduled)

- **Open (owner report 2026-10-02): a held job's WAV left the spool.**
  With "When I start them", a recording made 23:37:18 to 23:39:30 stayed in
  `queue.json` as a waiting spool job, but its WAV was in the output folder
  (no SRT next to it; the file's change time equals its last write, so it
  moved while recording or at Stop). "Move to Trash…" then failed with "the
  file does not exist". The owner could not recall the steps; neither
  `keepAfterFailure` (no live SRT was written) nor the recovery sheet
  (launch date known) explains it. Mitigated in v0.3.11: a missing WAV just
  removes the job (4.11). If it happens again, note what was on screen
  around Stop (Teams auto-recording, a dialog, the File or History tab).

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

- **Done 2026-10-02: Mac port of section 4.10, automatic recording of
  Microsoft Teams meetings.** Built as described in 4.10 "As built (Mac,
  2026-10-02)": `MeetingDetector`, the CoreAudio probe and listeners, and
  the decision core `MeetingAutoRecordRules` in HearsayCore (44 tests), the
  `MeetingAutoRecord` shell, the Record tab notice, notifications through
  `UNUserNotificationCenter`, the language sheet, Settings > General >
  Meetings, `HEARSAY_WATCH_MEETINGS`, snapshots, help, README, and the 15
  strings moved from catalog `windows` to the app catalog. Mac differences
  from Windows: bundle id and path matching instead of the parent hedge;
  a 2 s poll plus listeners and a deadline re-observation (fixed the same
  day: the first build had no poll and missed every meeting after the
  first); the notice
  clears when the setting is turned off; notifications ask for permission
  when the setting is turned on; a sheet instead of a dialog; the section is
  hidden before macOS 14.2. Verified: `swift test`, the Release build, the
  debug entry against the live audio process list, and the UI snapshots in
  English and German. Not verified: a real Teams call and the live GUI
  (the owner runs the Release app): section 16 item 31.

- **Done 2026-10-02: Mac port of section 4.11, manual final passes ("When I
  start them").** Built as described in 4.11 "As built (Mac)": `manual` in
  `FinalPassTiming` and the policy (the shared `manual` vectors run in
  `swift test`), release, hold and release-all in `TranscriptionQueue`
  with the timing-change rule and Try Again queuing released, the picker row,
  the Record tab rows, single meeting and Transcribe All, the held menu bar
  line, the quit alert variant, replay support, help, README and snapshots.
  Mac differences from Windows: Transcribe All is left out of the menu bar
  panel while no job is held instead of disabled; the queue is not unit tested
  (it lives in the app target), so its rules are covered by the policy tests
  and the replay runs. Verified: `swift test` (all green), the Release build,
  replays (manual with and without release, immediate, whenIdle) and the UI
  snapshots in English and German. The live GUI was not driven (the owner
  runs the Release app): a hands-on pass is item 30 in section 16.

- **Done 2026-10-02: Mac live preview setting (section 4.12, owner
  request).** Settings > General > Transcription > "Show the live preview
  while recording" (key `livePreviewMode`, shared with Windows); off runs
  no live chunks and no in-session detection, says so in the live area,
  and Auto detects at Stop. Built as in 4.12 "As built (Mac, 2026-10-02)";
  verified with `swift test`, the Release build, replays (Auto and EN with
  the preview off, Auto with it on) and the UI snapshots in English and
  Traditional Chinese. Windows: 18.9 "Live preview override". Hands-on:
  section 16 item 32.

- **Done 2026-10-02: Mac system audio only (section 4.13, owner
  request).** The Microphone picker's last row "No microphone (system
  audio only)" records only the sound the Mac plays; it is chosen by itself
  when no input device exists and is never persisted. Built as in 4.13 "As
  built (Mac, 2026-10-02)"; verified with `swift test`, the Release build,
  replays with and without `HEARSAY_REPLAY_MIC=off`, and the UI snapshots
  in English and Traditional Chinese. Windows: 18.9 "System audio only".
  Hands-on: section 16 item 33.

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
- **Fixed 2026-10-01 (Windows): pages off-center after a resize.** A
  StackPanel with `MaxWidth` and Stretch inside a ScrollViewer sat off-center
  on the Record, File and Models tabs (Settings by a few pixels). Those tabs
  and Settings now host the page in `Settings/CenteredColumn.cs`, which
  arranges it min(width, max) wide and centered; `HEARSAY_UI_SNAPSHOTS`
  renders every tab wide, back at the default and narrow to check it.

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
  sticks until relaunch. Build the shared setting of section 4.12 (key
  `livePreviewMode`, built on the Mac 2026-10-02): Windows shows a picker
  in Settings > General > Transcription, "Live preview: Automatic / Always
  on / Off", where Automatic follows the speed probe, Always on ignores it
  and lets the preview lag on a slow machine, and Off behaves as on the
  Mac (no live chunks, Auto detects at Stop). Reuse the Mac's strings for
  the Off behavior: the notice "Live preview is off. Turn it on in
  Settings > General." and the caption sentence about Stop (the Mac
  caption's second sentence). Also re-run the probe when a recording
  starts more than a few minutes after the cached result, or when it was
  measured under load. First confirm from the log (`whisper: runtime …`
  and `whisper: speed probe …` lines, visible when Hearsay.exe is started
  with stdout redirected) whether the GUI launch loaded Vulkan or fell
  back to CPU.
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

- **Queue UI follow-ups** (found 2026-09-30, WI-4): at a 480 px window the
  German Record card clips the label "Sprache" beside the language picker
  (`LanguageChoicePicker` does not wrap); a failed queue row has Try Again
  but not "Open Models" or "Transcribe this file" (the Mac's row has neither
  either; the single-meeting card has both); the tray menu's queue line and
  Stop & Start Next were never seen in the real popup (item 27 of section 16).

- **System audio only** (owner, 2026-10-02; shared design 4.13, built on
  the Mac first): the Windows Microphone picker gets the same last row,
  "No microphone (system audio only)" (reuse the shared key and its
  translations), replacing "No input device" (then drop that `windows`
  key). With it the session runs WASAPI loopback only (no microphone
  client, no microphone permission check), the system-audio switch shows
  on and disabled without changing the stored setting, the choice is not
  persisted, and the no-device rule is the same (chosen by itself, back to
  the default device when one appears unless the user picked it). Failure
  rules as on the Mac: a loopback that cannot start fails the start with
  "Nothing to record: the microphone is off and system audio could not
  start: <reason>"; Windows has no screen-recording permission, so there
  is no "Open System Settings" button and the reason is the loopback
  error (for example no output device, `NoOutputDevice`). Loopback ending
  on its own ends the recording with the WAV kept; zero samples at Stop
  reads "No audio arrived from system audio." The 0.1 s silence filler
  (18.4) keeps a system-only recording running while nothing plays. Same
  replay variable `HEARSAY_REPLAY_MIC=off`, same Stop & Start Next and
  Teams behavior, Mic meter hidden.

### 18.10 Back-to-back recordings (port of section 4.9, added 2026-09-30)

The Mac builds this first; Windows ports it after. Same behavior as 4.9
unless listed here. Shared contract: `shared/transcription-queue-tests.json`
(queue decisions, notes presentation, quit and update blockers), the
`finalPassTiming` setting (`immediate` | `whenIdle`), the strings in
`shared/localization`, and the `queue.json` fields in 4.9 (Windows keeps
its own file under `%LOCALAPPDATA%\Hearsay\Recording\`, same fields).

- **Default timing `whenIdle`.** This dev machine (18.6) cannot run a
  final pass and the live preview together, and on a PC whose live
  preview is off (the speed gate above) `immediate` would still slow the
  capture machine down. `immediate` stays available.
- **Suspend and resume on whisper.cpp (as built, 2026-09-30).** whisper.cpp
  cannot continue a decode from saved state, and the engine drives the C API
  (W5 option b), not Whisper.net's `ProcessAsync`. `WhisperEngine.TranscribeStep`
  takes a `shouldYield` callback and an optional `TranscriptionCheckpoint`.
  `shouldYield` is asked where cancellation is checked (before a silence-gate
  speech run, and in whisper.cpp's encoder-begin callback before every 30 s
  window), cancel wins, and only after at least one window of the call has
  begun, so every call advances (Mac rule). On yield the call returns, it does
  not throw, a `TranscriptionStep` whose `Suspended` holds the checkpoint; the
  window that was about to start is not decoded (the abort is at encoder
  begin, so no partial window is lost; the CPU abort callback still only
  serves cancellation). The checkpoint holds the segments so far (silent cues
  dropped), `ResumeSeconds` (the end of the last complete segment, or, when
  the windows decoded in the current speech run produced no segment, the
  run's start plus 30 s per window begun, capped at the run's end, so a
  resumed job advances even over music or noise; when the
  pass stopped between two speech runs, the start of the next run, so a
  finished run's silent tail is not decoded again), the resolved language
  (not detected again), the full path of the model (`IsForModel`; on another
  model the engine throws `TranscriptionCheckpointException` with
  `ModelMismatch` and the queue starts the job over, as on the Mac) and a
  fingerprint of the samples and the options (mismatches throw too).
  Resume slices the speech run that contains the offset to begin one second
  before it and passes `offset_ms` to `whisper_full`, rather than slicing
  exactly at the offset: a bare slice makes the log-mel frames at its left
  edge use reflect padding, which made the silent tail of a run hallucinate
  ("on.", "Thank you."); with the one-second pre-roll the frames at the
  offset are identical to an uninterrupted pass. Segment times are shifted by
  the slice start, so they stay absolute, and the silence gate is computed on
  the whole input, so its 30 s grid and its cue rule do not move. Merging
  (`CheckpointMerge`) keeps the checkpoint's cues that start before the
  offset and drops a first new cue that ends at or before it. Progress is
  for the whole input: a resumed call first reports the checkpoint's
  fraction. `TranscribeStep` does not change `Transcribe`, which is the same
  code without `shouldYield`; the fixture outputs are unchanged. Measured on
  Vulkan, turbo q5_0, yielding at every window boundary of an 82 s input (four
  fixture clips, one speech run) and of a 159 s input (two speech runs, a
  between-run yield): normalized text similarity to an uninterrupted pass
  1.0000 in both (needs at least 0.98), no duplicated, overlapping or missing
  cue at any resume point. The cue count can differ (14 vs 18 on the 82 s
  input): whisper.cpp splits a window into cues by its own timestamp tokens,
  and a window that begins at the offset can split differently from one that
  follows another window in the same call; the text is the same. A resume
  recomputes the mel of one second of audio before the offset.
- **Foreground counter.** Same rule as the Mac: live chunks, detection,
  File mode, and History re-runs are counted while they wait for the engine,
  and the running job checks the counter before a run and a window. On
  Windows the marking is explicit, not per engine method:
  `using var fg = engine.EnterForeground();` before the call (the same
  `Transcribe`/`DetectLanguage` methods serve foreground and background
  callers, where the Mac's background step is a separate method), and
  `engine.ForegroundWaiting` reads the lock-free count. The queue passes
  `shouldYield: () => engine.ForegroundWaiting > 0 || queueSaysSuspend()`.
- **Shortcut.** Stop & Start Next is `RegisterHotKey` like the other two,
  default Ctrl+Alt+Win+N (the Windows equivalents of ⌃⌥⌘N), editable in
  the shortcut recorder.
- **Tray.** The tray tooltip shows the recording status and, with no
  session, the running job's percentage; the tray menu gets Stop & Start
  Next.

**As built (Windows, WI-1 Core, 2026-09-30).**
- `Hearsay.Core/Transcription/TranscriptionQueuePolicy.cs`: `FinalPassTiming`,
  `TranscriptionJobState` and the pure `TranscriptionQueuePolicy`; the tests
  run all 71 vectors of `shared/transcription-queue-tests.json`, invalid
  cases included.
- `Hearsay.Core/Audio/TranscriptionQueueManifest.cs`: `TranscriptionQueueManifest`
  and `TranscriptionQueueStore` for `queue.json` and `<id>.live.srt` in the
  spool folder, the Mac's JSON fields and values (whole-second UTC
  `stopTime`, every key written, unknown or bad entries dropped one by one,
  running and suspended jobs reload as waiting). Writes use
  `OutputWriter.WriteReplacing` (temp file, then replace), as the Mac does. Differs from the Mac: a job id or
  WAV name must also be free of `\`, `:` and the other characters Windows
  forbids in a file name.
- `RecordingSpool.UnfinishedRecordings` leaves out WAVs of pending queued
  jobs (names compared without regard to case).
- `AppSettings.FinalPassTiming` (default `WhenIdle`, unknown stored value
  reads as the default) and `StopStartNextHotkey` (default Ctrl+Alt+Win+N),
  keys `finalPassTiming` and `stopStartNextHotkey`. The Core layer has no
  other list of hotkeys; registration and the Settings rows are WI-4. The
  picker labels are the app's (no Core string is surfaced).

**As built (Windows, WI-3 App, 2026-09-30).**
- `Features/Transcription/TranscriptionQueue.cs` (`TranscriptionQueue`,
  `TranscriptionJob`, `RecordingHandover`, `QueueEvent`, `NotesRequest`) is
  the port of `TranscriptionQueue.swift`; `LiveSink.cs` and the output half
  of `TranscriptOutput.cs` (`SaveTranscript`, `KeepAfterFailure`,
  `PendingRecording`) are the ports of `LiveSink.swift` and
  `TranscriptOutput.swift`. `AppShell` owns the queue (`Queue`) next to the
  controller and calls `Queue.Restore()` at launch. The queue takes an
  `IQueueEngine` (step, detection, whole pass; `TranscriptionEngine`
  implements it, a fake does in the tests), a model-path function and a WAV
  loader, so the tests need no model.
- Threading: the queue, its jobs and the controller are UI-thread objects.
  Every continuation returns to the UI context (`ConfigureAwait(true)`),
  `PropertyChanged` and `Changed` are raised there only, and a test UI
  thread (a dedicated thread with a `SynchronizationContext`) checks it. The
  decoder's `shouldYield` runs on the engine's thread and reads only
  `engine.ForegroundWaiting` and a volatile hold flag (timing is whenIdle and
  a session is active). The Record tab, the tray and Settings bind to the
  queue's `Jobs`, its per-job and queue `PropertyChanged` and the catch-all
  `Changed` (WI-4).
- Foreground marking is in `TranscriptionEngine`, not at each call site: its
  `TranscribeAsync` and `DetectLanguageAsync` take `EnterForeground()` before
  they wait for the engine (live chunks, session detection, File mode, the
  queue's language settling and "Transcribe again" all go through them),
  and `TranscribeStepAsync`, the queue's only background call, does not.
  The queue checks `MayRun` again just before every step and puts the job back
  ("suspended", or waiting when it has no checkpoint) when a session started
  or foreground work arrived while the samples were read or the language
  detected.
- Differences from the Mac: the queue also closes every job's live sink at
  quit (the engine's dispose waits for the running chunk, so a long live
  tail would hold the exit; the live segments so far are saved, and the next
  launch's job has no live tail); `NotesRequested` is an event that
  `MainWindow` answers at once by taking the request (the queue re-checks it
  then), because the notes panel exists whether or not the Record tab is
  showing, where the Mac's tab takes it in `onAppear`;
  `NotesFlowViewModel.IsAnyRunning` is the weak tracker of every flow
  (Record, File and History); the tray keeps its `Transcribing` phase and
  `AppShell` maps the running job's percentage into it while no session is
  active (the tray menu and tooltip wording are WI-4); the finished card shows
  "Generate Notes…" only when the job offers it (the Windows Record tab
  used to show it always).
- Record tab (WI-3 scope): the session on top, the single job as the single
  meeting (live preview, progress, "Use live preview instead", language
  banner, saved files, Try Again, notes), a capture failure's card with
  Try Again (queues the kept WAV), and Stop & Start Next next to Stop while
  recording or paused (tooltip with the shortcut). The queue list, tray,
  Settings, snapshots, help and README are WI-4 below.
- Hotkeys: `HotkeyAction.StopStartNext = 3` is registered with the other two
  (probe id moved to 100); two actions bound to the same chord fail at
  `RegisterHotKey` and show in `RegistrationError`.
- Quit: "Stop & Quit" hands the recording to the queue and quits without
  transcribing; pending jobs give "Recordings not transcribed yet: N" /
  "Hearsay continues with them the next time it opens." with Quit / Cancel; a
  running step or re-run is cancelled at its next window without a question;
  `UpdateService.InstallBlocker` says "Wait until the transcriptions are
  finished." while jobs are pending. A debug run quits without the question.
- Replay: `HEARSAY_REPLAY_FILE=<a>,<b>[,...]` as on the Mac, with
  `HEARSAY_REPLAY_TIMING=immediate|whenIdle|manual` (default `immediate`, the
  Mac's replay default; the setting's own default stays whenIdle; `manual` and
  `HEARSAY_REPLAY_RELEASE` are in 18.12); the Windows
  replay also prints each job's SRT to stdout between `--- srt (queue job N)
  ---` lines, because the scratch folder is removed at exit.
- Measured (2026-09-30, `en-30s.wav` then `de-30s.wav`, language Auto, turbo
  q5_0 on Vulkan, fake microphone, the replay's own scratch folders): the
  next session reaches Recording 68 ms (immediate) and 53 ms (whenIdle)
  after Stop & Start Next, which includes the mixer drain, closing the WAV,
  the hand-over, the device list refresh and the thread-pool hops of the
  start; the session-active flag does not drop. immediate: job 1 ran at once
  (detection, then put back for 7 s because the last live chunk of session 1
  was foreground work, then its pass), finished 20 s after the Stop & Start
  Next and 1.5 s before session 2 ended; job 2 finished 21 s after the last
  Stop. whenIdle: job 1 did nothing but settle its Auto language (foreground
  detection, 5 s after Stop) until session 2 stopped, then both passes ran
  one after the other, 27 s after the last Stop. Both exit 0, and the
  final SRTs have the same text. The real device restart (WASAPI start to the
  first buffer, and the loopback capture for system audio) comes on top and
  is not measured: item 27 of section 16.
- Not tested here: nothing of this ran with the CUDA runtime (there is no
  NVIDIA GPU on the dev machine), and the registration of the third hotkey
  was only exercised by the snapshot run (all three registered).

**As built (Windows, WI-4 UI, 2026-09-30).**
- Record tab: `QueueRows` (pure rules) and `QueueRowView` (one persistent
  control set per job, updated in place so a button is never replaced under
  the pointer). The "Transcription queue" card shows whenever the single
  meeting view does not (a session is active or two or more jobs), below the
  saved card; one row per job with its name and time, the state text
  (Waiting, Paused while recording via `IsPausedForSession`, Transcribing N%,
  Done, Failed with its message) and its actions as on the Mac, the folder
  and dismiss buttons as icons at the row's right (tooltip "Reveal in
  Explorer" / "Dismiss"), the language banner and re-run error. Actions wrap
  onto a second line in a narrow window (a small wrap panel; WinUI has none).
  "Generate Notes…" on a row and on the single-meeting card share
  `RecordView.StartNotes` (`NotesStarted`, then the `GenerateNotes` hook).
- Tray: the tooltip and the state line are WI-3's (status; with no session,
  the running job's percentage). `RecordingStatus.SetQueue` (pending count,
  running job's progress, held flag; pushed by `AppShell.SyncRecordingStatus`)
  gives `QueueLine`, the Mac's four strings and rules, shown as one disabled
  item under the state line; "Stop & Start Next" is always in the menu with
  the shortcut, enabled while recording or paused. The menu is a native popup,
  so only its inputs are checked by tests and the snapshot run.
- Settings > General: the picker "Transcribe finished recordings" (rows
  "Right away (in the background)", "When no recording is running") writes
  `AppSettings.FinalPassTiming` and follows it; the caption is the Mac's.
  The shortcut recorder has the third row; each recorder checks its chord
  against both other bindings (`HotkeyRecorder.Validate` takes the others,
  `HotkeyCheck.Conflict` names which) with the Windows-only hint "Already used
  for Stop & Start Next." (the other two hints existed); Reset restores all
  three. Backspace restores that action's own default.
- History's Rename: already wired in WI-3 (`Queue.BusyFiles` into
  `RecordingStatus.BusyFiles` into `HistoryViewModel.BusyStems`, plus the
  notes flow's own stem); WI-4 added a test of the path and fixed the stale
  comments.
- Snapshots (`64` to `70`): the queue with no session, with a session over a
  held queue, with a failure and a language banner, the same at a 480 px
  window, Settings > General with the recorder refusing a duplicate chord
  and at 480 px, and the help page at the Recording section. Looked at in
  all five languages: nothing clipped or overlapping in the new views.
  The tray smoke test also checks the queue line and Stop & Start Next inputs.
- Help and README: the Mac's Stop & Start Next passage has a Windows twin in
  each `shared/help/<lang>/Help.html` (Ctrl+Alt+Win+N, "by default only while
  no recording is running", the picker's other row named), and the README's
  Windows section says the same in three lines.
- Differs from the Mac: the row's folder button is beside the dismiss icon at
  the right, not among the text buttons; the menu shows Stop & Start Next
  disabled instead of leaving it out when nothing records (a native menu does
  not reflow); no menu bar label with a text percentage (a tray icon has no
  text; the tooltip has it).

### 18.11 Automatic recording of Microsoft Teams meetings (built 2026-10-01)

The Windows implementation of section 4.10, built first because the owner
asked for it on Windows; the Mac port followed on 2026-10-02 (4.10 "As
built (Mac, 2026-10-02)"). Same rules, same setting keys
(`autoRecordTeamsMeetings`, `autoRecordAsksLanguage`, both default off),
same strings (in `shared/localization`, catalog `app` since the Mac port
moved them from `windows`; only the prompt's "Record" stays under
`windows`).

- **Signal and probe.** `Hearsay.Core/Audio/MeetingAudioProbe.cs` lists the
  WASAPI audio sessions of every active capture endpoint through NAudio
  (`MMDevice.AudioSessionManager.Sessions`, `GetProcessID`, `State`;
  system-sounds sessions skipped, every COM wrapper disposed) as
  `CaptureSession` records with the process name (no `.exe`) and, when the
  own name is not Teams, the parent's name from one toolhelp snapshot per
  probe (`CreateToolhelp32Snapshot`, kernel32 P/Invoke in Core, as the
  ICU converter is). An endpoint or session that disappears while being
  read is skipped. No new package.
- **Detector.** `Hearsay.Core/Audio/MeetingDetector.cs`, pure: `Observe(sessions,
  now)` returns Started after a Teams Active session has been present for
  `StartDelay` (2 s) and Ended after none has for `EndGrace` (15 s), as
  4.10 says; `IsTeams` matches `ms-teams` and `teams`, any case, with or
  without `.exe`, on the own or the parent name. Tests in
  `Hearsay.Tests/Audio/MeetingDetectorTests.cs` (delay, reset, Inactive and
  Expired ignored, other apps ignored, both names, parent match, device
  switch gap, end grace once, restart cycle, `IsInMeeting`), plus a probe
  smoke test and a parent-lookup test that starts `ping.exe` as a child.
  The Mac ports these tests one for one.
- **Polling, not notifications.** WASAPI session notifications
  (`IAudioSessionNotification`) are registered per device and miss devices
  that appear later, so the coordinator probes every 2 s on the thread pool
  while the setting is on and decides on the UI thread; off, no timer
  runs. A probe failure is logged once until a probe succeeds again. The
  Mac polls every 2 s too: CoreAudio does not report a process's input
  starting or stopping (4.10, "As built (Mac)").
- **Coordinator.** `Hearsay.App/Features/Recording/MeetingAutoRecord.cs`
  follows 4.10's behavior list: Start when idle (the session is marked
  automatic), nothing when the user is already recording, Stop at the
  meeting's end only for an automatic session, the automatic flag drops as
  soon as the controller has no session (user Stop, failure, quit), Stop &
  Start Next keeps it (no gap in `IsSessionActive`), a failed start gets
  the "Recording not started" balloon and no retry, turning the setting
  off stops watching and leaves a running session alone (and drops the
  flag: a meeting that ends afterwards never stops it). The "Recording
  started" balloon fires when the phase reaches Recording, not at the
  `Start()` call. Injected probe, clock, balloon and prompt; the tests
  (`Hearsay.App.Tests/MeetingAutoRecordTests.cs`, 13, on the
  `RecordingControllerTests` rig) drive `Apply(sessions, now)` directly.
- **Record tab notice.** `RecordingController.AutomaticStartNotice`, set by
  the coordinator right after `Start()`, shown in the notice area above
  the system-audio notice, cleared by a plain Start and when the session
  ends (Stop & Start Next keeps it).
- **Balloons.** `TrayIcon.Notify(title, message)` on H.NotifyIcon's
  `ShowNotification`; a no-op while the icon is hidden (taskbar-only
  mode) or not created, and a failure is logged, never thrown. The Mac
  uses `UNUserNotificationCenter`.
- **Language prompt.** `MeetingRecordPrompt.AskAsync(shell, token)`: the main
  window comes forward on the Record tab and a ContentDialog asks
  "Microsoft Teams meeting started" / "Record this meeting?" with a
  picker (Auto, then each language by its own name, as the Auto mode
  default language picker; the Record tab's EN / ZH-TW short labels read
  poorly in a drop-down) preselected with the current choice, Record and
  Don't record. Record writes `RecordingController.LanguageChoice` (it
  persists) and the session starts; the meeting ending cancels the token,
  which hides the dialog, including one still queued behind another
  dialog. "Record" is a Windows-only key: the Mac's "Record" is the tab's
  name (German "Aufnahme", a noun) and GLOSSARY wants verbs on buttons
  ("Aufnehmen", "Grabar", "錄音", "录音").
- **Settings UI.** Settings > General > Meetings (header + card between
  Startup and Transcription): the two toggles with captions; the second is
  enabled only while the first is on. Checked in a `HEARSAY_UI_SNAPSHOTS`
  render; no numbered snapshot added.
- **Debug entry.** `HEARSAY_WATCH_MEETINGS=<seconds>`
  (`Features/Debug/MeetingWatchDebug.cs`, AGENTS.md table): lists the
  sessions every second (endpoint, pid, process, parent, state; then one
  line per change) and prints `meeting started` / `meeting ended`; records
  nothing. Checked on the dev machine against a parallel
  `HEARSAY_RECORD_SECONDS` run: it printed the Hearsay session with its
  `pwsh` parent as Active and then removed. Debug runs never start the
  coordinator.
- **Help and README.** One `<li data-platform="windows">` after the
  Startup item in each of the five help pages, quoting both translated
  labels (shared since the Mac port, with a Mac-only sentence). README's
  Windows section has a "Teams meetings" paragraph.
- **Untested.** Detection against a real Teams call: no agent could join
  one, so whether new Teams' microphone session is owned by `ms-teams.exe`
  itself (expected) or a helper (the parent hedge) is unconfirmed. Section
  16 item 28 is the owner's check with `HEARSAY_WATCH_MEETINGS`; if the
  owning process has another name, add it to `MeetingDetector`'s list.
  Known limits: the parent hedge does not guard against PID reuse (a
  parent that exited whose PID Teams now holds would count); only Teams is
  detected (a browser's microphone stream does not say which site uses
  it, so Zoom, Meet and the Teams web app are out of scope).

### 18.12 Manual final passes (port of section 4.11, added 2026-10-01)

The Windows implementation of section 4.11, built first. The Mac port
followed on 2026-10-02 (4.11 "As built (Mac)"). Same rules and setting value
(`manual`). The nine new strings moved from catalog `windows` to the app and
core catalogs with the Mac port. The `manual` vectors are a separate section
of `shared/transcription-queue-tests.json`, which both platforms run.

Work items:
- **WI-1, Core and queue.** Policy, vectors, the setting value, and the
  queue's released, hold and release-all handling. Replay support and
  tests.
- **WI-2, UI.** Picker row, Record tab rows, single meeting view, the
  Transcribe All header, tray line and menu item, the quit alert variant,
  strings and translations, help, README, and snapshots.

**As built (Windows, WI-1, 2026-10-01).**
- `shared/transcription-queue-tests.json` has a top-level `manual` section
  (33 `next`, 12 `shouldYield`, 12 `blocksUpdateInstall`, 11
  `allPendingHeld` cases; jobs carry `released`); the old sections are
  unchanged (the Mac test still sees 12 `shouldYield` and ignores the new
  key), and the file's `about` points to it. No other consumer reads the file.
- `FinalPassTiming.Manual` (`"manual"`, after WhenIdle in `FinalPassTimings.All`,
  default stays WhenIdle). `TranscriptionQueuePolicy`: `Job` has an optional
  `Released` (read only under Manual); `MayRun` treats Manual like WhenIdle;
  `IsHeld(timing, job)`; `Next` suspends a running job that is not released
  and starts or resumes only released jobs (a released job may run past an
  earlier held one; among released jobs queue order decides);
  `ShouldYield(..., runningReleased)`; `BlocksUpdateInstall(timing, jobs)`
  (held waiting and suspended jobs do not block, released and running ones
  do, so a running job whose Hold has not taken effect yet still blocks);
  `AllPendingHeld(timing, jobs)` (a running unreleased job counts as held).
  `QuitNeedsConfirmation` is unchanged and counts held jobs.
- `TranscriptionQueue`/`TranscriptionJob`: in-memory `IsReleased` and a
  derived `IsHeld` per job; queue `Release(job)`, `ReleaseAll()`, `Hold(job)`,
  `IsHeld(job)`, `HeldCount`, `AllPendingHeld`, `HasJobPausedForSession`;
  `QueueEvent.Held` and `Released` (announced only under Manual). The
  decoder's volatile flag is "session active under whenIdle or Manual", or
  "Manual and a running job that is not released"; the check just before a
  step uses the same policy function. Try Again (`Retry`, `EnqueueRetry`)
  queues released. Switching to Manual releases running and suspended jobs
  and holds waiting ones (also one that was released earlier); switching away
  re-evaluates at once. Restore gives unreleased jobs; `queue.json` is
  unchanged. Held jobs are not polled for. `IsPausedForSession` is false for
  held jobs, `ActiveJob` skips a held suspended job, and the tray's "paused"
  flag is set only when a released job waits for the session.
- No rule of 4.11 changed.
- Replay: `HEARSAY_REPLAY_TIMING=manual`, and `HEARSAY_REPLAY_RELEASE=<s>`
  (wait after the last Stop, then `ReleaseAll`); `held` and `released` events
  are printed. Without it a manual replay lists the held jobs and exits 0 once
  the live tails are done. Measured (`en-30s.wav` then `de-30s.wav`, Auto,
  turbo q5_0 on Vulkan, fake microphone): manual with release 2 s: both jobs
  held at queue time (19.4 s and 41.5 s), no pass until `Transcribe All` at
  43.5 s, then both SRTs done at 65.0 s and 73.6 s (first lines equal to the
  other timings'), exit 0; manual without release: Auto languages settled
  during the session, no pass, both jobs listed as held, exit 0; immediate and
  whenIdle replays behave as before (job 1 ran at once / after session 2).
  No CUDA path was run.

**As built (Windows, WI-2, 2026-10-01).**
- Settings > General > Transcription: the picker has the third row "When I
  start them" (`FinalPassTimingChoices.All`, `Label(Manual)`); a stored
  `manual` selects it. The caption is a Windows-only key ("The next recording
  can start at once, whichever you choose. With “When I start them”,
  recordings wait on the Record tab until you click Transcribe or Transcribe
  All."); the Mac's "Either way, ..." key stays in the catalog for the Mac.
- Rows (`QueueRows`, `QueueRowView`): `QueueRows.IsHeld(job, timing)` is the
  policy's own rule, so a row cannot disagree with the queue. A held waiting
  job reads "Not transcribed yet", a held suspended (or still running) one
  "On hold · N%" with the progress bar it kept. Held rows offer Transcribe
  (accent button, first) and, as before, "Use live preview instead"; a
  released pending row offers Hold (tooltip "Stops transcribing for now.
  Transcribe continues where it stopped.") while the timing is Manual, and
  not while it saves its live preview. A released job waiting for a session
  still reads "Paused while recording". Done and failed rows are unchanged.
  Controls stay in place and only change visibility.
- Queue card header: "Transcribe All" (`ReleaseAll`) while `HeldCount > 0`.
- Single meeting (`RecordView`, one job and no session): a held job keeps its
  live preview; the progress card's title is the row text ("Not transcribed
  yet" with no bar, or "On hold · N%" with the bar), with Transcribe and "Use
  live preview instead"; released, it is the card of before ("Transcribing",
  percentage) plus Hold. The state text at the top right of the Record tab
  reads the held text instead of "Finalizing…". `FeaturedJob` is
  `!session && Jobs.Count == 1`, so a held job qualifies (its state is
  Waiting or Suspended); `ActiveJob` skips only a held suspended job, which
  only the tray reads. The buttons of the card wrap in a narrow window.
- Tray: `RecordingStatus.SetQueue(..., allHeld, heldCount)`; with every
  pending job held the queue line reads "Not transcribed yet · in queue: N"
  (before the percentage and paused rules, with or without a session).
  **Menu choice:** the popup is not rebuilt each time it opens (it is rebuilt
  when the phase, the queue line or `CanTranscribeAll` changes:
  `TrayIcon.RebuildsMenu`), so "Transcribe All" is always in the menu, under
  Stop & Start Next, and enabled only while a job is held, as Stop & Start
  Next is for recording. It calls `queue.ReleaseAll()`.
- Quit alert: `Strings.QuitQueueMessage(queue.AllPendingHeld)`; the second
  line is "They stay in the queue until you transcribe them." when every
  pending job is held, else the Mac's line.
- WI-1 fix: `UseLivePreviewInstead` on a held job now releases it (the user
  asked for that save). Before, the job stayed counted as held while the live
  preview was written, so its row read "Not transcribed yet" with Transcribe,
  Transcribe All showed, and the quit alert could say "They stay in the queue".
- Strings: nine Windows-only keys (catalog `windows`, translated in de, es,
  zh-Hant and zh-Hans): "When I start them", the new caption, "Not
  transcribed yet", "On hold · %lld%%", "Transcribe All", "Hold", the Hold
  tooltip, "Not transcribed yet · in queue: %lld" and "They stay in the queue
  until you transcribe them." ("Transcribe" is the existing Mac key). Hold
  is "Zurückstellen" / "Aplazar" (defer), not a word for pause or stop. The
  Mac port moved the keys to the app catalog ("When I start them" to core),
  and `Strings.cs` reads them with `App(...)` / `Core(...)` now.
- Help and README: one sentence in each Windows twin of the Stop & Start Next
  passage (and one in the README's "Back to back" paragraph).
- Snapshots `71` to `76`: the held queue (an on-hold job with progress and a
  held waiting job, Transcribe All), the same at 480 px, a released running
  job with Hold among held jobs, the single held meeting (and at 480 px),
  Settings > General with the third row. The single meeting on hold and
  released, the tray's inputs and the quit alert line are checked in the same
  run. Looked at in all five languages.
- Not verified: the live tray menu (a native popup; only its inputs and the
  rebuild rule are tested), the buttons with a real pointer, a held job
  running a real pass (the queue is covered by `ManualQueueTests` and the
  replay of WI-1). No CUDA path was run.

**To do (owner request 2026-10-02).** The two 4.11 rules "Queue list always
under manual" and "Move to Trash on a held row", built on the Mac first:
- `FeaturedJob` is null while the timing is Manual (port
  `showsSingleMeeting` and its test); the single-meeting card's held states
  and the held state text at the top right become unreachable; check the
  tray and the notes hand-off still read right.
- A held, idle row gets **Move to Recycle Bin…** (the existing History key)
  after its other buttons, with a confirmation dialog: title "Move this
  recording to the Recycle Bin?", message "It is not transcribed, and its
  live preview is discarded." (the Mac's app key, usable as is), primary
  **Move to Recycle Bin** (existing key) and Cancel.
- `TranscriptionQueue.Trash(job)` with the Mac's guards and steps (port
  `canTrash` and its test), the Recycle Bin through the shell's
  recycle-bin delete (the one History uses), the row's error line, a
  `Trashed` queue event and `HEARSAY_REPLAY_TRASH=1` in the replay.
- A WAV that no longer exists: `Trash(job)` removes the job without an
  error (the Mac's rule of 2026-10-02, above).
- Strings: two Windows-only keys (catalog `windows`, translated in de, es,
  zh-Hant, zh-Hans as History's Recycle Bin keys are): "Move this recording
  to the Recycle Bin?" and "Could not move the recording to the Recycle Bin:
  %@"; the Mac's "It is not transcribed, and its live preview is discarded."
  is read with `App(...)`.
- Help: the Windows twin of the "When I start them" sentence adds "…or move
  one to the Recycle Bin with Move to Recycle Bin…".
- Snapshots: the single held job as a row (and at 480 px), a held row with
  Move to Recycle Bin…, and the error line.
