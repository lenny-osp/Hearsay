<#
.SYNOPSIS
  Builds the Windows release zip Hearsay-<version>-win-x64.zip and its
  SHA256SUMS.txt line (PLAN.md 18.4, "Updates and packaging").

.DESCRIPTION
  The Windows counterpart of the Mac's build, signing and make-dmg.sh steps
  in .github/workflows/release.yml. Used by .github/workflows/windows-release.yml
  and locally.

    1. dotnet build windows\Hearsay.slnx -c Release -p:Version=<v>
       --artifacts-path <OutDir>\build: a clean build of its own, so no stale
       file from a development build reaches the zip, and a running dev copy
       of Hearsay.exe (which locks the usual bin folder) does not block it.
       dotnet build rather than dotnet publish: the app is already
       unpackaged and self-contained for win-x64 (PLAN.md 18.6, "Toolchain
       verified"), so its build output is the complete app, the same shape
       CI tests and the dev machine runs; publish would produce the same
       folder by a second route. The build folder is removed at the end.
    2. Checks the app's Hearsay.exe: ProductName "Hearsay" and ProductVersion
       (without the "+<commit>" suffix) equal to <v>, as the in-app update
       does (UpdateInstall.CheckIdentity).
    3. Copies the app's output folder to <OutDir>\stage\Hearsay without the
       .pdb files.
    4. With -PfxPath: signs Hearsay.exe and every Hearsay*.dll there with
       signtool sign /fd SHA256 /f <pfx> /p <password> (no timestamp unless
       -Timestamp: a timestamp adds nothing to a self-signed certificate),
       then checks each file: signtool verify /pa passes, or fails only
       because the root is not trusted (the expected result for the
       self-signed certificate: "signed, chain untrusted"), and
       Get-AuthenticodeSignature names the PFX's certificate as signer.
       Without -PfxPath the zip is unsigned, and the script says so.
    5. Zips the folder as <OutDir>\Hearsay-<v>-win-x64.zip with one top-level
       Hearsay\ folder, and writes <OutDir>\SHA256SUMS.txt with its line
       "<sha256>  Hearsay-<v>-win-x64.zip" (shasum's two-space format, LF).

  Windows PowerShell 5.1. Exits non-zero on any failure.

.PARAMETER Version
  The release version, as in the tag without "v": 1.2.3 or 1.2.3-beta.1.

.PARAMETER PfxPath
  The code-signing PFX (make-signing-cert.ps1 writes it). Omit for an
  unsigned zip.

.PARAMETER PfxPassword
  The PFX password, as a SecureString or plain string. Asked for when
  -PfxPath is given without it.

.PARAMETER OutDir
  Where the zip and SHA256SUMS.txt go; default dist at the repository root.
  Relative paths are relative to the repository root.

.PARAMETER Timestamp
  Also timestamp the signatures with -TimestampUrl (RFC 3161).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File windows\scripts\make-release.ps1 -Version 0.3.0
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File windows\scripts\make-release.ps1 -Version 0.3.0 -PfxPath C:\keys\hearsay-code-signing.pfx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PfxPath,
    [object]$PfxPassword,
    [string]$OutDir = 'dist',
    [switch]$Timestamp,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$InGitHubActions = $env:GITHUB_ACTIONS -eq 'true'

function Fail([string]$Message) {
    if ($InGitHubActions) { [Console]::Out.WriteLine("::error::make-release: $Message") }
    [Console]::Error.WriteLine("make-release: $Message")
    exit 1
}

# Runs a native tool and returns its stdout and stderr as strings. Windows
# PowerShell 5.1 turns redirected stderr into error records, which stop the
# script under $ErrorActionPreference = 'Stop'; signtool reports the
# untrusted root there.
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # An empty stderr line arrives as an error record whose text is the
        # exception type name.
        $lines = @(& $Exe @Arguments 2>&1 | ForEach-Object {
            $line = "$_"
            if ($line -eq 'System.Management.Automation.RemoteException') { '' } else { $line }
        })
        return New-Object PSObject -Property @{ Exit = $LASTEXITCODE; Lines = $lines; Text = ($lines -join "`n") }
    } finally {
        $ErrorActionPreference = $saved
    }
}

function Step([string]$Message) { Write-Output ''; Write-Output "== $Message" }

function Format-Size([long]$Bytes) { '{0:N1} MB' -f ($Bytes / 1MB) }

function ConvertFrom-Password([object]$Value) {
    if ($Value -is [System.Security.SecureString]) {
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
        try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    }
    if ($Value -is [string]) { return $Value }
    Fail 'the PFX password must be a string or a SecureString.'
}

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

