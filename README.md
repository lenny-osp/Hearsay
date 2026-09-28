# Hearsay

Hearsay records your meetings on your Mac and turns them into transcripts.
It captures your microphone and, if you like, the sound of the call itself
(Zoom, Teams, Meet), transcribes everything on the Mac with an on-device
Whisper model, and saves a subtitle file (SRT) plus the recording. When you
want meeting notes, Hearsay can send the transcript to an AI provider you
pick and saves the notes next to the transcript. Transcripts never leave the
machine unless you confirm the AI step.

## Requirements

- A Mac with Apple Silicon (M1 or later). Intel Macs are not supported.
- macOS 14 Sonoma or later.
- Disk space for one speech model (74 MB to 3.08 GB, see below).

## Install

A signed, notarized download comes later. For now, build Hearsay from source:

1. Install Xcode 27 from the App Store, launch it once to accept the license,
   and select it: `sudo xcode-select -s /Applications/Xcode.app`.
2. Install the Metal Toolchain component (about 840 MB):
   `xcodebuild -downloadComponent MetalToolchain`.
3. Install XcodeGen: `brew install xcodegen`.
4. From the repository folder, run `Scripts/run-debug.sh`. It generates the
   Xcode project, builds the app, and opens it. The built app is at
   `.build/derived/Build/Products/Debug/Hearsay.app`; pass `--no-open` to
   build without launching.

## First run

1. **Output folder.** Transcripts and notes go to `~/Documents/Hearsay`,
   created on first use. To use another folder, open Settings > Output and
   click "Choose…".
2. **Download a model.** With no model installed, Hearsay offers the
   recommended model on one click. The Models tab lists all of them, with
   Download, Cancel, Delete, and "Use this model":

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
   `~/Library/Application Support/Hearsay/Models/`. A download that was
   interrupted resumes on the next launch.
3. **Grant permissions.** The first time you press Start, macOS asks for
   **Microphone** access. If "Also capture system audio" is on, it also asks
   for **Screen & System Audio Recording**; Hearsay only uses the audio. If
   you deny it, recording continues with the microphone only and a badge
   says so; "Open System Settings" takes you to the setting.

## Recording

- **Record tab.** Pick the microphone and the language (EN or ZH), leave
  "Also capture system audio" on to include the other side of a call, and
  press Start. A level bar shows the mix, two small meters show whether the
  microphone and the system audio are each alive, and a warning appears
  after 5 seconds of silence.
- **Menu bar.** The menu bar item shows the state and elapsed time, has
  Start/Stop, and turns red while recording. Recording continues when the
  main window is closed.
- **Hotkeys.** ⌃⌥⌘R starts and stops a recording, ⌃⌥⌘P pauses and resumes.
  They work in any app, even with the window closed. Change them in
  Settings > General.
- **Pause.** Pause stops capturing; paused time is left out of the
  transcript, so the SRT has no gap.
- **Live preview.** While you record, Hearsay transcribes the audio in
  chunks of about 30 seconds and shows the text as it goes. If the Mac falls
  behind, the preview lags but nothing is dropped.
- **Final pass.** After Stop, Hearsay transcribes the whole recording once
  more ("Finalizing…"). This is the SRT you get, with one cue per sentence.
  "Use live preview instead" skips the final pass and saves the preview as
  the SRT at once.
- If the final pass fails, the live preview SRT is kept when there is one,
  the WAV is always kept and its path shown, and "Transcribe this file"
  sends it to File mode.

## File mode

Drop an audio or video file on the File tab, or click "Choose…". Hearsay
reads wav, m4a, mp3, aac, aiff, caf, and the audio track of mp4 and mov.
The SRT goes to the output folder. Its name starts with the timestamp in the
source file name when there is one, otherwise the file's creation time,
otherwise its modification time.

## Meeting notes

