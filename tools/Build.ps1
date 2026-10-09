# Builds both plugins against the local game installs and copies them into the bridge profiles.
# -Root: where profiles\ and build\ live (default: the repo).
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$steam = Get-SteamInfo
if (-not $steam.UkDir) { throw 'ULTRAKILL not found in any Steam library' }
if (-not $steam.RorDir) { throw 'Risk of Rain 2 not found in any Steam library' }
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Get-Dotnet
foreach ($proj in 'src\UkBridge\UkBridge.csproj', 'src\RorBridge\RorBridge.csproj') {
    & $dotnet build (Join-Path $repo $proj) -c $Configuration -nologo -v q `
        "-p:UkDir=$($steam.UkDir)" "-p:RorDir=$($steam.RorDir)" "-p:BuildRoot=$Root\build" "-p:ProfilesRoot=$Root\profiles"
    if ($LASTEXITCODE -ne 0) { throw "build failed: $proj" }
}
