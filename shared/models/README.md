# Model catalog

Each platform keeps its own list of downloadable Whisper models, because
the weight formats differ:

- macOS: MLX safetensors from `mlx-community` on Hugging Face, in
  `mac/HearsayCore/Sources/HearsayCore/Resources/ModelCatalog.json`
  (decoded by `ModelCatalog.swift`).
- Windows (planned): GGUF files from `ggerganov/whisper.cpp` on Hugging
  Face, in a list of its own under `windows/` (PLAN.md section 18.3).

Both lists follow the schema below so the Models tab looks and behaves the
same. Only the schema is shared; the lists are not.

## Schema

```json
{
  "tokenizer": { "repo": "openai/whisper-large-v3", "files": ["tokenizer.json", "..."] },
  "entries": [
    {
      "repo": "mlx-community/whisper-large-v3-turbo",
      "displayName": "Whisper Large v3 Turbo",
      "sizeBytes": 1613977880,
      "weightsFile": "weights.safetensors",
      "quantization": "fp16",
      "family": "large-v3-turbo",
      "recommended": true
    }
  ]
}
```

| Field | Type | Meaning |
|---|---|---|
| `tokenizer.repo` | string | Hugging Face repo the shared tokenizer files come from, fetched once |
| `tokenizer.files` | [string] | file names fetched from that repo |
| `entries[].repo` | string | Hugging Face repo of the model; also its stable id |
| `entries[].displayName` | string | name shown in the Models tab (a product name, not localized) |
| `entries[].sizeBytes` | integer | download size in bytes of everything fetched for the entry, as reported by the Hugging Face API |
| `entries[].weightsFile` | string | name of the weights file in the repo |
| `entries[].quantization` | string | `fp16`, `8bit`, or `4bit` on macOS; Windows uses the GGUF quantization names (for example `q5_0`, `q8_0`) |
| `entries[].family` | string | grouping key, one of `tiny`, `base`, `small`, `medium`, `large-v3-turbo`, `large-v3`, shown in that order |
| `entries[].recommended` | bool | exactly one entry is `true`; it is offered first |

On macOS an entry downloads `config.json` plus `weightsFile` from `repo`,
then the tokenizer files. A whisper.cpp GGUF model is a single file, so on
Windows `weightsFile` is the whole download and the tokenizer is built into
it; the Windows list may leave `tokenizer.files` empty.
