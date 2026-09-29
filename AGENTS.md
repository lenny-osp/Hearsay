# Agent guide for Hearsay

Hearsay is a meeting recorder and transcriber. The macOS app is finished
and in daily use; the Windows version is in progress. This file tells an agent
how to work in this repository without breaking what exists. Read it
fully before editing anything.

## Layout

```text
shared/         resources both platforms use
  fixtures/     test audio (en, zh, de, es) and the Python reference SRTs
  localization/ GLOSSARY.md, strings-en.json, de/es/zh-Hant/zh-Hans.json
  help/         Help.html per interface language (the only copies to edit)
  prompts/      meeting-notes prompt text, languages, assembly and JSON contract
  assets/       icon-1024.png, written by mac/Scripts/make-icon.swift
  models/       model catalog schema (each platform keeps its own list)
  scripts/      make-naming-tests.py (regenerates the naming vectors)
  naming-tests.json, language-decision-tests.json   shared test vectors
mac/            the macOS app (Swift 6, SwiftUI, MLX)
  Hearsay/      app target (features, resources, string catalogs, help pages)
  HearsayCore/  pure logic, no UI, no MLX; tested with `swift test`
  HearsayWhisper/ vendored MLX Whisper model + the timestamped decoder
  Scripts/      build, translation, notices, icon scripts
  Spike/        the Phase 0 spike; its models/ folder holds local test models
  project.yml   XcodeGen spec; Hearsay.xcodeproj is generated, never edited
windows/        the Windows version (C#, WinUI 3, whisper.cpp; PLAN.md section 18)
  Spike/        the W1 spike; its models/ folder holds local test models
docs/           notes for the sibling whisper-tools repo
PLAN.md         the design record: every decision, with dates and reasons
README.md       user documentation
LICENSE         MIT, Chihling Wang
```

`PLAN.md` is the source of truth for design decisions. When you change
behavior, update the relevant PLAN.md row in the same change. Section 16
is the owner's hands-on checklist and section 17 the polish list; add to
them rather than leaving findings only in a chat report.

## Building and testing (macOS)

Everything runs from `mac/`.

```bash
cd mac && Scripts/run-debug.sh --release --no-open   # build (Release)
cd mac/HearsayCore && swift test                      # core tests, fast
cd mac/HearsayWhisper && TEST_RUNNER_HEARSAY_MODEL_DIR=/abs/path/to/model \
  xcodebuild -scheme HearsayWhisper -destination 'platform=macOS,arch=arm64' \
  -derivedDataPath .build/derived -skipPackagePluginValidation \
  -skipMacroValidation test                           # decoder tests, slow
```

- `swift build` cannot compile MLX's Metal kernels: any target that imports
  MLX must be built with `xcodebuild`, always with
  `-skipPackagePluginValidation -skipMacroValidation`.
- The Release app is `mac/.build/derived/Build/Products/Release/Hearsay.app`.
  The owner runs this build; do not launch a second instance of it (it would
  register the global hotkeys and a second menu bar item) and never kill
  the owner's running instance. Use the debug entry points below instead.
- `run-debug.sh` deletes embedded resource bundles before building because
  Xcode does not re-copy a bundle whose `.lproj` folders changed.
- Put a hard time limit on every long command:
  `perl -e 'alarm shift; exec @ARGV' 1500 <command>`.
- Local test models live in `mac/Spike/models/` (git-ignored). Do not
  download a model larger than 500 MB without being asked.

## Debug entry points (headless, no microphone needed)

Set environment variables and run the app binary directly; each prints to
stdout and quits. They use a throwaway defaults suite, so the owner's
settings are not touched.

| Variables | What it does |
|---|---|
| `HEARSAY_TRANSCRIBE_FILE=<wav> HEARSAY_MODEL_DIR=<dir> HEARSAY_LANGUAGE=auto\|en\|zh-TW\|zh-CN\|de\|es` | File-mode transcription; prints the language decision and the SRT path |
| `HEARSAY_REPLAY_FILE=<wav> HEARSAY_MODEL_DIR=<dir> [HEARSAY_REPLAY_UI=1] [HEARSAY_REPLAY_SYSTEM=silence]` | Replays a WAV through the real recording pipeline in real time (live preview, final pass) |
| `HEARSAY_RECORD_SECONDS=<n> HEARSAY_RECORD_DEVICE=<name part> [HEARSAY_RECORD_KEEP=<wav>]` | Records from a real input device; needs microphone permission |
| `HEARSAY_UI_SNAPSHOTS=<dir> HEARSAY_UI_LANGUAGE=<en\|de\|es\|zh-Hant\|zh-Hans>` | Renders every tab, sheet, the menu bar panel, the update progress window, and the help page to PNGs |
| `HEARSAY_INSTALL_UPDATE=<dmg> HEARSAY_INSTALL_TARGET=<app> [HEARSAY_INSTALL_VERSION=<v>]` | Runs the update install against a scratch app bundle: location check, checksum (against `SHA256SUMS.txt` next to the DMG, else skipped), mount, signature check, staging, bundle replacement; never relaunches. Never point it at the running app or `/Applications/Hearsay.app` |

