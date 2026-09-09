<#
    Prints the path to a Little Witch Nobeta install, or nothing if none is found.

        $GameDir = & .\tools\Find-GameDir.ps1

    Asks Steam rather than guessing a drive letter: the client records its own install in the
    registry and every library folder it knows about in libraryfolders.vdf, so a game on a
    second drive is found without anyone typing a path. Callers still take -GameDir, which is
    the answer for a copy Steam has never heard of.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
if (-not $steam) {
    $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
}
if (-not $steam) { return }

# The client's own folder first, then every library it knows about -- one "path" entry each.
# Both come out with their separators as Steam wrote them: forward slashes in the registry,
# doubled backslashes in the VDF. Windows takes either, and GetFullPath tidies the survivor.
$roots = @($steam)
$vdf = Join-Path $steam 'steamapps/libraryfolders.vdf'

if (Test-Path $vdf) {
    $pattern = [regex] '"path"\s+"(.+?)"'
    foreach ($found in $pattern.Matches((Get-Content $vdf -Raw))) {
        $roots += $found.Groups[1].Value
    }
}

foreach ($root in $roots) {
    $candidate = Join-Path $root 'steamapps/common/Little Witch Nobeta'
    if (Test-Path (Join-Path $candidate 'LittleWitchNobeta.exe')) {
        return [System.IO.Path]::GetFullPath($candidate)
    }
}
