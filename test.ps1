# Runs TheCloser's built-in self-tests and prints the results.
#   .\test.ps1            logic, parsing, rendering (silent, ~2 s)
#   .\test.ps1 -Audio     also plays a short quiet phrase to check speaker + microphone capture
#   .\test.ps1 -Api       also sends one real question to Claude using the key saved in Settings
#   .\test.ps1 -Demo      opens the window filled with sample content (no API calls)
#   .\test.ps1 -CI        skips the Windows 11 Live Captions installation check on CI
param([switch]$Audio, [switch]$Api, [switch]$Demo, [switch]$CI)

$ErrorActionPreference = 'Stop'
if ($CI -and ($Audio -or $Api -or $Demo)) {
    throw '-CI runs offline checks only; do not combine it with -Audio, -Api or -Demo.'
}

$exe = Join-Path $PSScriptRoot 'dist\TheCloser.exe'
if (-not (Test-Path $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') }

if ($Demo) {
    Start-Process $exe -ArgumentList '--demo'
    Write-Host 'Opened TheCloser with sample content. Quit it from the tray icon (or the X) when done.'
    return
}

$log = Join-Path $env:TEMP 'thecloser-selftest.txt'
Remove-Item $log -ErrorAction SilentlyContinue
$testArgs = @('--selftest', "`"$log`"")
if ($Audio) { $testArgs += '--audio' }
if ($Api) { $testArgs += '--api' }
if ($CI) { $testArgs += '--ci' }
$p = Start-Process -FilePath $exe -ArgumentList $testArgs -PassThru -Wait
if (-not (Test-Path $log)) { throw "Self-test exited with code $($p.ExitCode) without writing $log" }

foreach ($line in Get-Content $log) {
    if ($line -like 'FAIL*' -or $line -like '*FAILED') { Write-Host $line -ForegroundColor Red }
    elseif ($line -like 'PASS*' -or $line -like 'ALL CHECKS*') { Write-Host $line -ForegroundColor Green }
    elseif ($line -like '==*') { Write-Host $line -ForegroundColor Cyan }
    else { Write-Host $line }
}
exit $p.ExitCode
