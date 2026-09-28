#!/usr/bin/env python3
"""Regenerates shared/naming-tests.json from the whisper-tools Python CLI.

Usage, from the repo root:
    python3 shared/scripts/make-naming-tests.py [path/to/whisper-tools]

The whisper-tools checkout defaults to a sibling folder of this repo. Every
expected value is computed by the Python reference; the macOS tests
(NamingTests.swift, "shared vectors") then prove the Swift port agrees.
"""
import contextlib
import io
import json
import os
import sys
import tempfile

sys.dont_write_bytecode = True
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WHISPER_TOOLS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.dirname(ROOT)), "whisper-tools")
sys.path.insert(0, WHISPER_TOOLS)
import run_whisper as rw  # noqa: E402

sanitize_inputs = [
    # test_meeting_names_are_lowercase_ascii_and_filename_safe (verbatim)
    " Biweekly Cross Country Resource Management ",
    "Planning / Budget: Q4? <2026> | * Review\\Draft",
    "Café__ＲＥＶＩＥＷ\t會議 😀.MD",
    "../../Launch---Plan.srt",
    "會議😀 /:*?",
    "   ",
    "a" * 100,
    # test_ai_result_parsing_and_filename_sanitizing
    " Launch / Plan.md ",
    # test_meeting_name_default_override_and_invalid_retry
    "Launch Plan", "會議?!", "New / Launch PLAN", "會議", "Team Review",
    # NamingTests.swift extra inputs
    "Straße Ölfeld", "Ｑ４ ﬁnal", "İstanbul—Plan", "x.Srt  ", "notes.md.md", "-a-.txt",
    "日本 Meeting 2", "ǅemal", "a" * 79 + " b",
    # OutputWriter tests
    "Launch Plan!", "會議 ?", "genhe road trip", "Genhe Road Trip", "new name",
]
sanitize = [{"input": s, "output": rw.sanitize_ai_filename(s)} for s in sanitize_inputs]

insert_cases = [
    # test_meeting_name_is_first_content_under_first_heading (verbatim)
    ("# Structured Transcript\n\nDiscuss launch\n\n## Decisions\nShip it.\n", "launch-plan", "Structured Transcript"),
    ("# Structured Transcript\n\n**Meeting Name:** old-name\n\nDiscuss launch\n\n## Decisions\nShip it.\n", "launch-plan", "Structured Transcript"),
    ("Discuss launch", "launch-plan", "Structured Transcript"),
    # NamingTests.swift
    ("# Notes\n**meeting name:** old\n## Later\n**Meeting Name:** keep\n", "new", "Meeting Notes"),
    ("  ## Agenda\r\nItem\r\n", "n", "F"),
    ("Intro\n#NoSpace\n# Real\n.**Meeting Name:** x\nBody", "n", "F"),
    ("\n\n  text  ", "n", "F"),
    ("# Notes\n", "x", "Meeting Notes"),
    # OutputWriter tests
    ("# Notes\n\nBody\n", "launch-plan", "Meeting Notes"),
    ("Speaker: hi", "launch-plan", "Structured Transcript"),
    ("# Notes\n\nNew\n", "trip-to-genhe", "Meeting Notes"),
    ("# N", "genhe-road-trip", "Meeting Notes"),
]
insert = [{"markdown": m, "name": n, "fallbackHeading": f, "output": rw.insert_meeting_name(m, n, f)}
          for m, n, f in insert_cases]

missing_dir = "/nonexistent-hearsay-vectors"
timestamp_inputs = [
    "2025-04-03_14-05-06_customer-call.wav",
    "customer-call-2025-04-03-14-05-06.wav",
    "20250403T140506.wav",
    "2025-02-30_14-05-06.wav",
    "2025-04-03_24-05-06.wav",
    "12025-04-03_14-05-06.wav",
    "meeting.wav",
    "2026-09-03-14-05-06.srt",
    "2026-09-03_14-05-06.srt",
    "2024-02-29_00-00-00.wav",
    "2023-02-29_00-00-00.wav",
    "call 2025-04-03 14:05:06.m4a",
    "2025-02-30_14-05-06_then_2025-04-03_14-05-06.wav",
    "2025-04-03_14-05-061.wav",
]
timestamps = [{"filename": f, "timestamp": rw.source_file_timestamp(os.path.join(missing_dir, f))}
              for f in timestamp_inputs]

output_cases = [
    ("2026-09-03_14-05-06", "Launch Plan!", []),
    ("2020-01-01_00-00-00", "launch", ["2020-01-01_00-00-00_launch_transcript.md"]),
    ("2026-09-03_14-05-06", "launch", ["2026-09-03_14-05-06_launch.srt"]),
    ("2026-09-28_11-49-45", "launch", ["2026-09-28_11-49-45_launch.md"]),
    ("2026-09-03_14-05-06", "launch", ["2026-09-03_14-05-06_launch.srt", "2026-09-03_14-05-06_launch-2.md"]),
    ("2026-09-03_14-05-06", "launch", ["2026-09-03_14-05-06_launch.txt", "2026-09-03_14-05-06_launch-notes.md"]),
    ("2026-09-03_14-05-06", "launch", ["2026-09-03_14-05-06_launch-2.srt"]),
    ("2026-09-28_11-49-45", "Genhe Road Trip", ["2026-09-28_11-49-45_trip-to-genhe.srt"]),
]
output_names = []
for ts, name, existing in output_cases:
    with tempfile.TemporaryDirectory() as d:
        for e in existing:
            open(os.path.join(d, e), "w").close()
        src = os.path.join(d, "source.srt")
        open(src, "w").write("1\n")
        with contextlib.redirect_stderr(io.StringIO()):
            result = rw.save_named_outputs(src, name, "# N", "# T", timestamp=ts)
        stem = os.path.basename(result[0])[:-len(".srt")]
    output_names.append({"timestamp": ts, "name": name, "existing": existing, "stem": stem})

doc = {
    "about": (
        "Naming-rule test vectors shared by the macOS and Windows apps. Expected values come from "
        "the whisper-tools Python CLI (sanitize_ai_filename, insert_meeting_name, "
        "source_file_timestamp on a path that does not exist, save_named_outputs with the given "
        "timestamp and a source SRT named source.srt beside the existing files, no retained WAV). Hearsay also counts a retained <stem>.wav as a collision when the source SRT has one (PLAN.md 4.3 step 7); that case is covered in mac NamingTests.swift, not here. Regenerate with shared/scripts/make-naming-tests.py."
    ),
    "sanitize": sanitize,
    "insertMeetingName": insert,
    "timestampFromFilename": timestamps,
    "outputNames": output_names,
}
with open(os.path.join(ROOT, "naming-tests.json"), "w", encoding="utf-8") as f:
    json.dump(doc, f, ensure_ascii=False, indent=2)
    f.write("\n")
print(f"wrote naming-tests.json: {len(sanitize)} sanitize, {len(insert)} insertMeetingName, "
      f"{len(timestamps)} timestampFromFilename, {len(output_names)} outputNames")
