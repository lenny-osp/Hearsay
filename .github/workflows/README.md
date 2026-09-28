# GitHub Actions

Two workflows, both on the `macos-26` Apple Silicon runner with the newest
stable Xcode installed there (selected by `.github/actions/setup-mac`).
Neither needs or downloads a speech model. PLAN.md section 4.7 has the
reasons.

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
