# GitHub Actions

Four workflows. `ci.yml` and `release.yml` build the Mac app on the
`macos-26` Apple Silicon runner with the newest stable Xcode installed there
(selected by `.github/actions/setup-mac`); `windows-ci.yml` and
`windows-release.yml` build the Windows version on `windows-latest` (see
"Windows" below). A pushed `v*` tag runs both release workflows, which
publish one GitHub release. None needs or downloads a speech model.
PLAN.md sections 4.7 and 18.4 ("Updates and packaging") have the reasons.

## ci.yml: every pull request and every push to `main`

- `core-tests`: `swift test` in `mac/HearsayCore`.
- `app-build`: installs XcodeGen and, if missing, the Metal Toolchain;
  builds Release with `mac/Scripts/run-debug.sh --release --no-open --ci`;
  checks the version keys in the built Info.plist; runs the HearsayWhisper
  unit tests (the integration tests skip without
  `TEST_RUNNER_HEARSAY_MODEL_DIR`); runs `Scripts/export-strings.py` and
  fails if `shared/localization/strings-en.json` changed or a translation
  is incomplete (`merge-translations.py --check`); runs
  `Scripts/make-notices.sh` and fails if `mac/THIRD_PARTY_NOTICES.md`
  changed. SwiftPM checkouts are cached by the `Package.resolved` files.

## release.yml: a pushed tag `v*`

```bash
git tag v0.2.0
git push origin v0.2.0
```

