@echo off
rem Runs dev\run.ps1 without changing the machine's PowerShell execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
