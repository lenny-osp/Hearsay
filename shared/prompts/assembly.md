# Meeting-notes prompt

The text files in this folder are the exact bytes both platforms send.
They have no trailing newline; keep it that way (the macOS tests in
`mac/HearsayCore/Tests/HearsayCoreTests/NotesTests.swift` compare them
byte for byte with the compiled-in Swift constants).

| File | Contents |
|---|---|
| `general-meeting.txt` | the built-in "General meeting" template; `{output_language}` appears four times |
| `response-rules.txt` | the fixed filename and JSON-shape rules, appended after any template |
| `system-message.txt` | the system message sent with the prompt |
| `languages.json` | transcript language code to the name put into `{output_language}` |

## Assembly

1. Look up the transcript language code in `languages.json`. An unknown
   code is an error (`unsupported meeting-note language: <code>`); nothing
   is sent.
2. Take the template instructions (`general-meeting.txt`, or a user
   template) and replace every `{output_language}` with that name.
3. The user message is exactly:

   ```text
   <template> + "\n\n" + <response-rules.txt> + "\n\nThe source SRT is below:\n---\n" + <transcript> + "\n---"
   ```

   `<transcript>` is the SRT text unchanged.
4. Send it with `system-message.txt` as the system message. How each
   provider carries the system message (a chat `system` role, a CLI flag,
   prepended text, or not at all) is provider code, not part of this
   contract; on macOS see `CLIClient.swift` and `ChatCompletionsClient.swift`.

With the built-in template, `en` and `zh-TW` produce prompts byte-identical
to `build_meeting_prompt(transcript, "en" | "zh")` in the whisper-tools
Python CLI (Python "zh" is Hearsay's "zh-TW").

## Reply contract

The model returns one JSON object:

```json
{"filename": "short-descriptive-name", "markdown": "the complete meeting notes in Markdown", "transcript_markdown": "the complete structured transcript in Markdown"}
```

Validation (port of `parse_ai_result`), in this order:

1. Trim surrounding whitespace. If the whole reply is one code fence,
   ```` ```json ... ``` ```` or ```` ``` ... ``` ```` (the `json` tag is
   case-insensitive), use the text inside it, trimmed.
2. It must parse as JSON, else "AI output is not valid JSON: <detail>".
3. It must be an object, else "AI output must be a JSON object."
4. `filename`, then `markdown`, then `transcript_markdown` must each be a
   string with at least one non-whitespace character; the first that is
   missing, not a string, or blank fails with, respectively, "AI output did
   not contain a usable filename.", "... usable Markdown notes.", "... a
   usable structured transcript."
5. Values are used as returned, not trimmed. Each message is shown after
   "Parsing Error: ".

`filename` then goes through the naming rules (`shared/naming-tests.json`,
`sanitize`) before any file is written.
