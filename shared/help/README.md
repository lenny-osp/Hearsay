# Help pages

`<lang>/Help.html` is the in-app help for each interface language (en, de,
es, zh-Hant, zh-Hans). These are the only copies to edit: translators and
agents change the files here, never anything else.

- macOS: `mac/Scripts/sync-shared.sh` copies each page to
  `mac/Hearsay/Resources/<lang>.lproj/Help.html`, where Xcode bundles it.
  `Scripts/run-debug.sh` and `Scripts/generate-project.sh` run it first. The
  copies are git-ignored and overwritten on every build; an edit there is
  lost. The script fails if a language folder here has no `Help.html`, or
  if the app has a `.lproj` folder with no page here.
- Windows (planned): rendered from this folder in a WebView2 window
  (PLAN.md section 18.3).

Each page is self-contained: the CSS is embedded in its `<style>` block
and must stay identical across languages. The comment at the top of every
page lists what translators may change. UI labels quoted in
`<span class="ui">` must match the app's string catalogs
(`shared/localization/<lang>.json`, see `GLOSSARY.md`). Links of the form
`hearsay://open/<tab>` open a tab of the main window.
