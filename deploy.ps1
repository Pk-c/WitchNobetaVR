<#
.SYNOPSIS
    Installs NobetaVR into a Little Witch Nobeta install for development.

.DESCRIPTION
    Two halves, because they change at very different rates:

      -Loader   unpacks the BepInEx IL2CPP runtime and the OpenXR provider into the game
                folder. Needed once per game install, and again after bumping either.
      (default) builds the plugin and copies it to BepInEx\plugins.

    Both are additive and reversible. -Uninstall removes exactly the files this script puts
    down and nothing else -- note that the OpenXR provider lands *inside* the game's own
    plugin folder, next to libOni.dll and steam_api64.dll, so that folder is never removed
    wholesale. Save games live in LittleWitchNobeta_Data\Save and are never touched.

.EXAMPLE
    .\deploy.ps1 -Loader      # first time on a new game install
    .\deploy.ps1              # every build after that
    .\deploy.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'H:\Steam\steamapps\common\Little Witch Nobeta',
    [switch] $Loader,
    [switch] $Uninstall,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

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

$proj = Join-Path $root 'src\NobetaVR\NobetaVR.csproj'
Write-Host "Building NobetaVR ($Configuration)"
dotnet build $proj -c $Configuration -p:GameDir="$GameDir" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$dll  = Join-Path $root "build\$Configuration\NobetaVR.dll"
$dest = Join-Path $GameDir 'BepInEx\plugins'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $dll $dest -Force
Write-Host "Deployed  NobetaVR.dll -> $dest" -ForegroundColor Green
