<#
.SYNOPSIS
    Installs NobetaVR into a Little Witch Nobeta install for development.

.DESCRIPTION
    Two halves, because they change at very different rates:

      -Loader   unpacks the BepInEx IL2CPP runtime and the OpenXR provider into the game
                folder. Needed once per game install, and again after bumping either.
      -Package  builds the same two halves into dist\NobetaVR-<version>.zip instead:
                the archive players unzip into their own game folder. Touches no
                install of the game at all.
      (default) builds the plugin and copies it to BepInEx\plugins.

    Both are additive and reversible. -Uninstall removes exactly the files this script puts
    down and nothing else -- note that the OpenXR provider lands *inside* the game's own
    plugin folder, next to libOni.dll and steam_api64.dll, so that folder is never removed
    wholesale. Save games live in LittleWitchNobeta_Data\Save and are never touched.

.EXAMPLE
    .\deploy.ps1 -Loader      # first time on a new game install
    .\deploy.ps1              # every build after that
    .\deploy.ps1 -Package     # build the archive to publish
    .\deploy.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [switch] $Loader,
    [switch] $Package,
    [switch] $Uninstall,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $GameDir) {
    $GameDir = & (Join-Path $PSScriptRoot 'tools\Find-GameDir.ps1')
    if (-not $GameDir) {
        throw 'Could not find a Little Witch Nobeta install through Steam. Pass -GameDir with the folder holding LittleWitchNobeta.exe.'
    }
    Write-Host "Game found at $GameDir"
}

if (-not (Test-Path (Join-Path $GameDir 'LittleWitchNobeta.exe'))) {
    throw "No LittleWitchNobeta.exe under '$GameDir'. Pass -GameDir with the real path."
}

$dataDir    = Join-Path $GameDir 'LittleWitchNobeta_Data'
$nativeDir  = Join-Path $dataDir 'Plugins\x86_64'
$subsysDir  = Join-Path $dataDir 'UnitySubsystems'

# Everything the mod is allowed to add, so that -Uninstall is exact rather than
# approximate. Directories listed here are ones the mod created outright.
$ownedInGameRoot = @(
    'winhttp.dll'
    'doorstop_config.ini'
    '.doorstop_version'
    'changelog.txt'
    'BepInEx'
    'dotnet'
)
$ownedNativeFiles = @(
    'UnityOpenXR.dll'
    'openxr_loader.dll'
    'UnityOpenXR-LICENSE.md'
    'UnityOpenXR-THIRD-PARTY-NOTICES.md'
)

if ($Uninstall) {
    foreach ($item in $ownedInGameRoot) {
        $path = Join-Path $GameDir $item
        if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Host "removed  $item" }
    }
    foreach ($file in $ownedNativeFiles) {
        $path = Join-Path $nativeDir $file
        if (Test-Path $path) { Remove-Item $path -Force; Write-Host "removed  Plugins\x86_64\$file" }
    }
    # The game shipped no UnitySubsystems folder at all, so the whole thing is ours.
    if (Test-Path $subsysDir) { Remove-Item $subsysDir -Recurse -Force; Write-Host 'removed  UnitySubsystems' }

    Write-Host 'NobetaVR uninstalled.' -ForegroundColor Green
    return
}

if ($Loader) {
    $zip = Join-Path $root 'tools\cache\BepInEx-IL2CPP-win-x64.zip'
    if (-not (Test-Path $zip)) { & (Join-Path $root 'tools\fetch_bepinex.ps1') }

    Write-Host "Unpacking BepInEx into $GameDir"
    Expand-Archive -Path $zip -DestinationPath $GameDir -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'BepInEx\plugins') | Out-Null

    $stage = Join-Path $root 'packaging\xr'
    if (-not (Test-Path (Join-Path $stage 'UnityOpenXR.dll'))) { & (Join-Path $root 'tools\fetch_openxr.ps1') }

    Write-Host 'Installing the OpenXR provider'
    New-Item -ItemType Directory -Force -Path $nativeDir | Out-Null
    foreach ($file in $ownedNativeFiles) {
        Copy-Item (Join-Path $stage $file) $nativeDir -Force
    }

    # The engine reads this at startup to learn that an "OpenXR Display" subsystem exists.
    # It is read before any managed code runs, so it has to be on disk before launch -- a
    # plugin cannot put it there in time for the launch that is already under way.
    Copy-Item (Join-Path $stage 'UnitySubsystems') $dataDir -Recurse -Force

    Write-Host 'Loader installed. The first launch generates BepInEx\interop and takes about 15s.' -ForegroundColor Green
}

# --- plugin ------------------------------------------------------------------

# Remember where the game is, so that building the project on its own -- from an editor,
# or a bare dotnet build -- finds the same install without searching for it again.
$props = Join-Path $root 'src\NobetaVR\GameDir.props'
@(
    '<Project>'
    '  <PropertyGroup>'
    "    <GameDir>$GameDir</GameDir>"
    '  </PropertyGroup>'
    '</Project>'
) | Set-Content -Path $props -Encoding utf8

$proj = Join-Path $root 'src\NobetaVR\NobetaVR.csproj'
Write-Host "Building NobetaVR ($Configuration)"
dotnet build $proj -c $Configuration -p:GameDir="$GameDir" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$dll  = Join-Path $root "build\$Configuration\NobetaVR.dll"

# --- the archive players download --------------------------------------------

# Laid out as the game folder it unzips into, so that unzipping is the whole of the
# install: BepInEx and its runtime at the root, the plugin under BepInEx\plugins, and
# the OpenXR provider inside the game's own native plugin folder, which is where the
# engine looks for it before any managed code runs.
if ($Package) {
    $version = ([xml] (Get-Content $proj)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw 'No <Version> in NobetaVR.csproj.' }

    $stageDir = Join-Path $root 'build\package'
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    $zip = Join-Path $root 'tools\cache\BepInEx-IL2CPP-win-x64.zip'
    if (-not (Test-Path $zip)) { & (Join-Path $root 'tools\fetch_bepinex.ps1') }
    Expand-Archive -Path $zip -DestinationPath $stageDir -Force

    $stage = Join-Path $root 'packaging\xr'
    if (-not (Test-Path (Join-Path $stage 'UnityOpenXR.dll'))) {
        & (Join-Path $root 'tools\fetch_openxr.ps1')
    }

    $stagePlugins = Join-Path $stageDir 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $stagePlugins | Out-Null
    Copy-Item $dll $stagePlugins -Force

    $stageNative = Join-Path $stageDir 'LittleWitchNobeta_Data\Plugins\x86_64'
    New-Item -ItemType Directory -Force -Path $stageNative | Out-Null
    foreach ($file in $ownedNativeFiles) {
        Copy-Item (Join-Path $stage $file) $stageNative -Force
    }
    Copy-Item (Join-Path $stage 'UnitySubsystems') (Join-Path $stageDir 'LittleWitchNobeta_Data') -Recurse -Force

    # Prefixed rather than left as a bare LICENSE: these land next to the game's own
    # files, and nothing dropped into a game folder should read as the game's.
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stageDir 'NobetaVR-LICENSE.txt') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY.txt') (Join-Path $stageDir 'NobetaVR-THIRD-PARTY.txt') -Force

    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $archive = Join-Path $dist "NobetaVR-$version.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $archive

    $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "Packaged  $archive  ($size MB)" -ForegroundColor Green
    return
}

# --- the development copy ----------------------------------------------------

$dest = Join-Path $GameDir 'BepInEx\plugins'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $dll $dest -Force
Write-Host "Deployed  NobetaVR.dll -> $dest" -ForegroundColor Green