# The same rule as the Mac's release.yml tag check.
if ($Version -notmatch '^[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.]+)?$') {
    Fail "'$Version' is not a version (expected 1.2.3 or 1.2.3-beta.1)."
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solution = Join-Path $repoRoot 'windows\Hearsay.slnx'
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $repoRoot $OutDir }
$OutDir = [IO.Path]::GetFullPath($OutDir)
$buildDir = Join-Path $OutDir 'build'
$appBin = Join-Path $buildDir 'bin\Hearsay.App'
$zipName = "Hearsay-$Version-win-x64.zip"
$zipPath = Join-Path $OutDir $zipName
$sumsPath = Join-Path $OutDir 'SHA256SUMS.txt'
$stageRoot = Join-Path $OutDir 'stage'
$stage = Join-Path $stageRoot 'Hearsay'

# ---- Signing inputs, checked before the build so a typo fails fast ----
$signing = -not [string]::IsNullOrEmpty($PfxPath)
$signtool = $null
$password = $null
$expectedThumbprint = $null
if ($signing) {
    if (-not (Test-Path -LiteralPath $PfxPath -PathType Leaf)) { Fail "the PFX $PfxPath does not exist." }
    $PfxPath = (Resolve-Path -LiteralPath $PfxPath).ProviderPath
    if ($null -eq $PfxPassword) { $PfxPassword = Read-Host -AsSecureString -Prompt 'PFX password' }
    $password = ConvertFrom-Password $PfxPassword
    try {
        $pfxCert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(
            $PfxPath, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
    } catch {
        Fail "the PFX could not be opened (wrong password?): $($_.Exception.Message)"
    }
    if (-not $pfxCert.HasPrivateKey) { Fail 'the PFX has no private key.' }
    $expectedThumbprint = $pfxCert.Thumbprint
    $expectedSha256 = Get-Sha256Hex $pfxCert.RawData
    $pfxSubject = $pfxCert.Subject
    $pfxCert.Dispose()

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidates = @(Get-ChildItem -Path $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'x64\signtool.exe')) } |
        Sort-Object { [version]$_.Name } -Descending)
    if ($candidates.Count -gt 0) {
        $signtool = Join-Path $candidates[0].FullName 'x64\signtool.exe'
    } elseif (Test-Path (Join-Path $kits 'x64\signtool.exe')) {
        $signtool = Join-Path $kits 'x64\signtool.exe'
    } else {
        Fail "signtool.exe was not found under $kits\<version>\x64. Install the Windows 10/11 SDK."
    }
}

# ---- 1. Build ----
Step "dotnet build windows\Hearsay.slnx -c Release -p:Version=$Version --artifacts-path $buildDir"
if (Test-Path -LiteralPath $buildDir) { Remove-Item -LiteralPath $buildDir -Recurse -Force }
& dotnet build $solution -c Release "-p:Version=$Version" --artifacts-path $buildDir -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit $LASTEXITCODE)." }

# <build>\bin\Hearsay.App\release_win-x64\Hearsay.exe (the App tests project
# has a folder and a copy of its own).
$exes = @(Get-ChildItem -Path $appBin -Filter Hearsay.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -like 'release*' })
if ($exes.Count -ne 1) { Fail "expected one Hearsay.exe under $appBin\release*, found $($exes.Count)." }
$appOut = $exes[0].DirectoryName
Write-Output "App output: $appOut"

# ---- 2. Identity ----
Step 'Checking ProductName and ProductVersion'
function Test-Identity([string]$Exe) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
    $productVersion = $info.ProductVersion
    $bare = if ($null -eq $productVersion) { $null } else { ($productVersion -split '\+', 2)[0] }
    Write-Output "ProductName    $($info.ProductName)"
    Write-Output "ProductVersion $productVersion"
    Write-Output "FileVersion    $($info.FileVersion)"
    if ($info.ProductName -ne 'Hearsay') { Fail "ProductName of $Exe is '$($info.ProductName)', not 'Hearsay'." }
    if ($bare -ne $Version) {
        Fail "ProductVersion of $Exe is '$productVersion', not '$Version'."
    }
}
Test-Identity $exes[0].FullName

# ---- 3. Stage ----
Step "Staging $stage"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($old in @($stageRoot, $zipPath, $sumsPath)) {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null
& robocopy $appOut $stage /E /XF *.pdb /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "copying the app folder failed (robocopy exit $LASTEXITCODE)." }
$global:LASTEXITCODE = 0
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$stageBytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Output ("{0} files, {1}" -f $files.Count, (Format-Size $stageBytes))

