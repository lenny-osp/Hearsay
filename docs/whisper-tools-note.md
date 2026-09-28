# Note for the whisper-tools guides

Paste this paragraph into the whisper-tools macOS manual and quick starts:

> **On a Mac, try Hearsay.** Hearsay is a native macOS app that does what `run_whisper.py` does, without Python or FFmpeg.
> It records the microphone and call audio, transcribes on the Mac, and writes the same `<timestamp>_<name>.srt`, `.md`, and `_transcript.md` files using the same meeting-note prompt.
> The Copilot CLI path is not available in the app; for meeting notes, use OpenAI, Anthropic, Azure OpenAI, a local Ollama, or any OpenAI-compatible endpoint.
> To install it, see the README in the Hearsay repository.
