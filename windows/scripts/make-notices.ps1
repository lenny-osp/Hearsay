<#
.SYNOPSIS
  Generates windows/THIRD_PARTY_NOTICES.md from the license files of the
  NuGet packages the Windows app ships.

.DESCRIPTION
  The Windows counterpart of mac/Scripts/make-notices.sh (same output shape).

  Package list and exact versions come from the obj/project.assets.json
  files NuGet writes on restore (they work offline once restored), for every
  project in windows/Hearsay.slnx that is not a test project
  (<IsTestProject>true</IsTestProject>). Every package in those graphs is
  listed, transitive ones included, plus the framework reference packs NuGet
  downloads for them (downloadDependencies, e.g. Microsoft.Windows.SDK.NET.Ref,
  which ships WinRT.Runtime.dll).

  Build-only rule: a package is left out of section 1 only when it is in
  $BuildOnly below, i.e. it supplies build tools (compilers, resource and
  MSIX packaging tasks) and none of its files is copied to the app output.
  They are listed in section 3 instead. When a Release build of the app
  exists, the script checks that no .dll/.exe/.winmd/.pri from a build-only
  package is in its output folder.

  License text, per package folder %USERPROFILE%\.nuget\packages\<id>\<version>\:
    1. the nuspec's <license type="file">, which must exist;
    2. else a LICENSE, LICENSE.txt or LICENSE.md file in the package root;
    3. else the nuspec's <license type="expression"> if the expression is in
       $SpdxTexts (the SPDX text with the nuspec copyright line);
    4. else a <licenseUrl> only for packages listed in $UrlOnly.
  NOTICE, THIRD-PARTY-NOTICES and ThirdPartyNotices files in the package
  root are added verbatim. Anything else stops the script with a non-zero
  exit code naming the package; nothing is skipped silently.

  whisper.cpp itself (bundled as native DLLs in Whisper.net.Runtime*) gets
  its own section. Whisper.net does not ship whisper.cpp's license, so its
  LICENSE is fetched once over HTTPS from the whisper.cpp repository at the
  commit the resolved Whisper.net.Runtime version bundles ($WhisperCppCommits)
  and cached in %TEMP%\hearsay-notices.

  The output is deterministic: running the script twice gives the same file.
  Run it after every dependency change (after dotnet restore or build):
    powershell -ExecutionPolicy Bypass -File windows\scripts\make-notices.ps1
  CI form, which regenerates to a temp file and fails if the committed file
  differs:
    powershell -ExecutionPolicy Bypass -File windows\scripts\make-notices.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Any failure: one line on stderr, exit code 1.
