# W1 text spike (PLAN.md 18.8): builds TextSpike and runs every part.
# Usage: powershell -ExecutionPolicy Bypass -File windows\Spike\TextSpike\run.ps1 [-Part probe|zh|lang|cost|all]
param([string]$Part = 'all')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$project = Join-Path $PSScriptRoot 'TextSpike.csproj'
dotnet build $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\win-x64\TextSpike.exe'

function Invoke-Spike([string[]]$spikeArgs) {
    Write-Output ''
    Write-Output ('### TextSpike ' + ($spikeArgs -join ' '))
    & $exe @spikeArgs
    if ($LASTEXITCODE -ne 0) { throw "TextSpike $($spikeArgs -join ' ') failed with exit code $LASTEXITCODE" }
}

if ($Part -in 'probe', 'all') { Invoke-Spike @('probe') }
if ($Part -in 'zh', 'all') { Invoke-Spike @('zh'); Invoke-Spike @('zh', 'maxmatch') }
if ($Part -in 'lang', 'all') { Invoke-Spike @('lang') }
if ($Part -in 'cost', 'all') {
    # Each in its own process so the numbers are cold-start costs.
    Invoke-Spike @('cost-icu')
    Invoke-Spike @('cost-opencc')
    Invoke-Spike @('cost-opencc', 'maxmatch')
}