1. **Set up a provider** in Settings > AI. Pick a preset: GitHub Copilot
   CLI, OpenAI, Azure OpenAI, Anthropic (OpenAI compatible), Ollama / LM
   Studio, or Custom for any other OpenAI-compatible `/chat/completions`
   endpoint. "Test connection" sends a one-line prompt.
   - **GitHub Copilot CLI** runs the `copilot` program installed on your
     Mac (`npm install -g @github/copilot`) and uses its own login, so
     Hearsay needs no token; run `copilot` once in Terminal to log in.
     Hearsay finds it in Homebrew, `/usr/local/bin`, or nvm, or through
     your login shell; set the path in Settings > AI if it lives elsewhere.
     "Check Copilot" shows the version it found. The default model is
     `gpt-5.6-luna` with reasoning effort `max`; model `auto` lets Copilot
     choose. When Hearsay finds the CLI on first launch, this is the
     default provider.
   - **The other presets** need the model and your token; tokens are stored
     in the macOS Keychain. Ollama and LM Studio run locally and need no
     token.
2. **Confirm.** After a transcript is saved, Hearsay asks "Send transcript
   for meeting notes?" and shows the provider, model, and transcript size.
   "Keep local" keeps everything on the Mac; you can still give the files a
   name. Turn the question off in Settings > AI if you always want notes.
3. **Prompt templates.** The built-in "General meeting" template is the
   same prompt whisper-tools uses. Add your own templates in Settings > AI
   and pick one in the confirm sheet. Your template replaces the notes
   instructions only; the file-name and JSON rules are always added, so the
   reply stays readable.
4. **Name.** The AI suggests a short name; edit it or press Save. Names are
   turned into lowercase letters, digits, and hyphens.
5. **Output files**, all in the output folder:

   | File | Content |
   |---|---|
   | `<timestamp>_<name>.srt` | the transcript with timings |
   | `<timestamp>_<name>.md` | the meeting notes |
   | `<timestamp>_<name>_transcript.md` | a structured transcript |
   | `<timestamp>_<name>.wav` | the recording (16 kHz mono) |

   The timestamp looks like `2026-09-28_14-05-00`. If a name is taken, a
   number is added. Turn off keeping the WAV in Settings > Output; a failed
   transcription always keeps it.

## History

The History tab lists recordings, transcripts, and notes in the output
folder. Open the SRT, notes, or transcript, reveal them in Finder, move them
to the Trash, or run "Generate notes…" on a saved SRT.

## Window modes

Settings > Window decides where Hearsay appears: "Menu bar and Dock", "Menu
bar only", or "Dock only". Changes apply immediately. Settings > General
also has "Launch Hearsay at login".

## Crash recovery

Hearsay writes the recording to disk as it goes. If the app or the Mac
stops in the middle of a recording, the next launch finds the unfinished
recording and asks whether to transcribe it, keep it, or delete it.

## Not sandboxed

Hearsay is distributed with Developer ID (signed and notarized), not
through the Mac App Store, and it runs without the App Sandbox because it
starts your installed Copilot CLI, which needs its own login files, and
saves straight to `~/Documents/Hearsay`. It still uses the hardened runtime,
and macOS still asks before it can use the microphone or record system
audio.

## Troubleshooting

- **Start is disabled or asks for a model.** No model is installed or
  selected. Open the Models tab, download one, and click "Use this model".
- **Microphone permission denied.** Open System Settings > Privacy &
  Security > Microphone and turn Hearsay on.
- **System audio is silent after granting permission.** Quit and reopen
  Hearsay; macOS applies Screen & System Audio Recording only after a
  relaunch.
- **"GitHub Copilot CLI not found".** Install it with
  `npm install -g @github/copilot`, or enter the full path of `copilot`
  (for example the output of `command -v copilot` in Terminal) in
  Settings > AI.
- **The Copilot CLI call fails.** Run `copilot` once in Terminal to log in,
  and check that the model name is one your Copilot plan offers.
- **Launch at login waits for approval.** Approve Hearsay in System
  Settings > General > Login Items ("Open Login Items" in Settings >
  General takes you there).

## Relation to whisper-tools

Hearsay is the native macOS version of the `whisper-tools` Python CLI
(`run_whisper.py`). It writes the same output names (`<timestamp>_<name>.srt`,
`.md`, `_transcript.md`, and the WAV) and sends the same meeting-note prompt,
so files from both tools sit side by side. The GitHub Copilot CLI provider
runs `copilot` with the same arguments the CLI uses (`AI_PROVIDER=copilot`),
and the other presets cover its `AI_API_URL` path. One difference: Chinese
runs without an initial prompt, because the CLI's English prompt made the
turbo model echo the prompt and made large-v3 write Simplified characters,
while no prompt gives Traditional script and natural cue boundaries.