trap {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

# Packages that only carry build tools; nothing from them ships.
$BuildOnly = @{
    'microsoft.windows.sdk.buildtools'      = 'Windows SDK build tools (MIDL, MakePri, WinMD tools), used at build time only'
    'microsoft.windows.sdk.buildtools.msix' = 'MSIX packaging build tasks, used at build time only'
}

# Packages that ship no license file and declare only a licenseUrl.
# id -> license name, note
$UrlOnly = @{
    'microsoft.windows.sdk.net.ref' = @{
        Name = 'Microsoft Windows SDK license terms'
        Note = 'The .NET projection of the Windows SDK: ships Microsoft.Windows.SDK.NET.dll and the C#/WinRT runtime WinRT.Runtime.dll (C#/WinRT is MIT, https://github.com/microsoft/CsWinRT). The package ships no license file; its nuspec gives only a license URL.'
    }
}

# whisper.cpp commit bundled by each Whisper.net.Runtime version, from the
# Whisper.net repository's whisper.cpp submodule (recorded in
# windows/Spike/WhisperSpike/REPORT.md). Add a row when Whisper.net changes.
$WhisperCppCommits = @{
    '1.9.1' = @{ Commit = 'f24588a272ae8e23280d9c220536437164e6ed28'; Version = '1.8.5' }
}

# SPDX license texts for packages that declare an expression and ship no
# file. {COPYRIGHT} is replaced by the nuspec copyright line.
$SpdxTexts = @{
    'MIT' = @'
MIT License

{COPYRIGHT}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
}

$LicenseFilePattern = '^(?i)license(\.(txt|md))?$'
$NoticeFilePattern = '^(?i)(notice|third-party-notices|thirdpartynotices)(\.(txt|md))?$'

function Fail([string] $message) {
    throw "make-notices: $message"
}

$Utf8 = New-Object System.Text.UTF8Encoding $false

# Text as it goes into a fence: BOM dropped, LF newlines, trailing
# whitespace removed from each line, leading and trailing blank lines removed.
function Get-NormalizedText([string] $text) {
    $text = $text.TrimStart([char]0xFEFF)
    $text = $text -replace "`r`n", "`n" -replace "`r", "`n"
    $lines = $text.Split("`n") | ForEach-Object { $_.TrimEnd() }
    $joined = ($lines -join "`n").Trim("`n")
    if ($joined.Length -eq 0) { Fail 'empty license or notice text' }
    return $joined
}

function Read-Text([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "missing $path" }
    return Get-NormalizedText ([System.IO.File]::ReadAllText($path, $Utf8))
}

function Get-Fence([string] $text) {
    $marker = '```'
    while ($text.Contains($marker)) { $marker += '`' }
    return "$($marker)text`n$text`n$marker"
}

function Get-MetadataNode($doc, [string] $name) {
    return $doc.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$name']")
}

function Get-MetadataText($doc, [string] $name) {
    $node = Get-MetadataNode $doc $name
    if ($null -eq $node) { return '' }
    return $node.InnerText.Trim()
}

# Which license a file holds; stops when it cannot tell.
function Get-LicenseName([string] $text, [string] $path) {
    if ($text -match 'Permission is hereby granted, free of charge, to any person obtaining a copy') { return 'MIT' }
    if ($text -match 'Redistribution and use in source and binary forms' -and
        ($text -match 'Neither the name' -or $text -match 'may not be used to endorse or promote')) { return 'BSD-3-Clause' }
    if ($text -match '^MICROSOFT SOFTWARE LICENSE TERMS') { return 'Microsoft Software License Terms' }
    if ($text -match 'Apache License\s+Version 2\.0') { return 'Apache-2.0' }
    Fail "cannot tell which license $path is; add a rule to Get-LicenseName"
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windows = Join-Path $repo 'windows'
$solution = Join-Path $windows 'Hearsay.slnx'
$outFile = Join-Path $windows 'THIRD_PARTY_NOTICES.md'
if (-not (Test-Path -LiteralPath $solution)) { Fail "missing $solution" }

# --- Shipped projects -------------------------------------------------------
[xml] $slnx = [System.IO.File]::ReadAllText($solution, $Utf8)
$projects = New-Object System.Collections.Generic.List[string]
foreach ($node in $slnx.SelectNodes('//Project')) {
    $projects.Add($node.GetAttribute('Path'))
}
$projects.Sort([StringComparer]::OrdinalIgnoreCase)
if ($projects.Count -eq 0) { Fail "no projects in $solution" }

$shipped = New-Object System.Collections.Generic.List[string]
foreach ($relative in $projects) {
    $csproj = Join-Path $windows ($relative -replace '/', '\')
    if (-not (Test-Path -LiteralPath $csproj)) { Fail "missing $csproj" }
    $content = [System.IO.File]::ReadAllText($csproj, $Utf8)
    if ($content -match '<IsTestProject>\s*true\s*</IsTestProject>') { continue }
    $shipped.Add($csproj)
}

# --- Resolved package graph -------------------------------------------------
# key: lowercase id -> @{ Id; Version; Projects }
$packages = @{}
$packagesRoot = $null
foreach ($csproj in $shipped) {
    $assetsPath = Join-Path (Split-Path $csproj -Parent) 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) {
        Fail "missing $assetsPath; run: dotnet restore windows\Hearsay.slnx"
    }
    $assets = [System.IO.File]::ReadAllText($assetsPath, $Utf8) | ConvertFrom-Json
    $root = [string] $assets.project.restore.packagesPath
    if ($null -eq $packagesRoot) { $packagesRoot = $root }
    elseif ($packagesRoot -ne $root) { Fail "projects restore to different package folders ($packagesRoot, $root)" }

    $found = @()
    foreach ($target in $assets.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            if ($library.Value.type -ne 'package') { continue }
            $parts = $library.Name.Split('/')
            $found += , @($parts[0], $parts[1])
        }
    }
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        $downloads = $framework.Value.PSObject.Properties['downloadDependencies']
        if ($null -eq $downloads) { continue }
        foreach ($dependency in $downloads.Value) {
            if ($dependency.version -notmatch '^\[([^,\]]+), *\1\]$') {
                Fail "download dependency $($dependency.name) has no exact version ($($dependency.version)) in $assetsPath"
            }
            $found += , @($dependency.name, $Matches[1])
        }
    }
    foreach ($pair in $found) {
        $key = $pair[0].ToLowerInvariant()
        if ($packages.ContainsKey($key)) {
            if ($packages[$key].Version -ne $pair[1]) {
                Fail "$($pair[0]) resolves to $($packages[$key].Version) and $($pair[1]) in different projects"
            }
        }
        else {
            $packages[$key] = @{ Id = $pair[0]; Version = $pair[1] }
        }
    }
}
if ($packages.Count -eq 0) { Fail 'no packages found' }

