# Hearsay for Windows

Not started. The design is in [PLAN.md](../PLAN.md), section 18 "Windows
version": goal, layout, stack (C# / WinUI 3, WASAPI, whisper.cpp through
Whisper.net), what `shared/` provides, acceptance thresholds, phases, and
risks. Read [AGENTS.md](../AGENTS.md) before working here.

Planned layout:

```text
windows/
  Hearsay.sln
  Hearsay.App/       WinUI 3 app
  Hearsay.Core/      ported logic, tested with the shared vectors
  Hearsay.Whisper/   whisper.cpp integration and model store
  Hearsay.Tests/
  scripts/
  THIRD_PARTY_NOTICES.md
```
