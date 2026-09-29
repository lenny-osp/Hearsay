#!/usr/bin/env python3
"""Merge translator files into the string catalogs.

Reads ../shared/localization/<lang>.json for each language (de, es, zh-Hant, zh-Hans,
or the ones named on the command line). Each file is the array of
../shared/localization/strings-en.json with a "translation" added to every entry:

    {"catalog": "app|core|infoplist|credits", "key": "...", "english": "...",
     "comment": "...", "placeholders": ["%@"], "translation": "..."}

Every file is checked before anything is written. The merge fails when a
file misses a key or has one the catalogs do not have (or has it twice),
when an entry's English no longer matches the catalog (re-export and
re-translate it), when a translation is empty, or when its placeholders
differ from the English ones (same placeholders, same count; %1$@-style
positions may reorder them). Keys marked "Do not translate" must repeat the
English exactly and are not written.

Then each translation is written into its catalog's "localizations" with
state "translated", and the credits entry becomes
Hearsay/Resources/<lang>.lproj/Credits.rtf. Run Scripts/generate-project.sh
afterwards so the project lists the new .lproj folders.

Usage, from mac/:
    Scripts/merge-translations.py                 # every ../shared/localization/<lang>.json present
    Scripts/merge-translations.py de zh-Hant      # only these
    Scripts/merge-translations.py --input DIR de  # read DIR/de.json instead
    Scripts/merge-translations.py --check de      # validate only
"""
import argparse
import importlib.util
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LANGUAGES = ["de", "es", "zh-Hant", "zh-Hans"]

# Share the catalog list, placeholder rule, and Xcode-style JSON writer with
# the export script.
_spec = importlib.util.spec_from_file_location("export_strings", Path(__file__).with_name("export-strings.py"))
export_strings = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(export_strings)
CATALOGS = export_strings.CATALOGS
CREDITS = export_strings.CREDITS
CREDITS_KEY = export_strings.CREDITS_KEY
PLACEHOLDER = export_strings.PLACEHOLDER


class MergeError(Exception):
    pass


def load_catalogs():
    return {name: json.loads(path.read_text(encoding="utf-8")) for name, path in CATALOGS.items()}


def expected_entries(catalogs):
    """{(catalog, key): (english, translatable)} for every catalog key plus
    the credits text."""
    expected = {}
    for name, catalog in catalogs.items():
        source = catalog.get("sourceLanguage", "en")
        for key, entry in catalog.get("strings", {}).items():
            english = export_strings.english_value(key, entry, source)
            expected[(name, key)] = (english, entry.get("shouldTranslate") is not False)
    expected[("credits", CREDITS_KEY)] = (export_strings.credits_text(), True)
    return expected


def parse_placeholders(text):
    """[(position or None, spec without position)] in order. Raises on a
    lone % that is neither %% nor a placeholder."""
    stripped = text.replace("%%", "")
    found = []
    last = 0
    for match in PLACEHOLDER.finditer(stripped):
        if "%" in stripped[last:match.start()]:
            raise MergeError(f"stray % in {text!r} (write %% for a percent sign)")
        last = match.end()
        spec = match.group(0)
        position = int(match.group(1)) if match.group(1) else None
        if position is not None:
            spec = "%" + spec[len(match.group(1)) + 2:]
        found.append((position, spec))
    if "%" in stripped[last:]:
        raise MergeError(f"stray % in {text!r} (write %% for a percent sign)")
    return found


def check_placeholders(english, translation):
    wanted = [spec for _, spec in parse_placeholders(english)]
    uses_format = bool(wanted) or "%%" in english
    if not uses_format:
        # Plain text: a percent sign is just a character.
        return
    got = parse_placeholders(translation)
    positions = [position for position, _ in got]
    if any(position is not None for position in positions):
        if any(position is None for position in positions):
            raise MergeError("mixes numbered (%1$@) and plain (%@) placeholders")
        if sorted(positions) != list(range(1, len(wanted) + 1)):
            raise MergeError(f"numbered placeholders {sorted(positions)} do not cover 1…{len(wanted)} once each")
        for position, spec in got:
            if wanted[position - 1] != spec:
                raise MergeError(f"%{position}$ is {spec} but the English placeholder is {wanted[position - 1]}")
    elif [spec for _, spec in got] != wanted:
        raise MergeError(f"placeholders {[spec for _, spec in got]} differ from the English {wanted}")