$keys = New-Object System.Collections.Generic.List[string]
foreach ($k in $packages.Keys) { $keys.Add($k) }
$keys.Sort([StringComparer]::Ordinal)

# --- Build-only check against a Release build, when there is one -----------
foreach ($csproj in $shipped) {
    $bin = Join-Path (Split-Path $csproj -Parent) 'bin\Release'
    if (-not (Test-Path -LiteralPath $bin)) { continue }
    $outputNames = @{}
    Get-ChildItem -LiteralPath $bin -Recurse -File | ForEach-Object { $outputNames[$_.Name.ToLowerInvariant()] = $true }
    foreach ($key in $keys) {
        if (-not $BuildOnly.ContainsKey($key)) { continue }
        $folder = Join-Path $packagesRoot "$key\$($packages[$key].Version.ToLowerInvariant())"
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        Get-ChildItem -LiteralPath $folder -Recurse -File -Include *.dll, *.exe, *.winmd, *.pri | ForEach-Object {
            if ($outputNames.ContainsKey($_.Name.ToLowerInvariant())) {
                Fail "$($packages[$key].Id) is marked build-only but $($_.Name) is in $bin; remove it from `$BuildOnly"
            }
        }
    }
}

# --- License text per package -----------------------------------------------
$entries = New-Object System.Collections.Generic.List[object]
$buildOnlyEntries = New-Object System.Collections.Generic.List[object]
foreach ($key in $keys) {
    $id = $packages[$key].Id
    $version = $packages[$key].Version
    $folder = Join-Path $packagesRoot "$key\$($version.ToLowerInvariant())"
    if (-not (Test-Path -LiteralPath $folder)) { Fail "$id $($version): package folder $folder is missing; run dotnet restore" }
    $nuspecPath = Join-Path $folder "$key.nuspec"
    if (-not (Test-Path -LiteralPath $nuspecPath)) { Fail "$id $($version): missing $nuspecPath" }
    $nuspec = New-Object System.Xml.XmlDocument
    $nuspec.LoadXml([System.IO.File]::ReadAllText($nuspecPath, $Utf8))
    $displayId = Get-MetadataText $nuspec 'id'
    if ($displayId -eq '') { $displayId = $id }

    if ($BuildOnly.ContainsKey($key)) {
        $buildOnlyEntries.Add(@{ Id = $displayId; Version = $version; Reason = $BuildOnly[$key] })
        continue
    }

    $projectUrl = Get-MetadataText $nuspec 'projectUrl'
    if ($projectUrl -eq '') {
        $repository = Get-MetadataNode $nuspec 'repository'
        if ($null -ne $repository) { $projectUrl = $repository.GetAttribute('url') }
    }
    if ($projectUrl -eq '') { Fail "$displayId $($version): nuspec has no projectUrl or repository url" }

    $licenseNode = Get-MetadataNode $nuspec 'license'
    $licenseType = ''
    $licenseValue = ''
    if ($null -ne $licenseNode) {
        $licenseType = $licenseNode.GetAttribute('type')
        $licenseValue = $licenseNode.InnerText.Trim()
    }
    $rootFiles = @(Get-ChildItem -LiteralPath $folder -File)
    $rootNames = [string[]] @($rootFiles | ForEach-Object { $_.Name.ToLowerInvariant() })
    [Array]::Sort($rootNames, $rootFiles, [StringComparer]::Ordinal)

    $licenseText = $null
    $licenseName = $null
    $source = $null
    if ($licenseType -eq 'file') {
        $path = Join-Path $folder ($licenseValue -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "$displayId $($version): nuspec names license file $licenseValue, which is not in $folder" }
        $licenseText = Read-Text $path
        $licenseName = Get-LicenseName $licenseText $path
        $source = "License text from ``$licenseValue`` in the package."
    }
    else {
        $candidates = @($rootFiles | Where-Object { $_.Name -match $LicenseFilePattern })
        if ($candidates.Count -gt 1) { Fail "$displayId $($version): several license files in $folder" }
        if ($candidates.Count -eq 1) {
            $licenseText = Read-Text $candidates[0].FullName
            $licenseName = Get-LicenseName $licenseText $candidates[0].FullName
            if ($licenseType -eq 'expression') { $licenseName = $licenseValue }
            $source = "License text from ``$($candidates[0].Name)`` in the package."
        }
        elseif ($licenseType -eq 'expression') {
            if (-not $SpdxTexts.ContainsKey($licenseValue)) {
                Fail "$displayId $($version): ships no license file and SPDX expression '$licenseValue' has no known text; add it to `$SpdxTexts"
            }
            $copyright = Get-MetadataText $nuspec 'copyright'
            $copyrightSource = 'copyright'
            if ($copyright -eq '') {
                $authors = Get-MetadataText $nuspec 'authors'
                if ($authors -eq '') { Fail "$displayId $($version): nuspec has neither copyright nor authors for the SPDX text" }
                $copyright = "Copyright (c) $authors"
                $copyrightSource = 'authors'
            }
            $licenseText = Get-NormalizedText ($SpdxTexts[$licenseValue].Replace('{COPYRIGHT}', $copyright))
            $licenseName = $licenseValue
            $source = "The package ships no license file; its nuspec declares ``$licenseValue``. License text: the SPDX $licenseValue text with the nuspec $copyrightSource line."
        }
        elseif ($UrlOnly.ContainsKey($key)) {
            $url = Get-MetadataText $nuspec 'licenseUrl'
            if ($url -eq '') { Fail "$displayId $($version): listed in `$UrlOnly but its nuspec has no licenseUrl" }
            $licenseName = $UrlOnly[$key].Name
            $source = "$($UrlOnly[$key].Note) License terms: $url"
        }
        else {
            Fail "$displayId $($version): no license file, no SPDX expression and not in `$UrlOnly ($folder)"
        }
    }

    $notices = New-Object System.Collections.Generic.List[object]
    foreach ($file in $rootFiles) {
        if ($file.Name -match $NoticeFilePattern) {
            $notices.Add(@{ Name = $file.Name; Text = (Read-Text $file.FullName) })
        }
    }

    $entries.Add(@{
            Id = $displayId; Version = $version; Url = $projectUrl
            License = $licenseName; LicenseText = $licenseText; Source = $source
            Notices = $notices
        })
}

