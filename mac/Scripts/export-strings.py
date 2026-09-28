#!/usr/bin/env python3
"""Sync the string catalogs from the last build and export the English
strings for translators.

1. Syncs Hearsay/Resources/Localizable.xcstrings (app) and
   HearsayCore/Sources/HearsayCore/Resources/Localizable.xcstrings (core)
   with the .stringsdata files the compiler wrote during the last xcodebuild
   (SWIFT_EMIT_LOC_STRINGS), using `xcrun xcstringstool sync`. Keys no longer
   in the source are removed.
2. Writes ../shared/localization/strings-en.json: one entry per key of the app, core,
   and InfoPlist catalogs plus the text of en.lproj/Credits.rtf, as
   {"catalog", "key", "english", "comment", "placeholders"}.

Usage, from mac/ (after Scripts/run-debug.sh or --release):
    Scripts/export-strings.py [--derived .build/derived] [--configuration Release]
    Scripts/export-strings.py --no-sync      # export only
"""
import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOGS = {
    "app": ROOT / "Hearsay/Resources/Localizable.xcstrings",
    "core": ROOT / "HearsayCore/Sources/HearsayCore/Resources/Localizable.xcstrings",
    "infoplist": ROOT / "Hearsay/Resources/InfoPlist.xcstrings",
}
CREDITS = ROOT / "Hearsay/Resources/en.lproj/Credits.rtf"
CREDITS_KEY = "Credits.rtf"
LOCALIZATION = ROOT.parent / "shared" / "localization"
EXPORT = LOCALIZATION / "strings-en.json"
# Where each synced catalog's .stringsdata files live under
# Build/Intermediates.noindex.
STRINGSDATA = {
    "app": "Hearsay.build/{configuration}/Hearsay.build",
    "core": "HearsayCore.build/{configuration}/HearsayCore-t.build",
}
CREDITS_COMMENT = (
    "Credits shown in the About Hearsay window (en.lproj/Credits.rtf). Plain text; "
    "line breaks are kept. 'Settings > General > Acknowledgements' names the Settings "
    "tab, its General section, and the Acknowledgements section: use the translated "
    "names of those labels."
)

# printf-style placeholders as Xcode writes them into catalog keys.
PLACEHOLDER = re.compile(
    r"%(?:(\d+)\$)?[-+ #0']*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:hh|h|ll|l|q|z|t|j|L)?[@dDuUxXoOfFeEgGcCsSaAp]"
)


def placeholders(text):
    """Placeholders of `text` in order, without positions (%1$@ -> %@).
    %% is a literal percent sign, not a placeholder."""
    found = []
    for match in PLACEHOLDER.finditer(text.replace("%%", "")):
        spec = match.group(0)
        if match.group(1):
            spec = "%" + spec[len(match.group(1)) + 2:]
        found.append(spec)
    return found


def stringsdata_files(derived, configuration, name):
    folder = Path(derived) / "Build/Intermediates.noindex" / STRINGSDATA[name].format(configuration=configuration)
    return folder, sorted(folder.rglob("*.stringsdata"))


def source_files(derived, configuration):
    """{catalog: {key: [source file names]}} from the .stringsdata files, to
    give translators context for keys without a developer comment."""
    found = {}
    for name in STRINGSDATA:
        _, files = stringsdata_files(derived, configuration, name)
        keys = found.setdefault(name, {})
        for file in files:
            data = json.loads(file.read_text(encoding="utf-8"))
            source = Path(data.get("source", "")).name
            for table in data.get("tables", {}).values():
                for item in table:
                    names = keys.setdefault(item["key"], [])
                    if source and source not in names:
                        names.append(source)
    return found


def sync(derived, configuration):
    for name in STRINGSDATA:
        folder, files = stringsdata_files(derived, configuration, name)
        if not files:
            sys.exit(f"no .stringsdata under {folder}; build {configuration} first")
        command = ["xcrun", "xcstringstool", "sync", str(CATALOGS[name])]
        for file in files:
            command += ["--stringsdata", str(file)]
        subprocess.run(command, check=True)
        # xcstringstool keeps keys it no longer sees as "stale"; drop them so
        # the catalog holds exactly the keys in the source.
        catalog = json.loads(CATALOGS[name].read_text(encoding="utf-8"))
        strings = catalog.get("strings", {})
        stale = [key for key, entry in strings.items() if entry.get("extractionState") == "stale"]
        for key in stale:
            del strings[key]
        write_catalog(CATALOGS[name], catalog)
        print(f"{name}: synced {len(files)} .stringsdata files, {len(strings)} keys, removed {len(stale)} stale")


def write_catalog(path, catalog):
    # Xcode's own formatting: two-space indent, " : " separators, sorted keys.
    text = json.dumps(catalog, ensure_ascii=False, indent=2, sort_keys=True, separators=(",", " : "))
    path.write_text(text + "\n", encoding="utf-8")


def english_value(key, entry, source_language):
    unit = entry.get("localizations", {}).get(source_language, {}).get("stringUnit")
    return unit["value"] if unit else key


def credits_text():
    result = subprocess.run(
        ["textutil", "-convert", "txt", "-stdout", str(CREDITS)],
        check=True, capture_output=True, text=True,
    )
    return result.stdout.strip()


def export(sources):
    entries = []
    for name, path in CATALOGS.items():
        catalog = json.loads(path.read_text(encoding="utf-8"))
        source = catalog.get("sourceLanguage", "en")
        for key in sorted(catalog.get("strings", {})):
            entry = catalog["strings"][key]
            english = english_value(key, entry, source)
            comment = entry.get("comment", "")
            if not comment and sources.get(name, {}).get(key):
                comment = "UI text in " + ", ".join(sorted(sources[name][key])) + "."
            if entry.get("shouldTranslate") is False:
                comment = (comment + " " if comment else "") + "Do not translate: repeat the English exactly."
            entries.append({
                "catalog": name,
                "key": key,
                "english": english,
                "comment": comment,
                "placeholders": placeholders(english),
            })
    english = credits_text()
    entries.append({
        "catalog": "credits",
        "key": CREDITS_KEY,
        "english": english,
        "comment": CREDITS_COMMENT,
        "placeholders": placeholders(english),
    })
    EXPORT.parent.mkdir(parents=True, exist_ok=True)
    EXPORT.write_text(json.dumps(entries, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    counts = {}
    for entry in entries:
        counts[entry["catalog"]] = counts.get(entry["catalog"], 0) + 1
    print(f"wrote {EXPORT.relative_to(ROOT.parent)}: {len(entries)} entries {counts}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--derived", default=str(ROOT / ".build/derived"))
    parser.add_argument("--configuration", default="Debug")
    parser.add_argument("--no-sync", action="store_true", help="only write the export")
    args = parser.parse_args()
    if not args.no_sync:
        sync(args.derived, args.configuration)
    export(source_files(args.derived, args.configuration))


if __name__ == "__main__":
    main()
