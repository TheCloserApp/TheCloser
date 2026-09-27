@echo off
rem Runs build.ps1 without needing to change PowerShell's script execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
