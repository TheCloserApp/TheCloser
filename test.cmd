@echo off
rem Runs test.ps1 without needing to change PowerShell's script execution policy.
rem   test          self-tests          test -Demo    open the demo window
rem   test -Audio   + audio capture     test -Api     + a real Claude answer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test.ps1" %*
