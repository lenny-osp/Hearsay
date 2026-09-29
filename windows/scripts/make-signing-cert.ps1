<#
.SYNOPSIS
  Creates Hearsay's self-signed Windows code-signing certificate once and
  exports it for the WINDOWS_CERTIFICATE_PFX and WINDOWS_CERTIFICATE_PASSWORD
  repository secrets (PLAN.md 18.4, "Updates and packaging").

.DESCRIPTION
  The Windows counterpart of the Mac's "Hearsay Code Signing (self-signed)"
  certificate (PLAN.md 4.7). Every release is signed with the same
  certificate, so the in-app update can check that a new Hearsay.exe has
  the running build's signer (UpdateInstall.CheckSignature, SHA-256
  thumbprint). Windows does not trust it: SmartScreen still asks on the first
  launch of a downloaded zip.

  Run it once, on the owner's machine:
    1. creates the certificate in Cert:\CurrentUser\My (RSA 3072, SHA-256,
       exportable key, valid 10 years; -Type CodeSigningCert);
    2. exports it with its private key to -PfxPath, protected by the
       password (asked for when -Password is not given);
    3. prints the base64 of the PFX (the WINDOWS_CERTIFICATE_PFX secret) and
       the SHA-1 and SHA-256 thumbprints.
  The password is the WINDOWS_CERTIFICATE_PASSWORD secret. Keep the PFX and
  the password somewhere safe outside the repository (a password manager):
  a new certificate means the next update cannot verify its signer.

  New-SelfSignedCertificate also puts a copy of the certificate (no key) in
  Cert:\CurrentUser\CA, the intermediate store; that does not make Windows
  trust it. Nothing is added to a root or trusted-publisher store.

  It refuses to run when a certificate with the subject already exists in
  Cert:\CurrentUser\My, unless -Force (which creates another one next to it;
  the old one is not removed). It refuses a -PfxPath inside the repository.

  Windows PowerShell 5.1; no modules beyond the PKI module Windows ships.

.PARAMETER PfxPath
  Where to write the PFX, outside the repository, e.g.
  %USERPROFILE%\Documents\hearsay-code-signing.pfx. Must not exist yet.

.PARAMETER Password
  The PFX password, as a SecureString or plain string. Asked for (twice)
  when omitted.

.PARAMETER Subject
  The certificate subject. Leave the default for the real certificate; a
  different subject is only for testing the script.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File windows\scripts\make-signing-cert.ps1 -PfxPath "$env:USERPROFILE\Documents\hearsay-code-signing.pfx"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [object]$Password,
    [string]$Subject = 'CN=Hearsay Code Signing (self-signed)',
    [int]$Years = 10,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    [Console]::Error.WriteLine("make-signing-cert: $Message")
    exit 1
}

function ConvertTo-SecurePassword([object]$Value) {
    if ($Value -is [System.Security.SecureString]) { return $Value }
    if ($Value -is [string]) {
        if ($Value.Length -eq 0) { Fail 'the password is empty.' }
        return (ConvertTo-SecureString -String $Value -AsPlainText -Force)
    }
    Fail 'the password must be a string or a SecureString.'
}

function ConvertFrom-SecurePassword([System.Security.SecureString]$Value) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\') + '\'
$pfxFull = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PfxPath))
if ($pfxFull.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Fail "the PFX must not be written inside the repository ($repoRoot). Name a path outside it."
}
if (Test-Path -LiteralPath $pfxFull) { Fail "$pfxFull already exists; name a new file." }
$pfxFolder = Split-Path -Parent $pfxFull
if (-not (Test-Path -LiteralPath $pfxFolder -PathType Container)) { Fail "the folder $pfxFolder does not exist." }
if ($Years -lt 1 -or $Years -gt 30) { Fail 'Years must be between 1 and 30.' }

$existing = @(Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject })
if ($existing.Count -gt 0) {
    foreach ($c in $existing) {
        Write-Output ("Existing: {0}  SHA-1 {1}  valid to {2:yyyy-MM-dd}" -f $c.Subject, $c.Thumbprint, $c.NotAfter)
    }
    if (-not $Force) {
        Fail "a certificate with the subject '$Subject' already exists in Cert:\CurrentUser\My. Export that one (certmgr.msc or Export-PfxCertificate) or pass -Force to create another."
    }
    Write-Warning 'Creating another certificate with the same subject (-Force). Releases signed with it have a different thumbprint, so installed copies signed with the old one cannot update to them in-app.'
}

if ($null -eq $Password) {
    $first = Read-Host -AsSecureString -Prompt 'PFX password'
    $second = Read-Host -AsSecureString -Prompt 'PFX password again'
    if ((ConvertFrom-SecurePassword $first) -ne (ConvertFrom-SecurePassword $second)) { Fail 'the passwords differ.' }
    $securePassword = $first
} else {
    $securePassword = ConvertTo-SecurePassword $Password
}
if ((ConvertFrom-SecurePassword $securePassword).Length -eq 0) { Fail 'the password is empty.' }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
    -FriendlyName ($Subject -replace '^CN=', '') `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable -KeyUsage DigitalSignature `
    -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddYears($Years)

$exportArgs = @{ Cert = $cert; FilePath = $pfxFull; Password = $securePassword }
if ((Get-Command Export-PfxCertificate).Parameters.ContainsKey('CryptoAlgorithmOption')) {
    $exportArgs['CryptoAlgorithmOption'] = 'AES256_SHA256'
}
Export-PfxCertificate @exportArgs | Out-Null

$bytes = [IO.File]::ReadAllBytes($pfxFull)
$sha = [Security.Cryptography.SHA256]::Create()
try { $sha256 = ([BitConverter]::ToString($sha.ComputeHash($cert.RawData))).Replace('-', '') }
finally { $sha.Dispose() }

Write-Output ''
Write-Output "Created:    $($cert.Subject)"
Write-Output "Store:      Cert:\CurrentUser\My\$($cert.Thumbprint)"
Write-Output ("Valid:      {0:yyyy-MM-dd} to {1:yyyy-MM-dd}" -f $cert.NotBefore, $cert.NotAfter)
Write-Output "Key:        RSA $($cert.PublicKey.Key.KeySize), signature $($cert.SignatureAlgorithm.FriendlyName)"
Write-Output "SHA-1:      $($cert.Thumbprint)"
Write-Output "SHA-256:    $sha256   (the thumbprint the in-app update compares)"
Write-Output "PFX:        $pfxFull ($($bytes.Length) bytes)"
Write-Output ''
Write-Output 'Repository secrets (GitHub > Settings > Secrets and variables > Actions):'
Write-Output '  WINDOWS_CERTIFICATE_PASSWORD  the password you just chose'
Write-Output '  WINDOWS_CERTIFICATE_PFX       the base64 below, as one line:'
Write-Output ''
Write-Output ([Convert]::ToBase64String($bytes))
Write-Output ''
Write-Output 'Keep the PFX and its password outside the repository. Sign a local build with:'
Write-Output "  windows\scripts\make-release.ps1 -Version <v> -PfxPath `"$pfxFull`""
