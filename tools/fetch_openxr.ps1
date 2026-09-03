<#
    Downloads Unity's OpenXR provider package and stages the three files the mod actually
    needs into packaging\xr.

    Version 1.6.0 is pinned deliberately: its package.json declares `unity: 2020.3`, which
    is the engine this game was built with. A provider built for a newer editor declares a
    newer XR SDK interface version, and a 2020.3 engine will not register its subsystems --
    the failure is silent, showing up only as an empty descriptor list.

    Nothing here is committed. The binaries are third-party and belong in the release
    archive, not in the repository, so packaging\xr is git-ignored and rebuilt on demand.
#>
[CmdletBinding()]
param(
    [string] $Version = '1.6.0'
)

$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $PSScriptRoot 'cache'
$stage = Join-Path $root 'packaging\xr'

New-Item -ItemType Directory -Force -Path $cache | Out-Null

$tgz = Join-Path $cache "openxr-$Version.tgz"
if (-not (Test-Path $tgz)) {
    $url = "https://packages.unity.com/com.unity.xr.openxr/-/com.unity.xr.openxr-$Version.tgz"
    Write-Host "GET $url"
    Invoke-WebRequest -Uri $url -OutFile $tgz
}

$extract = Join-Path $cache "openxr-$Version"
if (-not (Test-Path $extract)) {
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    tar -xzf $tgz -C $extract
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the OpenXR package.' }
}

$pkg = Join-Path $extract 'package'

# Sanity-check the version we extracted rather than trusting the file name: a stale cache
# entry with the right name and the wrong contents would be very hard to diagnose later.
$declared = (Get-Content (Join-Path $pkg 'package.json') -Raw | ConvertFrom-Json)
if ($declared.version -ne $Version) {
    throw "Cached package declares version $($declared.version), expected $Version. Delete $extract and retry."
}
if ($declared.unity -ne '2020.3') {
    Write-Warning "This package declares unity $($declared.unity); the game is 2020.3. Subsystem registration may fail."
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'UnitySubsystems\UnityOpenXR') | Out-Null

Copy-Item (Join-Path $pkg 'Runtime\windows\x64\UnityOpenXR.dll')        $stage -Force
Copy-Item (Join-Path $pkg 'RuntimeLoaders\windows\x64\openxr_loader.dll') $stage -Force
Copy-Item (Join-Path $pkg 'Runtime\UnitySubsystemsManifest.json') `
          (Join-Path $stage 'UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json') -Force

# The Unity Package Distribution License requires the licence text to travel with the
# binaries, so it is staged as part of the payload rather than left behind in the cache.
Copy-Item (Join-Path $pkg 'LICENSE.md') (Join-Path $stage 'UnityOpenXR-LICENSE.md') -Force
Copy-Item (Join-Path $pkg 'Third Party Notices.md') `
          (Join-Path $stage 'UnityOpenXR-THIRD-PARTY-NOTICES.md') -Force

Write-Host "Staged OpenXR $Version into $stage" -ForegroundColor Green
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    Write-Host ("  {0,-45} {1,8:N0} bytes" -f $_.FullName.Substring($stage.Length + 1), $_.Length)
}