# --- whisper.cpp --------------------------------------------------------------
if (-not $packages.ContainsKey('whisper.net.runtime')) {
    Fail 'Whisper.net.Runtime is not in the package graph; update the whisper.cpp section of this script'
}
$runtimeVersion = $packages['whisper.net.runtime'].Version
if (-not $WhisperCppCommits.ContainsKey($runtimeVersion)) {
    Fail "no whisper.cpp commit recorded for Whisper.net.Runtime $runtimeVersion; add it to `$WhisperCppCommits"
}
$whisperCpp = $WhisperCppCommits[$runtimeVersion]
$whisperCppUrl = "https://raw.githubusercontent.com/ggml-org/whisper.cpp/$($whisperCpp.Commit)/LICENSE"
$cacheDir = Join-Path ([System.IO.Path]::GetTempPath()) 'hearsay-notices'
$cacheFile = Join-Path $cacheDir "whisper.cpp-$($whisperCpp.Commit)-LICENSE"
if (-not (Test-Path -LiteralPath $cacheFile)) {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $partial = "$cacheFile.part"
    try {
        Invoke-WebRequest -Uri $whisperCppUrl -OutFile $partial -UseBasicParsing -TimeoutSec 30
    }
    catch {
        Fail "could not fetch the whisper.cpp license from $($whisperCppUrl): $($_.Exception.Message)"
    }
    Move-Item -LiteralPath $partial -Destination $cacheFile -Force
}
$whisperCppLicense = Read-Text $cacheFile
if ((Get-LicenseName $whisperCppLicense $whisperCppUrl) -ne 'MIT') { Fail "$whisperCppUrl is not the MIT license" }
# The runtime packages that carry whisper.cpp DLLs for Windows x64 (the Metal
# package, which only carries a macOS shader source file, is pruned from the
# graph by the projects and would be skipped here).
$whisperCppPackages = @($entries | Where-Object {
        $_.Id -like 'Whisper.net.Runtime*' -and
        (Test-Path -Path (Join-Path $packagesRoot "$($_.Id.ToLowerInvariant())\$($_.Version.ToLowerInvariant())\build\win-x64\whisper.dll"))
    } | ForEach-Object { "$($_.Id) $($_.Version)" })