1. `MARKETING_VERSION` is the tag without `v` (`0.2.0`), and
   `CURRENT_PROJECT_VERSION` is the workflow run number. A tag with a
   suffix (`v0.3.0-beta.1`) becomes a GitHub pre-release, which the in-app
   update check never offers (GitHub's "latest release" skips them).
2. Builds Release with those values and checks them in the built
   Info.plist.
3. `mac/Scripts/make-dmg.sh` writes `dist/Hearsay-<version>.dmg`
   (Hearsay.app and an Applications link), then `SHA256SUMS.txt`.
4. Publishes the release with both files and generated release notes, and
   an install section that says whether the build is notarized.

### Signing and notarization (off by default)

Without secrets the app is ad-hoc signed, which is the normal case today:
there is no Apple Developer account yet. The release notes then tell users
how to open it the first time (macOS 14: right-click > Open; macOS 15 and
later: System Settings > Privacy & Security > Open Anyway).

The signing steps run only when the `MACOS_CERTIFICATE_P12` secret exists;
notarization additionally needs `APPLE_ID`, `APPLE_TEAM_ID`, and
`APPLE_APP_PASSWORD`. Today the secrets hold a **self-signed** certificate
("Hearsay Code Signing (self-signed)", created 2026-09-28, valid to 2036,
master copy in the owner's login keychain). It does not satisfy Gatekeeper,
but it gives every build the same code-signing identity, so macOS keeps the
Microphone and Screen & System Audio Recording permissions across updates.
Replacing it with a Developer ID certificate needs no workflow change.
Add these repository secrets (Settings > Secrets and variables > Actions):

| Secret | What it is |
|---|---|
| `MACOS_CERTIFICATE_P12` | The "Developer ID Application" certificate and its private key, exported from Keychain Access as .p12, then `base64 -i cert.p12 \| pbcopy` |
| `MACOS_CERTIFICATE_PASSWORD` | The password chosen when exporting the .p12 |
| `MACOS_SIGNING_IDENTITY` | Optional. The identity name, for example `Developer ID Application: Chihling Wang (TEAMID)`; defaults to the first "Developer ID Application" identity |
| `APPLE_ID` | The Apple ID email of the developer account |
| `APPLE_TEAM_ID` | The 10-character team ID |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID (appleid.apple.com > Sign-In and Security) |

With them, the workflow imports the certificate into a temporary keychain,
signs the app with `codesign --deep --options runtime --timestamp` and the
app's entitlements, builds the DMG, signs it, notarizes it with
`xcrun notarytool submit --wait`, staples the ticket, and deletes the
keychain.

## Windows

### windows-ci.yml: every pull request and every push to `main`

One job on `windows-latest` with the .NET 10 SDK (`actions/setup-dotnet`):

- builds `windows\Hearsay.slnx` in Release (warnings are errors through
  `windows/Directory.Build.props`), which includes the WinUI app;
- runs `dotnet test windows\Hearsay.Tests` (the Whisper integration tests
  skip without `TEST_RUNNER_HEARSAY_MODEL_DIR`, the capture tests skip
  without audio devices; the update tests run the swap helper in Windows
  PowerShell against scratch folders);
- runs `windows\scripts\make-notices.ps1 -Check` and fails if
  `windows/THIRD_PARTY_NOTICES.md` changed;
- runs `python windows\scripts\import-strings.py --check` and fails if a
  `.resw` file is stale or a translation is missing.

NuGet packages are cached by the hash of the project files. Test results
(`.trx`) are uploaded when a step fails.

### windows-release.yml: a pushed tag `v*`

Runs on the same tag as `release.yml` and adds the Windows build to the
same release:

1. The version is the tag without `v`, with the same rule as `release.yml`
   (`v1.2.3` or `v1.2.3-beta.1`; a suffix makes a pre-release).
2. Builds `windows\Hearsay.slnx` in Release with `-p:Version=<version>` and
   runs the core and App tests, as `windows-ci.yml` does.
3. `windows\scripts\make-release.ps1` builds the app again in a clean
   folder (`dist\build`), checks that `Hearsay.exe` has ProductName
   `Hearsay` and ProductVersion `<version>`, signs `Hearsay.exe` and the
   `Hearsay*.dll` files when the secrets exist, and writes
   `dist\Hearsay-<version>-win-x64.zip` (one `Hearsay\` folder inside, no
   `.pdb` files) and its `SHA256SUMS.txt` line.
4. **Coordination with the Mac release:** it waits (checking every minute,
   up to 45 minutes) until `gh release view <tag>` shows the release with
   `Hearsay-<version>.dmg` and `SHA256SUMS.txt`, i.e. until `release.yml`
   has published. Then it downloads that `SHA256SUMS.txt`, adds the zip's
   line (replacing an older line for the same zip), and uploads the zip and
   the merged file with `gh release upload --clobber`. The in-app update on
   both platforms reads this one file.
5. Adds a "Windows" section to the release notes (download, extract to e.g.
   `%LOCALAPPDATA%\Programs`, run `Hearsay.exe`, the SmartScreen "More
   info > Run anyway" step, and `Get-FileHash` against `SHA256SUMS.txt`),
   before GitHub's generated "What's Changed" part. The section sits
   between `<!-- hearsay-windows:start -->` and `<!-- hearsay-windows:end -->`
   markers, so a re-run replaces it instead of adding a second one.

If the Mac release has not appeared after 45 minutes, the job creates the
release itself (`gh release create`, same title, generated notes), with a
`SHA256SUMS.txt` holding only the zip's line and a note in the body. If
`release.yml` publishes later, it overwrites the notes and
`SHA256SUMS.txt`; re-run `windows-release.yml` for the tag to add the
Windows part back. Re-running is always safe.

### Windows signing

Releases are signed with a **self-signed** code-signing certificate,
"Hearsay Code Signing (self-signed)", the Windows counterpart of the Mac's
(PLAN.md 4.7). Windows does not trust it, so SmartScreen still asks on the
first launch of a downloaded zip, but every release has the same signer
and the in-app update checks that a new `Hearsay.exe` has the running
build's certificate (SHA-256 thumbprint). Create it once, on the owner's
machine, and keep the PFX and its password outside the repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows\scripts\make-signing-cert.ps1 -PfxPath "$env:USERPROFILE\Documents\hearsay-code-signing.pfx"
```

It creates the certificate in `Cert:\CurrentUser\My` (RSA 3072, SHA-256,
10 years), asks for a password, exports the PFX, and prints its base64 and
the thumbprints. It refuses to run if a certificate with that subject
already exists (`-Force` makes another one, which breaks in-app updates
from builds signed with the old one). Add these repository secrets:

| Secret | What it is |
|---|---|
| `WINDOWS_CERTIFICATE_PFX` | The base64 of the PFX, one line, as `make-signing-cert.ps1` prints it |
| `WINDOWS_CERTIFICATE_PASSWORD` | The PFX password chosen when running the script |

With both, the workflow decodes the PFX into `$RUNNER_TEMP`, signs through
`make-release.ps1` (`signtool sign /fd SHA256`, no timestamp: it adds
nothing to a self-signed certificate), checks each signature
(`signtool verify /pa` reports the untrusted self-signed root, which the
script accepts; the signer must be the PFX's certificate), and deletes the
PFX in a step that always runs. **Without them** the zip is unsigned: the
log and the job summary say so, and the release notes say the build is
not signed. An unsigned release cannot be installed from inside a signed
copy of Hearsay (the signer check refuses it); users download it instead.
If Azure Trusted Signing is bought later, only the signing step changes.
