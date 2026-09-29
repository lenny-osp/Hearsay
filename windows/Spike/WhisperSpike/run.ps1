# W1 spike, Whisper half: reproduces every measurement in REPORT.md.
#
#   .\run.ps1                                   # both models, CPU and Vulkan
#   .\run.ps1 -ModelDir ..\models -Runtime cpu  # one runtime
#   .\run.ps1 -Models ggml-small.bin -Runtime vulkan -Sweep ''
#   .\run.ps1 -Models ggml-large-v3-turbo-q5_0.bin -Runtime vulkan -Sweep '' -NativeDetect $false -PadSeconds 45
#   (run it with: powershell -NoProfile -ExecutionPolicy Bypass -File run.ps1 ...; the
#    flag applies to that one process only and changes no machine setting)
#
# Each model x runtime runs in its own process (Whisper.net picks the native
# library once per process). Runs are sequential so they do not compete for
# the CPU. Results land in -OutDir: <model>-<runtime>.json, one SRT per
# fixture, the native whisper.cpp log, and an .error.txt if loading failed.
param(
    [string]$ModelDir = (Join-Path $PSScriptRoot '..\models'),
    [ValidateSet('cpu', 'vulkan', 'both')]
    [string]$Runtime = 'both',
    [string[]]$Models = @('ggml-small.bin', 'ggml-large-v3-turbo-q5_0.bin'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'out'),
    [string]$Fixtures = (Join-Path $PSScriptRoot '..\..\..\shared\fixtures'),
    # Thread counts for the warm 30 s window sweep; '' = default threads only.
    [string]$Sweep = '2,4,8,12',
    # Per-process time limit.
    [int]$TimeoutMinutes = 40,
    # Also run --native-detect (all language probabilities + no-speech from one encoder run).
    [bool]$NativeDetect = $true,
    # If > 0, also run every model x runtime with this many seconds of silence appended
    # to each fixture (hallucination check; REPORT.md used 45 with turbo on Vulkan).
    [int]$PadSeconds = 0
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'WhisperSpike.csproj'
dotnet build $proj -c Release -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\win-x64\WhisperSpike.dll'

New-Item -ItemType Directory -Force $OutDir | Out-Null
dotnet $exe --api (Join-Path $OutDir 'whisper-net-api.txt')

$runtimes = if ($Runtime -eq 'both') { @('cpu', 'vulkan') } else { @($Runtime) }
foreach ($m in $Models) {
    $modelPath = Join-Path $ModelDir $m
    if (-not (Test-Path $modelPath)) { Write-Warning "missing $modelPath"; continue }
    foreach ($rt in $runtimes) {
        $argList = @($exe, '--model', $modelPath, '--runtime', $rt, '--fixtures', $Fixtures, '--out', $OutDir)
        if ($Sweep -ne '') { $argList += @('--sweep', $Sweep) }
        $p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -NoNewWindow -PassThru
        $null = $p.Handle  # Windows PowerShell 5.1 reports ExitCode only if the handle was opened
        if (-not $p.WaitForExit($TimeoutMinutes * 60 * 1000)) {
            $p.Kill()
            Write-Warning "$m / $rt timed out after $TimeoutMinutes min"
        }
        elseif ($p.ExitCode -ne 0) {
            Write-Warning "$m / $rt exited with $($p.ExitCode); see $OutDir"
        }
        if ($PadSeconds -gt 0) {
            $argList = @($exe, '--model', $modelPath, '--runtime', $rt, '--fixtures', $Fixtures, '--out', $OutDir, '--pad', $PadSeconds)
            $p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -NoNewWindow -PassThru
            $null = $p.Handle
            if (-not $p.WaitForExit($TimeoutMinutes * 60 * 1000)) { $p.Kill(); Write-Warning "$m / $rt pad run timed out" }
        }
        if ($NativeDetect) {
            # Language detection through whisper.cpp's C API, cross-checked with Whisper.net.
            $argList = @($exe, '--native-detect', '--model', $modelPath, '--runtime', $rt, '--fixtures', $Fixtures, '--out', $OutDir)
            $p = Start-Process -FilePath 'dotnet' -ArgumentList $argList -NoNewWindow -PassThru
            $null = $p.Handle
            if (-not $p.WaitForExit($TimeoutMinutes * 60 * 1000)) {
                $p.Kill()
                Write-Warning "$m / $rt native-detect timed out after $TimeoutMinutes min"
            }
            elseif ($p.ExitCode -ne 0) {
                Write-Warning "$m / $rt native-detect exited with $($p.ExitCode)"
            }
        }
    }
}