Debug runs may leave an empty `~/Library/Preferences/tw.og1o.hearsay.debug-*.plist`;
delete those you create. Delete every SRT a debug run writes into the
owner's output folder (`~/Documents/Hearsay`) and never touch the owner's
own files there.

## Building and testing (Windows)

Everything runs from `windows/`, on Windows 11 x64 with the .NET 10 SDK
and Visual Studio 2026 (workloads ".NET desktop development" and "WinUI
application development").

```powershell
dotnet build windows\Hearsay.slnx -c Release         # build
dotnet test windows\Hearsay.Tests -c Release          # core tests, fast
dotnet test windows\Hearsay.App.Tests -c Release      # app logic and string lookup, fast
```

- The solution is `Hearsay.slnx` (the XML format .NET 10 creates).
  `Directory.Build.props` holds the settings every project shares; do not
  repeat them in a csproj. New source files need no csproj edit.
- Tests read `shared/` through `Hearsay.Tests.SharedFiles` (path stamped
  at build time); never copy a shared file into `windows/`.

- The Whisper integration tests (`windows/Hearsay.Tests/Whisper`) read
  the model path from `TEST_RUNNER_HEARSAY_MODEL_DIR` and skip without it,
  as on macOS. On Windows the value is the model path: a whisper.cpp
  `ggml-*.bin` file, or a folder holding one
  (`ggml-large-v3-turbo-q5_0.bin` preferred when there are several).
  `HEARSAY_TEST_WHISPER_RUNTIME=cpu|vulkan` pins the runtime for a
  measurement run. On this dev machine the full Whisper set takes about
  5 minutes on Vulkan; on CPU budget 30 minutes or more.
  ```powershell
  $env:TEST_RUNNER_HEARSAY_MODEL_DIR = "$PWD\windows\Spike\models\ggml-large-v3-turbo-q5_0.bin"   # an absolute path
  dotnet test windows\Hearsay.Tests -c Release --filter "FullyQualifiedName~Hearsay.Tests.Whisper" --logger "console;verbosity=detailed"
  ```
- The owner may run an installed Windows build. Do not start a second
  instance of it (hotkeys, tray icon) and never kill it; use the debug
  entry points.
- Put a hard time limit on every long command: the tool's `timeout`
  parameter, or in Git Bash `perl -e 'alarm shift; exec @ARGV' 1500 <command>`.
- Local test models (whisper.cpp `ggml-*.bin`) live in
  `windows/Spike/models/` (git-ignored). The 500 MB download rule applies.
- The dev machine has no NVIDIA GPU (PLAN.md 18.6): CUDA code paths
  cannot be run here; say so in the report instead of claiming them tested.
- The Windows app implements the same debug entry points (same variable
  names and meaning) with a throwaway settings folder instead of
  `%APPDATA%\Hearsay`. Delete every SRT a debug run writes into the owner's
  output folder (`%USERPROFILE%\Documents\Hearsay`).
- Third-party notices: after a dependency change (and a restore) run
  `windows\scripts\make-notices.ps1`; it rewrites
  `windows/THIRD_PARTY_NOTICES.md` from the restored packages and fails
  loudly, naming the package, if a license text cannot be found.
  `make-notices.ps1 -Check` is the CI form: it fails if the committed
  file is out of date.

## Parity with the Python tool

`mac/HearsayWhisper` must reproduce `mlx_whisper` 0.4.3 output. The
acceptance test compares against `shared/fixtures/*.expected.srt`, which
are byte-identical today. Keep them that way: a decoder change that alters
any fixture output needs a written reason in PLAN.md section 6. The
meeting-notes prompt for English and Traditional Chinese must stay
byte-identical to the Python tool (tests in `NotesTests.swift`).
`shared/fixtures/README.md` says how to regenerate the references.

## Conventions

- Swift 6 language mode, strict concurrency, no force unwraps outside
  tests, no new third-party packages beyond those listed in PLAN.md.
- Every ported rule gets a unit test mirroring the Python test; every
  user-visible failure is actionable and returns a non-zero status.
- Tests that need `UserDefaults` use `ScratchDefaults` (never a bare
  `UserDefaults(suiteName:)`, which leaks plists into `~/Library/Preferences`).
- User-facing strings go through the string catalogs
  (`String(localized:)`, `bundle: .module` in HearsayCore). Never localize
  the AI prompt, CLI arguments, file names, log lines, product names, or
  language autonyms.
- Translation workflow after changing strings: build, then from `mac/`
  run `Scripts/export-strings.py --configuration Release`, add the new keys
  to each `shared/localization/<lang>.json` following `GLOSSARY.md`, then
  `Scripts/merge-translations.py` (use `--check <lang>` first). UI labels
  quoted in the help pages must match the catalogs. Help pages are edited
  only in `shared/help/<lang>/Help.html`; `Scripts/sync-shared.sh` copies
  them to the git-ignored `Hearsay/Resources/<lang>.lproj/Help.html`.
  Windows: every user-facing string goes through
  `windows/Hearsay.App/Strings.cs` (`App("…")`, `Core("…")` for the Mac's
  catalogs, `Win("…")` for Windows-only keys, which are catalog `windows`
  entries in the same JSON files). After changing Strings.cs or the JSON,
  run `python windows/scripts/import-strings.py` (CI: `--check`); it writes
  `windows/Hearsay.App/Strings/<lang>/Resources.resw`, which are committed
  so a build needs no Python, and fails if a key is missing in any language.
  The Mac's export and merge scripts must keep the `windows` entries.
- Third-party notices: after a dependency change run
  `mac/Scripts/make-notices.sh`; it fails loudly if a license file moved.
- C# (Windows): nullable reference types on, warnings are errors, no `!`
  null-forgiving operator outside tests, no NuGet packages beyond those
  listed in PLAN.md 18.3. Windows reads `shared/` at build or run time;
  it never keeps its own copy of a shared file.
- Line endings: `.gitattributes` makes every text file LF on every
  platform (`.sln`, `.bat`, `.cmd` are CRLF). The byte-compared files in
  `shared/` (prompts, expected SRTs, vectors, help) must never get CRLF;
  C# code that reads them must not translate newlines.
- Git: author is Chihling Wang <chihlingw@gmail.com> (repo-local config;
  set it in every new clone, including on Windows).
  Commit messages have an imperative subject and a short body; no
  `Co-Authored-By` or other attribution trailers, even if a tool suggests
  one. Subagents do not commit; the reviewing agent commits after building
  and testing.

## Owner decisions to respect

These were made deliberately; do not "fix" them (details and dates in
PLAN.md):

- App Sandbox is off so the app can run the AI command-line tools; the
  distribution path is Developer ID, not the Mac App Store.
- One main window, no window tabs, Settings is a tab (⌘, opens it).
- Language picker Auto / EN / ZH-TW / ZH-CN / DE / ES; Auto is the default
  for new installs; the Auto mode default language (Settings > General;
  `preferredLanguage` in code) defaults to
  English and is changed only by the user.
- Chinese is transcribed with no initial prompt; Traditional or Simplified
  is a character conversion afterwards.
- Notes providers are CLIs using the owner's subscriptions (Copilot,
  Claude Code, Codex, Antigravity), plus Ollama and Custom; CLI presets
  never send temperature. Default models: Copilot `gpt-5.6-luna`, Claude
  Code `claude-sonnet-5` (high), Codex `gpt-6-luna` (max), Antigravity
  `gemini-3.8-flash-high`.
