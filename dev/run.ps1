# Developer run script: update the code, then build and run AmbiWin with a log file.
#
#   dev\run.cmd                   run the current branch
#   dev\run.cmd -Branch day2-app  switch to a branch first
#
# AmbiWin runs in the notification area: quit it from its tray menu ("Quit AmbiWin").
# Logs go to .logs\ in the repo (ignored by git).
param(
    [string]$Branch = "",
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

Write-Host "Log: $log" -ForegroundColor Cyan
Write-Host "AmbiWin runs in the notification area. Quit it from its tray menu." -ForegroundColor Cyan

# A PowerShell window opened before the .NET SDK was installed has a stale PATH: fall back to the default location.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "The .NET SDK was not found. Install it with: winget install Microsoft.DotNet.SDK.10" }

& $dotnet run --project src/AmbiWin.App -c Release -- @appArgs
