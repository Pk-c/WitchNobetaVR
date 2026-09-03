<#
    Downloads the BepInEx IL2CPP runtime this mod is built against into
    tools\cache, which is git-ignored: the archive is a third-party binary and
    has no business in the repository.

    Pinned deliberately. BepInEx 6 is pre-release and its Il2CppInterop output
    changes between builds, so an unpinned fetch would silently invalidate the
    interop assemblies the plugin compiled against.
#>
[CmdletBinding()]
param(
    [string] $Version = '6.0.0-pre.2'
)

$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot 'cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

$name = "BepInEx-Unity.IL2CPP-win-x64-$Version.zip"
$url  = "https://github.com/BepInEx/BepInEx/releases/download/v$Version/$name"
$out  = Join-Path $cache 'BepInEx-IL2CPP-win-x64.zip'

Write-Host "GET $url"
Invoke-WebRequest -Uri $url -OutFile $out
Write-Host "-> $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
