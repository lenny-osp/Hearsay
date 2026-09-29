# Hearsay for Windows

In progress; no code yet. The design is in [PLAN.md](../PLAN.md), section
18 "Windows version": goal, layout, stack (C# on .NET 10 / WinUI 3, WASAPI,
whisper.cpp through Whisper.net), what `shared/` provides, acceptance
thresholds, phases, risks, and the porting gaps still open (18.8). Read
[AGENTS.md](../AGENTS.md) before working here.

## Prerequisites

- Windows 11 x64, Git for Windows
- .NET 10 SDK
- Visual Studio 2026 with ".NET desktop development" and "WinUI
  application development"
- Developer Mode on (Settings > System > For developers) to deploy
  packaged debug builds
- Optional: an NVIDIA GPU and the CUDA Toolkit for the CUDA runtime

A checkout made before the root `.gitattributes` existed has CRLF text
files; refresh it once (PLAN.md 18.2, "Line endings").

Planned layout:

```text
windows/
  Hearsay.sln
  Hearsay.App/       WinUI 3 app
  Hearsay.Core/      ported logic, tested with the shared vectors
  Hearsay.Whisper/   whisper.cpp integration and model store
  Hearsay.Tests/
  Spike/             W1 spike; models/ holds local test models (git-ignored)
  scripts/
  THIRD_PARTY_NOTICES.md
```
