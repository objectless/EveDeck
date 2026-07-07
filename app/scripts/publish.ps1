<#
  One-shot build + publish for the EveDeck desktop app.

  Stops any running EveDeck (it locks publish\EveDeck.dll and causes MSB3027), builds Release,
  optionally runs the test suite, then publishes SELF-CONTAINED to app\publish and prints the
  resulting exe timestamp. Self-contained is the default the user runs day to day.

  Usage (from anywhere):
    pwsh -NoProfile -ExecutionPolicy Bypass -File F:\EveDeck\app\scripts\publish.ps1
    pwsh -NoProfile -ExecutionPolicy Bypass -File F:\EveDeck\app\scripts\publish.ps1 -RunTests
#>
[CmdletBinding()]
param([switch]$RunTests)

$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)   # ...\app
$sln  = Join-Path $appRoot 'EveDeck.sln'
$proj = Join-Path $appRoot 'src\EveDeck\EveDeck.csproj'
$out  = Join-Path $appRoot 'publish'

Write-Host '== build =='
dotnet build $sln -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) { Write-Error 'BUILD FAILED'; exit 1 }

if ($RunTests) {
  Write-Host '== test =='
  dotnet test $sln -c Release -v quiet --nologo
  if ($LASTEXITCODE -ne 0) { Write-Error 'TESTS FAILED'; exit 1 }
}

# Release the file lock: stop our own running exe before copying over publish\EveDeck.dll.
$running = Get-Process EveDeck -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500; Write-Host 'stopped running EveDeck' }

Write-Host '== publish =='
dotnet publish $proj -c Release --self-contained -r win-x64 -o $out -v quiet --nologo
if ($LASTEXITCODE -ne 0) { Write-Error 'PUBLISH FAILED'; exit 1 }

$exe = Join-Path $out 'EveDeck.exe'
if (Test-Path $exe) { Write-Host ('PUBLISHED  ' + (Get-Item $exe).LastWriteTime) }
else { Write-Error 'publish succeeded but EveDeck.exe missing'; exit 1 }
