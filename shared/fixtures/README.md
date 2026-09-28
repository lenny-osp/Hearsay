# Test fixtures

- `en-30s.wav`: 18.9 s of synthesized English (macOS `say`, voice Samantha), 16 kHz mono s16le.
- `zh-30s.wav`: 29.4 s of Mandarin read by the owner (the opening of 老舍's
  《北京的春節》), 16 kHz mono s16le. Replaced 2026-09-28; the first version
  was a noisy clip.
- `zh-30s.truth.srt`: the owner's hand-corrected transcript of `zh-30s.wav`
  (same timings as the expected file). Not used by the parity tests; it is
  the ground truth for accuracy measurements, for example the Windows
  acceptance in PLAN.md section 18.5. It differs from the model output in
  two characters (臘七 vs 臘漆, 裏 vs 里).
- `de-30s.wav`: 22.0 s of synthesized German meeting talk (macOS `say`, voice Anna, de_DE), 16 kHz mono s16le.
- `es-30s.wav`: 21.6 s of synthesized Spanish meeting talk (macOS `say`, voice `Eddy (Spanish (Spain))`, es_ES), 16 kHz mono s16le.
- `*.expected.srt`: reference output from Python `mlx_whisper` 0.4.3 with
  `mlx-community/whisper-large-v3-turbo`, `--condition-on-previous-text False
  --hallucination-silence-threshold 2.0`, temperature fallback defaults.
  `en`, `de` and `es` used `--language en` / `de` / `es`. `zh` used `--language zh` and **no initial
  prompt** (see PLAN.md section 6 for why).

Regenerate with:

```bash
# de / es audio
say -v Anna -o /tmp/de.aiff "Guten Morgen zusammen. Lasst uns mit dem Stand des Projekts beginnen. Das neue Release ist für nächsten Donnerstag geplant, aber wir haben noch zwei offene Fehler im Anmeldebereich. Markus, kannst du bis Mittwoch eine Lösung vorschlagen? Außerdem müssen wir das Budget für das dritte Quartal besprechen. Ich schicke euch nach dem Meeting eine Zusammenfassung per E-Mail."
say -v 'Eddy (Spanish (Spain))' -o /tmp/es.aiff "Buenos días a todos. Empecemos con el estado del proyecto. La nueva versión está prevista para el próximo jueves, pero todavía tenemos dos errores abiertos en la pantalla de inicio de sesión. Lucía, ¿puedes proponer una solución antes del miércoles? Además, tenemos que revisar el presupuesto del tercer trimestre. Os enviaré un resumen por correo después de la reunión."
afconvert -f WAVE -d LEI16@16000 -c 1 /tmp/de.aiff shared/fixtures/de-30s.wav
afconvert -f WAVE -d LEI16@16000 -c 1 /tmp/es.aiff shared/fixtures/es-30s.wav

mlx_whisper shared/fixtures/en-30s.wav --model mlx-community/whisper-large-v3-turbo --language en \
  --condition-on-previous-text False --hallucination-silence-threshold 2.0 --output-format srt --output-dir /tmp/exp
mlx_whisper shared/fixtures/zh-30s.wav --model mlx-community/whisper-large-v3-turbo --language zh \
  --condition-on-previous-text False --hallucination-silence-threshold 2.0 --output-format srt --output-dir /tmp/exp
mlx_whisper shared/fixtures/de-30s.wav --model mlx-community/whisper-large-v3-turbo --language de \
  --condition-on-previous-text False --hallucination-silence-threshold 2.0 --output-format srt --output-dir /tmp/exp
mlx_whisper shared/fixtures/es-30s.wav --model mlx-community/whisper-large-v3-turbo --language es \
  --condition-on-previous-text False --hallucination-silence-threshold 2.0 --output-format srt --output-dir /tmp/exp
```

Language-detection references in `IntegrationTests.pythonDetection` come
from `model.detect_language(pad_or_trim(log_mel_spectrogram(audio,
padding=N_SAMPLES), N_FRAMES).astype(mx.float16))` with the same model, the
path `transcribe(language=None)` takes.