# ---- 4. Sign ----
$toSign = @((Join-Path $stage 'Hearsay.exe')) + @(Get-ChildItem -LiteralPath $stage -Filter 'Hearsay*.dll' -File | ForEach-Object { $_.FullName })
if ($signing) {
    Step "Signing $($toSign.Count) files with $pfxSubject"
    Write-Output "signtool: $signtool"
    $signArgs = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $password)
    if ($Timestamp) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') } else { Write-Output 'No timestamp (self-signed; pass -Timestamp to add one).' }
    $signResult = Invoke-Native $signtool ($signArgs + $toSign)
    $signExit = $signResult.Exit
    # Never echo the command line: it holds the password.
    $signResult.Lines | ForEach-Object { Write-Output "  $_" }
    if ($signExit -ne 0) { Fail "signtool sign failed (exit $signExit)." }

    Step 'Verifying the signatures'
    $untrustedRoot = 'terminated in a root certificate which is not trusted'
    foreach ($file in $toSign) {
        $name = Split-Path -Leaf $file
        $verifyResult = Invoke-Native $signtool @('verify', '/pa', $file)
        $verifyOutput = $verifyResult.Text
        $verifyExit = $verifyResult.Exit
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        $signer = $signature.SignerCertificate
        if ($null -eq $signer) { Fail "$name has no signature ($($signature.Status): $($signature.StatusMessage))." }
        if ($signer.Thumbprint -ne $expectedThumbprint) {
            Fail "$name is signed by $($signer.Subject) ($($signer.Thumbprint)), not by the PFX's certificate $expectedThumbprint."
        }
        if ($signature.Status -eq 'HashMismatch' -or $signature.Status -eq 'NotSigned') {
            Fail "$name signature status is $($signature.Status): $($signature.StatusMessage)"
        }
        if ($verifyExit -eq 0) {
            $verdict = 'signed, chain trusted'
        } elseif (($verifyOutput -replace '\s+', ' ') -match $untrustedRoot) {
            $verdict = 'signed, chain untrusted (self-signed root; expected)'
        } else {
            Fail "signtool verify /pa failed for ${name}:`n$verifyOutput"
        }
        Write-Output ("  {0,-22} {1}; Get-AuthenticodeSignature: {2}" -f $name, $verdict, $signature.Status)
    }
    Write-Output "Signer: $pfxSubject"
    Write-Output "        SHA-1 $expectedThumbprint"
    Write-Output "        SHA-256 $expectedSha256"
    $password = $null
} else {
    Step 'NOT SIGNED'
    $message = "No -PfxPath: $zipName is UNSIGNED. SmartScreen shows 'unknown publisher', and installed signed copies refuse it as an in-app update."
    if ($InGitHubActions) { Write-Output "::warning::$message" }
    Write-Warning $message
}

# The staged exe once more: signing must not have changed its identity.
Test-Identity (Join-Path $stage 'Hearsay.exe') | Out-Null

# ---- 5. Zip and checksum ----
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$stageBytes = ($files | Measure-Object -Property Length -Sum).Sum
Step "Zipping $zipPath"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Entry by entry: .NET Framework's ZipFile.CreateFromDirectory writes
# backslashes into the entry names, which the zip format does not allow.
$writer = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in ($files | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $writer, $file.FullName, "Hearsay/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $writer.Dispose()
}

# The layout the in-app update expects (UpdateInstall: one Hearsay.exe, at the
# root or in one top folder); forward slashes only.
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($archive.Entries)
    $bad = @($entries | Where-Object { $_.FullName.Contains('\') -or -not $_.FullName.StartsWith('Hearsay/') })
    if ($bad.Count -gt 0) { Fail "unexpected zip entry $($bad[0].FullName)." }
    $exeEntries = @($entries | Where-Object { $_.Name -eq 'Hearsay.exe' })
    if ($exeEntries.Count -ne 1 -or $exeEntries[0].FullName -ne 'Hearsay/Hearsay.exe') {
        Fail "the zip should contain exactly Hearsay/Hearsay.exe, found $($exeEntries.Count)."
    }
    $entryCount = $entries.Count
} finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$line = "$hash  $zipName"
[IO.File]::WriteAllText($sumsPath, "$line`n", (New-Object Text.UTF8Encoding($false)))
Remove-Item -LiteralPath $stageRoot -Recurse -Force
Remove-Item -LiteralPath $buildDir -Recurse -Force

$zipBytes = (Get-Item -LiteralPath $zipPath).Length
Step 'Done'
Write-Output ("{0}  {1} ({2} entries; {3} files, {4} unpacked)" -f $zipPath, (Format-Size $zipBytes), $entryCount, $files.Count, (Format-Size $stageBytes))
Write-Output "$sumsPath"
Write-Output "  $line"
if ($signing) { Write-Output "Signed: Hearsay.exe and $($toSign.Count - 1) Hearsay*.dll files." } else { Write-Output 'UNSIGNED build.' }
exit 0
