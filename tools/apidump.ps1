<#
    Prints the real member list of a type in the game's Il2CppInterop assemblies.

        .\tools\apidump.ps1 SceneManager XRSettings
        .\tools\apidump.ps1 --types Nobeta        # find types by name

    Interop assemblies are read from the game install; override with -GameDir.
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Names
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $GameDir) {
    $GameDir = & (Join-Path $PSScriptRoot 'Find-GameDir.ps1')
    if (-not $GameDir) { throw 'Could not find the game through Steam. Pass -GameDir.' }
}
$dll  = Join-Path $root 'build\apidump\apidump.dll'

if (-not (Test-Path $dll) -or
    (Get-Item (Join-Path $PSScriptRoot 'apidump\Program.cs')).LastWriteTime -gt (Get-Item $dll).LastWriteTime) {
    dotnet build (Join-Path $PSScriptRoot 'apidump\apidump.csproj') -c Debug --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'apidump build failed.' }
}

$env:NOBETA_INTEROP = Join-Path $GameDir 'BepInEx\interop'
dotnet $dll @Names