- Antigravity runs with Hearsay's own deny-list project and its run is
  deleted afterwards; never edit the owner's global agy, Claude Code, or
  Codex settings.
- Recordings are spooled and always kept on failure; the WAV is kept after
  success unless the user turned that off.
- Help and README are short; the owner asked for essentials only.

## Things never to do

- Never send a real transcript from the owner's output folder to any AI
  provider. Live checks use `shared/fixtures/en-30s.expected.srt`.
- Never run `defaults write` against the owner's real domain
  `tw.og1o.hearsay` or the global domain.
- On Windows, never write the owner's `%APPDATA%\Hearsay\settings.json`,
  the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key, or
  Hearsay's Credential Manager entries; tests use a scratch folder.
- Never change Windows system or security settings (Developer Mode,
  Defender, execution policy, drivers); ask the owner to do it.
- Never delete anything in the owner's home folder except files you
  created in that session, and list them first.
- Never take screenshots of the owner's screen; render views with
  `HEARSAY_UI_SNAPSHOTS`.
- Never add `Co-Authored-By` lines to commits.

## Windows version

See PLAN.md, section 18 "Windows version". Shared resources for it live in
`shared/`; the Swift code is not portable, but the fixtures, translations,
naming rules, prompt text, and help content are meant to be reused.

- The behavioral spec is the macOS app (`mac/HearsayCore` sources and
  tests) plus the Python tool (`run_whisper.py`, and
  `run_whisper_windows.py` for Windows device and path handling, in the
  sibling `whisper-tools` checkout). Port a rule from the Swift code and
  its test, and name the Swift file in the C# file's summary comment.
- Where Windows must behave differently (PLAN.md 18.4 and 18.8), the
  difference is written in PLAN.md first.
- A behavior change that both platforms share updates the `shared/`
  file and its vectors first, then both apps.
- Agents use `.claude/agents/windows-implementer.md` for Windows work items.
