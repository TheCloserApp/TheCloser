# Builds TheCloser.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
#   .\build.ps1          build dist\TheCloser.exe
#   .\build.ps1 -Run     build and launch
param([switch]$Run)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x C# compiler not found at $csc" }

$dist = Join-Path $root 'dist'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $dist, $obj | Out-Null
$exe = Join-Path $dist 'TheCloser.exe'
$ico = Join-Path $obj 'TheCloser.ico'

$refs = @(
    'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Net.Http.dll', 'System.Web.Extensions.dll', 'System.Security.dll',
    'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
    (Join-Path $fw 'System.Xaml.dll'),
    (Join-Path $fw 'WPF\PresentationFramework.dll'), (Join-Path $fw 'WPF\PresentationCore.dll'),
    (Join-Path $fw 'WPF\UIAutomationClient.dll'), (Join-Path $fw 'WPF\UIAutomationTypes.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll'), (Join-Path $fw 'WPF\System.Speech.dll')
) | ForEach-Object { "/r:$_" }

# The styles live in Theme.xaml; embed it so it can be merged at runtime (no XAML/BAML compilation).
$theme = "/resource:$(Join-Path $root 'src\Ui\Theme.xaml'),TheCloser.Theme.xaml"

function Invoke-Csc([string]$out, [string[]]$extra) {
    $sources = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
    $cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', '/nowarn:1701,1702,4014',
                 "/out:$out", "/win32manifest:$(Join-Path $root 'src\app.manifest')", $theme) + $extra + $refs + $sources
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)" }
}

# Stage 1 (first build only): compile once to render the app icon.
if (-not (Test-Path $ico)) {
    $tmp = Join-Path $obj 'TheCloser.iconstage.exe'
    Invoke-Csc $tmp @()
    & $tmp --write-icon $ico | Out-Null
    Start-Sleep -Milliseconds 300
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

$running = Get-Process TheCloser -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { throw "TheCloser is running from $exe - quit it (tray icon > Quit) and build again." }

Invoke-Csc $exe @("/win32icon:$ico")
$size = [math]::Round((Get-Item $exe).Length / 1KB)
Write-Host "Built $exe ($size KB)"

if ($Run) { Start-Process $exe }
