# Builds both plugins against the local game installs and copies them into the bridge profiles.
# -Root: where profiles\ and build\ live (default: the repo).
# -Pack: also copy the two plugins to prebuilt\, where Install.ps1 on another PC finds them and skips building.
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [string]$Configuration = 'Release', [switch]$Pack)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$steam = Get-SteamInfo
if (-not $steam.UkDir) { throw 'ULTRAKILL not found in any Steam library' }
if (-not $steam.RorDir) { throw 'Risk of Rain 2 not found in any Steam library' }
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Get-Dotnet
if (-not $dotnet) { throw 'no .NET SDK on this PC: run tools\Install.ps1 -Build (it fetches one into .tools\dotnet)' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
foreach ($proj in 'src\UkBridge\UkBridge.csproj', 'src\RorBridge\RorBridge.csproj') {
    & $dotnet build (Join-Path $repo $proj) -c $Configuration -nologo -v q `
        "-p:UkDir=$($steam.UkDir)" "-p:RorDir=$($steam.RorDir)" "-p:BuildRoot=$Root\build" "-p:ProfilesRoot=$Root\profiles"
    if ($LASTEXITCODE -ne 0) { throw "build failed: $proj" }
}
if ($Pack) {
    $prebuilt = Join-Path $repo 'prebuilt'
    New-Item -ItemType Directory -Force $prebuilt | Out-Null
    Copy-Item (Join-Path $Root 'profiles\ror2\BepInEx\plugins\RorBridge\RorBridge.dll'), (Join-Path $Root 'profiles\uk\BepInEx\plugins\UkBridge\UkBridge.dll') $prebuilt -Force
    "packed: $prebuilt\RorBridge.dll, $prebuilt\UkBridge.dll"
}
