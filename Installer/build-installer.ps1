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
$paletteCsproj = Join-Path $projectDir 'PaletteBuilder\PaletteBuilder.csproj'
$stageDir = Join-Path $scriptDir 'Stage'
$paletteStageDir = Join-Path $stageDir 'PaletteBuilder'
$wxsFile = Join-Path $scriptDir 'FracturingFog.wxs'
$msiOut = Join-Path $scriptDir "FracturingFog-$Version-x64.msi"

if (-not (Test-Path $csproj)) { throw "csproj not found: $csproj" }
if (-not (Test-Path $paletteCsproj)) { throw "csproj not found: $paletteCsproj" }

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

    # NETSDK1152: stale bin/obj from prior builds with differing Platform/RID
    # combos (e.g. Release\net10.0 vs Release\net10.0\win-x64 vs x64\Release)
    # cause duplicate apphost.exe / deps.json paths during publish gather.
    # Wipe scratch dirs of the generator projects (they're ProjectRef'd as
    # libs by FracturingFogCLD) before publishing.
    $scratchTargets = @(
        (Join-Path $projectDir 'CalculatorGen\bin'),
        (Join-Path $projectDir 'CalculatorGen\obj'),
        (Join-Path $projectDir 'ColorGen\bin'),
        (Join-Path $projectDir 'ColorGen\obj'),
        (Join-Path $projectDir 'PaletteBuilder\bin'),
        (Join-Path $projectDir 'PaletteBuilder\obj'),
        (Join-Path $projectDir 'PaletteBuilder\bin.lib'),
        (Join-Path $projectDir 'PaletteBuilder\obj.lib')
    )
    foreach ($t in $scratchTargets) {
        if (Test-Path $t) {
            Write-Host "Cleaning $t"
            Remove-Item $t -Recurse -Force
        }
    }

    # ErrorOnDuplicatePublishOutputFiles=false: CalculatorGen + ColorGen are
    # OutputType=Exe referenced as ProjectRef. Publish builds them under
    # multiple GlobalProperties variants (no Platform / Platform=x64 / +RID),
    # each producing apphost.exe + deps.json + runtimeconfig.json in a
    # separate obj subtree. The gather step then trips NETSDK1152. Same
    # project, same content — last-wins copy is harmless.
    & dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false `
        -p:ErrorOnDuplicatePublishOutputFiles=false -o $stageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    Write-Host "Publishing PaletteBuilder self-contained win-x64 to $paletteStageDir ..."
    & dotnet publish $paletteCsproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false `
        -p:ErrorOnDuplicatePublishOutputFiles=false -o $paletteStageDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (PaletteBuilder) failed (exit $LASTEXITCODE)" }
} else {
    if (-not (Test-Path $stageDir)) { throw "Stage dir missing: $stageDir" }
    if (-not (Test-Path $paletteStageDir)) { throw "PaletteBuilder stage dir missing: $paletteStageDir" }
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