def validate(language, entries, expected):
    if not isinstance(entries, list):
        raise MergeError(f"{language}: the file must hold a JSON array")
    seen = {}
    problems = []
    for index, entry in enumerate(entries):
        where = f"{language} entry {index}"
        if not isinstance(entry, dict) or not all(k in entry for k in ("catalog", "key", "english", "translation")):
            problems.append(f"{where}: needs catalog, key, english, and translation")
            continue
        identity = (entry["catalog"], entry["key"])
        label = f"{language} {entry['catalog']}:{entry['key']!r}"
        if entry["catalog"] == "windows":
            # Windows-only strings share these files (PLAN.md 18.3,
            # Localization). They have no Mac catalog; the Windows side
            # checks them with windows/scripts/import-strings.py --check.
            continue
        if identity in seen:
            problems.append(f"{label}: listed twice")
            continue
        if identity not in expected:
            problems.append(f"{label}: extra key, not in the catalogs")
            continue
        english, translatable = expected[identity]
        translation = entry["translation"]
        seen[identity] = translation
        if entry["english"] != english:
            problems.append(f"{label}: the English changed since the export (now {english!r})")
        if not isinstance(translation, str) or not translation.strip():
            problems.append(f"{label}: empty translation")
            continue
        if not translatable and translation != english:
            problems.append(f"{label}: must not be translated; repeat {english!r}")
        try:
            check_placeholders(english, translation)
        except MergeError as error:
            problems.append(f"{label}: {error}")
    for identity in sorted(set(expected) - set(seen)):
        problems.append(f"{language} {identity[0]}:{identity[1]!r}: missing key")
    if problems:
        raise MergeError("\n".join(problems))
    return seen


COFFEE_TEXT = "Buy Me a Coffee"
COFFEE_URL = "https://buymeacoffee.com/chihlingw"


def rtf_escape(text):
    out = []
    for char in text:
        code = ord(char)
        if char in "\\{}":
            out.append("\\" + char)
        elif char == "\n":
            out.append("\\\n")
        elif code < 0x80:
            out.append(char)
        else:
            # RTF \u takes a signed 16-bit value; characters beyond the BMP
            # are written as a UTF-16 surrogate pair.
            units = [code] if code <= 0xFFFF else [
                0xD800 + ((code - 0x10000) >> 10), 0xDC00 + ((code - 0x10000) & 0x3FF)]
            for unit in units:
                out.append(f"\\u{unit - 0x10000 if unit > 0x7FFF else unit}?")
    return "".join(out)


def credits_rtf(text):
    """The English Credits.rtf with its text replaced: same fonts, colors,
    size, and centering."""
    english = CREDITS.read_text(encoding="utf-8")
    marker = "\\cf0 "
    head = english[: english.index(marker) + len(marker)]
    body = rtf_escape(text.replace("\r\n", "\n"))
    # The brand name becomes a clickable link in the About panel.
    link = '{\\field{\\*\\fldinst{HYPERLINK "' + COFFEE_URL + '"}}{\\fldrslt ' + COFFEE_TEXT + '}}'
    body = body.replace(COFFEE_TEXT, link, 1)
    return head + body + "}"


def merge(language, translations, catalogs):
    for (name, key), translation in translations.items():
        if name == "credits":
            continue
        entry = catalogs[name]["strings"][key]
        if entry.get("shouldTranslate") is False:
            continue
        entry.setdefault("localizations", {})[language] = {
            "stringUnit": {"state": "translated", "value": translation}
        }
    folder = CREDITS.parent.parent / f"{language}.lproj"
    folder.mkdir(exist_ok=True)
    (folder / "Credits.rtf").write_text(credits_rtf(translations[("credits", CREDITS_KEY)]), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("languages", nargs="*", help=f"any of {', '.join(LANGUAGES)}")
    parser.add_argument("--input", default=str(ROOT.parent / "shared" / "localization"), help="folder holding <lang>.json")
    parser.add_argument("--check", action="store_true", help="validate only; write nothing")
    args = parser.parse_args()
    folder = Path(args.input)

    languages = args.languages or [lang for lang in LANGUAGES if (folder / f"{lang}.json").exists()]
    unknown = [lang for lang in languages if lang not in LANGUAGES]
    if unknown:
        sys.exit(f"unknown language {', '.join(unknown)}; use {', '.join(LANGUAGES)}")
    if not languages:
        sys.exit(f"no <lang>.json in {folder}")

    catalogs = load_catalogs()
    expected = expected_entries(catalogs)
    validated = {}
    failures = []
    for language in languages:
        path = folder / f"{language}.json"
        try:
            entries = json.loads(path.read_text(encoding="utf-8"))
            validated[language] = validate(language, entries, expected)
        except FileNotFoundError:
            failures.append(f"{language}: {path} not found")
        except json.JSONDecodeError as error:
            failures.append(f"{language}: {path} is not valid JSON: {error}")
        except MergeError as error:
            failures.append(str(error))
    if failures:
        print("\n".join(failures), file=sys.stderr)
        sys.exit(1)
    if args.check:
        print(f"ok: {', '.join(languages)} ({len(expected)} entries each)")
        return
    for language, translations in validated.items():
        merge(language, translations, catalogs)
    for name, catalog in catalogs.items():
        export_strings.write_catalog(CATALOGS[name], catalog)
    print(f"merged {', '.join(languages)} into {len(CATALOGS)} catalogs and Credits.rtf")


if __name__ == "__main__":
    main()
