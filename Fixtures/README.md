# Test fixtures

- `en-30s.wav`: 18.9 s of synthesized English (macOS `say`, voice Samantha), 16 kHz mono s16le.
- `zh-30s.wav`: 27.6 s of Mandarin speech recorded by the owner, 16 kHz mono s16le.
- `*.expected.srt`: reference output from Python `mlx_whisper` 0.4.3 with
  `mlx-community/whisper-large-v3-turbo`, `--condition-on-previous-text False
  --hallucination-silence-threshold 2.0`, temperature fallback defaults.
  `en` used `--language en`. `zh` used `--language zh` and **no initial
  prompt** (see PLAN.md section 6 for why).

Regenerate with:

```bash
mlx_whisper Fixtures/en-30s.wav --model mlx-community/whisper-large-v3-turbo --language en \
  --condition-on-previous-text False --hallucination-silence-threshold 2.0 --output-format srt --output-dir /tmp/exp
mlx_whisper Fixtures/zh-30s.wav --model mlx-community/whisper-large-v3-turbo --language zh \
  --condition-on-previous-text False --hallucination-silence-threshold 2.0 --output-format srt --output-dir /tmp/exp
```
