<#
.SYNOPSIS
  Build FracturingFog Windows installer (.msi).

.DESCRIPTION
  1. Publishes the app as self-contained win-x64 into .\Stage.
  2. Compiles the WiX source into FracturingFog-<version>-x64.msi.

.PARAMETER Version
  Product version (must be x.y.z form). Default: 0.6.0.

.PARAMETER Configuration
  Build configuration. Default: Release.

.PARAMETER SkipPublish
  Skip the dotnet publish step (reuse existing .\Stage).

.EXAMPLE
  .\build-installer.ps1
  .\build-installer.ps1 -Version 0.5.0
#>
[CmdletBinding()]
param(
    [string]$Version = '0.6.0',
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Split-Path -Parent $scriptDir
$csproj = Join-Path $projectDir 'FracturingFogCLD.csproj'
$stageDir = Join-Path $scriptDir 'Stage'
$wxsFile = Join-Path $scriptDir 'FracturingFog.wxs'
$msiOut = Join-Path $scriptDir "FracturingFog-$Version-x64.msi"

if (-not (Test-Path $csproj)) { throw "csproj not found: $csproj" }

# Verify wix tool present
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    throw "WiX v5 not installed. Run: dotnet tool install --global wix --version 5.0.2"
}

# Verify required extensions
$exts = & wix extension list -g
if ($exts -notmatch 'WixToolset\.UI\.wixext') {
    Write-Host "Installing WiX UI extension..."
    & wix extension add -g WixToolset.UI.wixext/5.0.2
}
if ($exts -notmatch 'WixToolset\.Util\.wixext') {
    Write-Host "Installing WiX Util extension..."
    & wix extension add -g WixToolset.Util.wixext/5.0.2
}

if (-not $SkipPublish) {
    Write-Host "Publishing self-contained win-x64 to $stageDir ..."
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    & dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} else {
    if (-not (Test-Path $stageDir)) { throw "Stage dir missing: $stageDir" }
}

Write-Host "Compiling MSI -> $msiOut ..."
Push-Location $scriptDir
try {
    & wix build $wxsFile `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        -d ProductVersion=$Version `
        -o $msiOut
    if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$size = [math]::Round((Get-Item $msiOut).Length / 1MB, 2)
Write-Host ""
Write-Host "Built: $msiOut ($size MB)" -ForegroundColor Green
