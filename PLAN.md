# Hearsay: native macOS port of whisper-tools

Status: Phase 0 spike done 2026-09-28, verdict GO. See section 15 for
results. `Spike/` holds the throwaway spike; the app itself is not built yet.

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
| Inference | MLX. The Whisper module of `Blaizzy/mlx-audio-swift` (MIT, 1,526 lines, commit `01dec7c9`) is vendored into `HearsayCore/Whisper/` and its decode loop is replaced with a timestamped decoder ported from `mlx_whisper` 0.4.3. Reason: section 15. Direct dependencies become `ml-explore/mlx-swift` and `huggingface/swift-transformers` only. |
| Models | Downloaded on demand from Hugging Face `mlx-community/whisper-*` repos into the app's own model directory. User picks the model. Nothing ships inside the bundle. |
| Audio I/O | AVFoundation. Mic via `AVCaptureSession` + `AVCaptureAudioDataOutput` (16 kHz mono Float32), chosen by CoreAudio UID; `AVAudioFile` for files. No FFmpeg. Changed 2026-09-28: the `AVAudioEngine` tap got no buffers after switching input device, because the input node kept reporting the previous device's rate. |
| System audio | Captured with ScreenCaptureKit (`SCStream`, audio only) and mixed with the mic, so Zoom/Teams/Meet calls are transcribed, not just the room. Decided 2026-09-28. |
| Live transcript | Yes in v1. 30 s chunks are transcribed while recording and shown as a live preview; the final SRT comes from one full pass after Stop. Decided 2026-09-28. |
| Output folder | Default `~/Documents/Hearsay`, user-configurable in Settings. Decided 2026-09-28. |
| Languages | Added 2026-09-28 (owner request): Auto, EN, ZH-TW, ZH-CN, DE, ES (the two Chinese choices replaced ZH plus a separate "Chinese output" setting, owner request 2026-09-28: both call Whisper with "zh", ZH-TW transcripts are converted to Traditional characters and ZH-CN to Simplified; when Auto detects Chinese it uses the preferred language's Chinese variant, else ZH-TW; stored "zh" settings migrate by the old Chinese output setting). Auto is the picker default for new users. Detection only compares the four supported languages (probabilities renormalized over them), skips silent windows (below about -60 dBFS, or no-speech probability above 0.6; the turbo model's no-speech probability alone never flags silence, measured 2026-09-28), averages the restricted probabilities of up to three speech windows, and locks the language once per session. Below the confidence threshold it uses the **preferred language** from Settings > General (default English, owner choice; never changed automatically by what the user picks elsewhere) and says so, with one-click re-run in another language. When the user picked a language and detection is confident it is a different one, a banner offers to re-run in the detected language; nothing switches automatically. Meeting notes default to the transcript language; the confirm sheet can pick another of the four for that run only (added 2026-09-28, owner request). History SRTs with no stored language default to the language NaturalLanguage detects in the text (probability at least 0.6), else the language choice or preferred language. Mixed-language (code-switching) meetings are out of scope. |
| v1 extras | Global hotkey, pause/resume, Sparkle auto-updates, crash recovery of an unfinished recording, custom prompt templates, and a setting that decides whether the WAV is kept at all. Decided 2026-09-28. |
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
   `Fixtures/` for integration tests. Record it yourself; do not
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
      Updates/                Sparkle wiring
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
  Fixtures/                    short test audio, tiny model stub for offline tests
  Scripts/
    build-release.sh           xcodebuild archive, sign, notarize, staple, DMG
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
- Menu bar icon changes to a filled red variant while recording, and a
  paused variant while paused.
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

Sparkle 2 with `SUFeedURL` pointing at an appcast you host (GitHub Pages
or the repo's Releases). EdDSA signing key kept out of the repo. Sparkle
runs in the sandbox with the XPC services it ships. "Check for updates"
in the app menu and the menu bar extra, automatic check on launch, opt-in
for pre-releases.

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
| `--initial-prompt` for zh | supported as an advanced setting, but **off by default**. Measured 2026-09-28 on `Fixtures/zh-30s.wav` with the turbo model: the Python tool's English prompt ("The following is a sentence in Traditional Chinese.") made turbo echo the prompt and produce no transcript, and made large-v3 output Simplified characters. A Traditional Chinese prompt ("以下是繁體中文的句子。") gave Traditional script but rounded every timestamp to whole seconds and appended a hallucinated closing line. No prompt gave Traditional script, natural cue boundaries, and no hallucination. |
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
- Check for updates automatically: on / off.

## 9. Entitlements and privacy

```text
com.apple.security.app-sandbox                       false   (see section 1)
com.apple.security.device.audio-input                true    (hardened runtime needs it)
NSMicrophoneUsageDescription  "Hearsay records meetings you start and transcribes them on this Mac."
NSAudioCaptureUsageDescription "Hearsay captures the audio of your calls so both sides of a meeting are transcribed."
```

System audio needs the Screen & System Audio Recording TCC permission.

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
| 6. Ship | History, hotkeys, quit handling, crash recovery, launch at login, app icon, README done 2026-09-28. Remaining: Sparkle + appcast, signing, notarization, DMG script, quick-start guide note (`docs/whisper-tools-note.md`). | 2 days |

Total: about 8 to 9 weeks of calendar time.

## 11. Testing

- `HearsayCoreTests`: port `whisper-tools/tests/test_run_whisper.py` cases for
  sanitize, insert name, save outputs, rename with rollback, timestamp
  parsing, level meter math, prompt text, response parsing, SRT formatting.
  These run on any Mac in seconds.
- Integration (opt-in, `HEARSAY_MODEL_DIR` set): transcribe
  `Fixtures/en-30s.wav` and `Fixtures/zh-30s.wav`, assert a known phrase
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

Setup: `Spike/` Swift package, executable `hearsay-spike`, depends on
`mlx-audio-swift` at commit `01dec7c9`. Built with `xcodebuild` after
installing the Metal Toolchain. Fixture: `Fixtures/en-30s.wav`, 18.9 s of
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
cd Spike && xcodebuild -scheme hearsay-spike -destination 'platform=macOS,arch=arm64' \
  -configuration Release -derivedDataPath .build/derived \
  -skipPackagePluginValidation -skipMacroValidation build
cd .. && Spike/.build/derived/Build/Products/Release/hearsay-spike \
  Spike/models/mlx-community_whisper-large-v3-turbo Fixtures/en-30s.wav en
```

`Spike/models/` is git-ignored; re-download with the URLs in section 5.

## 16. Hands-on checklist for the owner (after Phase 4b)

Things no agent could verify because they need permissions or a person.
Verified 2026-09-28: recording, live preview, final pass, File mode (items
1, 10, 12 in part). Still open: 2 to 9, 11.

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

- **To do (owner request 2026-09-28): write a Windows version plan**
  (`PLAN-WINDOWS.md`, beside this file; plan only, no code yet). Starting
  points agreed in discussion: native C# / .NET app (WinUI 3 or WPF) with a
  tray icon; whisper.cpp, or faster-whisper with CUDA on NVIDIA GPUs; WASAPI
  for the mic and loopback for system audio; Windows Credential Manager for
  tokens; same HTTP providers plus Copilot CLI. Reuse this plan's naming
  rules, prompt, JSON contract, and the `Fixtures/` audio with expected SRTs
  as a shared acceptance suite. Cover CPU-only performance (smaller default
  model), the need for a Windows machine to build and test, and an estimate
  (discussed: about 6 to 8 weeks of agent work).

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