if ($whisperCppPackages.Count -eq 0) { Fail 'no Whisper.net.Runtime package carries build\win-x64\whisper.dll' }

# --- Output -------------------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
function Add-Line([string] $line) { [void] $sb.Append($line).Append("`n") }

Add-Line '# Third-Party Notices'
Add-Line ''
Add-Line 'Hearsay is MIT licensed (see `LICENSE`, Copyright (c) 2026 Chihling Wang).'
Add-Line 'The Windows version is built with the open-source and redistributable'
Add-Line 'software listed below. This file is generated by'
Add-Line '`windows/scripts/make-notices.ps1` from the license files of the exact'
Add-Line 'package versions the app is built with; do not edit it by hand.'
Add-Line ''
Add-Line '1. Bundled libraries: NuGet packages whose files ship in the app folder.'
Add-Line '2. Credited, not shipped: models and programs Hearsay uses but does not include.'
Add-Line '3. Build-only packages: used to build the app, nothing from them ships.'
Add-Line '4. Not used.'
Add-Line ''
Add-Line '## 1. Bundled libraries'
Add-Line ''
Add-Line '| Component | Version | License |'
Add-Line '| --- | --- | --- |'
foreach ($e in $entries) {
    Add-Line "| [$($e.Id)]($($e.Url)) | $($e.Version) | $($e.License) |"
}
Add-Line "| [whisper.cpp (in Whisper.net.Runtime)](https://github.com/ggml-org/whisper.cpp) | $($whisperCpp.Version) | MIT |"
Add-Line ''
Add-Line 'A license or notice text that several packages share is printed once, at'
Add-Line 'the first package that has it.'
Add-Line ''
Add-Line '### 1.1 NuGet packages'
Add-Line ''
$printed = @{}
foreach ($e in $entries) {
    Add-Line "#### $($e.Id) $($e.Version)"
    Add-Line ''
    Add-Line "$($e.Url), $($e.License). $($e.Source)"
    Add-Line ''
    if ($null -ne $e.LicenseText) {
        if ($printed.ContainsKey($e.LicenseText)) {
            Add-Line "Same license text as $($printed[$e.LicenseText]) above."
        }
        else {
            Add-Line (Get-Fence $e.LicenseText)
            $printed[$e.LicenseText] = "$($e.Id) $($e.Version)"
        }
        Add-Line ''
    }
    foreach ($n in $e.Notices) {
        if ($printed.ContainsKey($n.Text)) {
            Add-Line "``$($n.Name)``: same text as $($printed[$n.Text]) above."
        }
        else {
            Add-Line "``$($n.Name)``:"
            Add-Line ''
            Add-Line (Get-Fence $n.Text)
            $printed[$n.Text] = "$($e.Id) $($e.Version) ``$($n.Name)``"
        }
        Add-Line ''
    }
}
Add-Line '### 1.2 whisper.cpp'
Add-Line ''
Add-Line "#### whisper.cpp $($whisperCpp.Version)"
Add-Line ''
Add-Line 'https://github.com/ggml-org/whisper.cpp, MIT. Compiled into the native'
Add-Line "DLLs (whisper.dll, ggml-*.dll) of $($whisperCppPackages -join ' and '),"
Add-Line "at commit ``$($whisperCpp.Commit)``."
Add-Line 'The packages do not ship its license, so the text is taken from the'
Add-Line 'whisper.cpp repository at that commit. It covers the ggml library in the'
Add-Line 'same tree.'
Add-Line ''
Add-Line (Get-Fence $whisperCppLicense)
Add-Line ''
Add-Line '## 2. Credited, not shipped'
Add-Line ''
Add-Line 'These are downloaded or run at the user''s request and are not part of'
Add-Line 'the Hearsay app folder. No license text is required here; each is under'
Add-Line 'its own terms.'
Add-Line ''
Add-Line '- Whisper model weights, downloaded from Hugging Face at runtime:'
Add-Line '  `ggerganov/whisper.cpp` (https://huggingface.co/ggerganov/whisper.cpp),'
Add-Line '  whisper.cpp GGML conversions of the OpenAI Whisper models, which are MIT'
Add-Line '  (https://github.com/openai/whisper).'
$runtimePacks = @($entries | Where-Object { $_.Id -like 'Microsoft.NETCore.App.Runtime*' -or $_.Id -like 'Microsoft.WindowsDesktop.App.Runtime*' })
if ($runtimePacks.Count -eq 0) {
    Add-Line '- The .NET runtime (MIT, https://github.com/dotnet/runtime): the app is'
    Add-Line '  framework-dependent and runs on the .NET runtime installed on the machine.'
}
Add-Line '- AI command-line tools Hearsay can run for meeting notes, if the user has'
Add-Line '  installed them: GitHub Copilot CLI, Claude Code, OpenAI Codex CLI and'
Add-Line '  Antigravity CLI. They are separate products under their own terms, are'
Add-Line '  not shipped with Hearsay, and Hearsay does not include any of their code.'
Add-Line '  The same holds for local model servers such as Ollama or LM Studio that'
Add-Line '  Hearsay can send notes requests to.'
Add-Line '- The SRT cleanup follows the output rules of `mlx_whisper` 0.4.3'
Add-Line '  (https://github.com/ml-explore/mlx-examples, MIT) by way of the macOS app;'
Add-Line '  no code of it is included.'
Add-Line ''
Add-Line '## 3. Build-only packages'
Add-Line ''
Add-Line 'Referenced by the projects but only used while building; none of their'
Add-Line 'files is copied to the app folder.'
Add-Line ''
foreach ($b in $buildOnlyEntries) {
    Add-Line "- $($b.Id) $($b.Version): $($b.Reason)."
}
if ($buildOnlyEntries.Count -eq 0) { Add-Line '- None.' }
Add-Line ''
Add-Line '## 4. Not used'
Add-Line ''
Add-Line '- FFmpeg: unlike whisper-tools, Hearsay does not use or ship FFmpeg.'

$text = $sb.ToString()

try {
    if ($Check) {
        $temp = [System.IO.Path]::GetTempFileName()
        try {
            [System.IO.File]::WriteAllText($temp, $text, $Utf8)
            if (-not (Test-Path -LiteralPath $outFile)) {
                [Console]::Error.WriteLine("make-notices: $outFile is missing; run windows\scripts\make-notices.ps1")
                exit 1
            }
            $expected = [System.IO.File]::ReadAllBytes($temp)
            $actual = [System.IO.File]::ReadAllBytes($outFile)
            $same = $expected.Length -eq $actual.Length
            if ($same) {
                for ($i = 0; $i -lt $expected.Length; $i++) {
                    if ($expected[$i] -ne $actual[$i]) { $same = $false; break }
                }
            }
            if (-not $same) {
                [Console]::Error.WriteLine("make-notices: $outFile is out of date; run windows\scripts\make-notices.ps1")
                exit 1
            }
            Write-Output "$outFile is up to date"
        }
        finally {
            Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        [System.IO.File]::WriteAllText($outFile, $text, $Utf8)
        Write-Output "Wrote $outFile ($($entries.Count) packages)"
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
