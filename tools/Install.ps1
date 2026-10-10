# One-time setup, safe to run again: the .NET SDK check, the two BepInEx profiles, Doorstop 4 for ULTRAKILL (backed up
# first, disabled by default), and both plugins built against your own installs.
#   powershell -ExecutionPolicy Bypass -File tools\Install.ps1
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$steam = Get-SteamInfo
if (-not $steam.UkDir) { throw 'ULTRAKILL is not installed in any Steam library' }
if (-not $steam.RorDir) { throw 'Risk of Rain 2 is not installed in any Steam library' }
"ULTRAKILL:      $($steam.UkDir)"
"Risk of Rain 2: $($steam.RorDir)"
if (Get-Process 'Risk of Rain 2', 'ULTRAKILL' -ErrorAction SilentlyContinue) { throw 'close Risk of Rain 2 and ULTRAKILL first' }

$dotnet = Get-Dotnet
$sdks = & $dotnet --list-sdks 2>$null
if (-not $sdks) {
    'The .NET SDK is missing: installing it with winget...'
    winget install --id Microsoft.DotNet.SDK.8 -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity
    if (-not (& (Get-Dotnet) --list-sdks 2>$null)) { throw 'the .NET SDK did not install: get it from https://dotnet.microsoft.com/download and run this again' }
}

# Risk of Rain 2 needs a Doorstop proxy next to its exe too (r2modman leaves one; a clean install has none)
$rorProxy = Join-Path $steam.RorDir 'winhttp.dll'
if (-not (Test-Path $rorProxy)) {
    'Risk of Rain 2 has no Doorstop (winhttp.dll): install BepInExPack once with r2modman, or copy winhttp.dll and doorstop_config.ini from'
    "$Root\downloads\bbepis-BepInExPack-*.zip (folder BepInExPack) next to 'Risk of Rain 2.exe' with 'enabled = false' in the ini."
    throw 'Risk of Rain 2 has no Doorstop proxy'
}

& (Join-Path $PSScriptRoot 'Setup-Profiles.ps1') -Root $Root
& (Join-Path $PSScriptRoot 'Install-UkDoorstop.ps1') -Root $Root
& (Join-Path $PSScriptRoot 'Build.ps1') -Root $Root
''
'Done. To play:  powershell -ExecutionPolicy Bypass -File tools\Play.ps1   (then pick V1 in character select)'
