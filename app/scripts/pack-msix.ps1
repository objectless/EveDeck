<#
.SYNOPSIS
  Builds the Microsoft Store (MSIX) package for EveDeck.

.DESCRIPTION
  Takes an already-published self-contained build and wraps it in an MSIX using makeappx.exe from
  the Windows SDK.

  This does NOT sign the package, and that is deliberate: Microsoft re-signs Store submissions with
  its own certificate at ingestion. Signing here would require a code-signing certificate whose
  subject names the holder, which is exactly what publishing through the Store avoids.

  The output .msix is therefore NOT installable locally as-is. To test it on this machine you must
  sign it with a self-signed certificate whose subject EXACTLY matches Identity/@Publisher in the
  manifest, and trust that certificate -- see -SelfSignForLocalTest.

.PARAMETER Version
  Four-part version for the package (e.g. 1.24.0.0). MSIX requires four parts and requires the
  final part to be 0 for Store submissions.

.PARAMETER PublishDir
  Directory holding the already-published self-contained app (must contain EveDeck.exe).

.PARAMETER OutFile
  Path to write the .msix to.

.PARAMETER SelfSignForLocalTest
  Also create a self-signed certificate and sign the package with it, so it can be installed on this
  machine for testing. NEVER use this for a Store submission.
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$PublishDir = "$PSScriptRoot\..\publish",
    [string]$OutFile = "$PSScriptRoot\..\publish-msix\EveDeck.msix",
    [switch]$SelfSignForLocalTest
)

$ErrorActionPreference = "Stop"

$repoApp = Resolve-Path "$PSScriptRoot\.."
$manifestSource = Join-Path $repoApp "packaging\AppxManifest.xml"
$assetsSource = Join-Path $repoApp "packaging\Assets"

if (-not (Test-Path $manifestSource)) { throw "Manifest not found: $manifestSource" }
if (-not (Test-Path (Join-Path $PublishDir "EveDeck.exe"))) {
    throw "No EveDeck.exe in $PublishDir. Run scripts\publish.ps1 first."
}

# Version: default to the csproj's, normalised to the four parts MSIX requires.
if (-not $Version) {
    $csproj = Get-Content (Join-Path $repoApp "src\EveDeck\EveDeck.csproj") -Raw
    if ($csproj -match '<Version>([\d\.]+)</Version>') { $Version = $Matches[1] } else { throw "Could not read <Version> from csproj" }
}
$parts = @($Version.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
# Store requires the revision (4th) part to be 0.
$parts[3] = '0'
$packageVersion = ($parts[0..3] -join '.')
Write-Host "== packaging EveDeck $packageVersion =="

# Locate makeappx.exe from the newest installed Windows SDK.
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeappx = Get-ChildItem -Path $sdkRoot -Filter "makeappx.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $makeappx) { throw "makeappx.exe not found under $sdkRoot -- install the Windows SDK." }

# Stage: the package layout is the published app plus the manifest and tile assets at its root.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("evedeck-msix-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Copy-Item "$PublishDir\*" $stage -Recurse -Force
    # Copy the CONTENTS into Assets, not the folder itself: the published app already ships an
    # Assets directory (fonts, bg.png), and Copy-Item on an existing destination nests the source
    # inside it as Assets\Assets, which makeappx then reports as missing tile images.
    $stageAssets = Join-Path $stage "Assets"
    if (-not (Test-Path $stageAssets)) { New-Item -ItemType Directory -Path $stageAssets -Force | Out-Null }
    Copy-Item "$assetsSource\*" $stageAssets -Force

    (Get-Content $manifestSource -Raw).Replace("__VERSION__", $packageVersion) |
        Set-Content (Join-Path $stage "AppxManifest.xml") -Encoding utf8

    $outDir = Split-Path $OutFile -Parent
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

    & $makeappx.FullName pack /d $stage /p $OutFile /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }

    if ($SelfSignForLocalTest) {
        Write-Warning "Self-signing for LOCAL TEST ONLY. Do not submit this file to the Store."
        $manifestXml = [xml](Get-Content (Join-Path $stage "AppxManifest.xml"))
        $publisher = $manifestXml.Package.Identity.Publisher
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher `
            -KeyUsage DigitalSignature -FriendlyName "EveDeck MSIX local test" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        $signtool = Get-ChildItem -Path $sdkRoot -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw "signtool.exe not found -- cannot self-sign." }
        & $signtool.FullName sign /fd SHA256 /a /s My /n $cert.Subject $OutFile
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
        Write-Host "Signed with local test cert $($cert.Thumbprint). Trust it before installing:"
        Write-Host "  Export it from Cert:\CurrentUser\My and import into Local Machine > Trusted People."
    }

    Write-Host "MSIX  $OutFile"
    Write-Host ((Get-Item $OutFile).Length / 1MB).ToString("0.0") "MB"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
