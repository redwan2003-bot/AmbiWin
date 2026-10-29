# Developer run script: update the code, then build and run AmbiWin with a log file.
#
#   dev\run.cmd                      run the current branch
#   dev\run.cmd -Branch day1-overlay switch to a branch first
#   dev\run.cmd -ShowInCapture       glow visible in screenshots/recordings (for demos and remote checks)
#   dev\run.cmd -Idle                steady glow, no music sync
#
# Logs go to .logs\ in the repo (ignored by git). Stop with Ctrl+C.
param(
    [string]$Branch = "",
    [switch]$ShowInCapture,
    [switch]$Idle,
    [switch]$NoUpdate
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (-not $NoUpdate) {
    git fetch --quiet origin
    if ($Branch) { git checkout --quiet $Branch }
    git pull --quiet --ff-only
}
Write-Host "AmbiWin at $(git rev-parse --short HEAD) on $(git rev-parse --abbrev-ref HEAD)" -ForegroundColor Cyan

$logDir = Join-Path (Get-Location) ".logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("run-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")

$appArgs = @("--log", $log)
if ($ShowInCapture) { $appArgs += "--show-in-capture" }
if ($Idle) { $appArgs += "--idle" }

Write-Host "Log: $log" -ForegroundColor Cyan
dotnet run --project src/AmbiWin.App -c Release -- @appArgs
