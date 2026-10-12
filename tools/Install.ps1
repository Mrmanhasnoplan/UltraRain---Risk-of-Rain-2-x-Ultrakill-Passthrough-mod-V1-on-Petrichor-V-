# One-time setup, safe to run again: the two BepInEx profiles, Doorstop 4 for ULTRAKILL (backed up first, disabled by
# default), and the two plugins. The plugins come ready-built from prebuilt\ when they are there, so nothing has to be
# compiled and no .NET SDK is needed; otherwise (or with -Build) they are built against your own installs, with an
# installed .NET SDK or one fetched into .tools\dotnet as a plain ZIP.
#   Install.cmd   (double-click), or:   powershell -ExecutionPolicy Bypass -File tools\Install.ps1
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$Build)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$steam = Get-SteamInfo
if (-not $steam.RorDir) { throw 'Risk of Rain 2 is not installed in any Steam library' }
$prebuilt = Join-Path (Split-Path $PSScriptRoot -Parent) 'prebuilt'
$ready = (Test-Path (Join-Path $prebuilt 'RorBridge.dll')) -and (Test-Path (Join-Path $prebuilt 'UkBridge.dll'))
# Without ULTRAKILL the mod still installs, for joining a friend who plays V1 (the mod has to be on both sides): you
# play an ordinary survivor and see their V1. Playing V1 yourself needs ULTRAKILL.
$joinOnly = -not $steam.UkDir
if ($joinOnly -and ($Build -or -not $ready)) { throw 'ULTRAKILL is not installed in any Steam library, and without it the plugins cannot be built here: use a download of the mod that has the prebuilt folder' }
"ULTRAKILL:      $(if ($joinOnly) { 'not installed: setting up for joining a friend''s V1 game only' } else { $steam.UkDir })"
"Risk of Rain 2: $($steam.RorDir)"
if (Get-Process 'Risk of Rain 2', 'ULTRAKILL' -ErrorAction SilentlyContinue) { throw 'close Risk of Rain 2 and ULTRAKILL first' }

# Risk of Rain 2 needs a Doorstop proxy next to its exe too (r2modman leaves one; a clean install has none)
$rorProxy = Join-Path $steam.RorDir 'winhttp.dll'
if (-not (Test-Path $rorProxy)) {
    'Risk of Rain 2 has no Doorstop (winhttp.dll): install BepInExPack once with r2modman, or copy winhttp.dll and doorstop_config.ini from'
    "$Root\downloads\bbepis-BepInExPack-*.zip (folder BepInExPack) next to 'Risk of Rain 2.exe' with 'enabled = false' in the ini."
    throw 'Risk of Rain 2 has no Doorstop proxy'
}

& (Join-Path $PSScriptRoot 'Setup-Profiles.ps1') -Root $Root
if (-not $joinOnly) { & (Join-Path $PSScriptRoot 'Install-UkDoorstop.ps1') -Root $Root }

if ($ready -and -not $Build) {
    foreach ($p in @(@('RorBridge', 'ror2'), @('UkBridge', 'uk'))) {
        $to = Join-Path $Root "profiles\$($p[1])\BepInEx\plugins\$($p[0])"
        New-Item -ItemType Directory -Force $to | Out-Null
        Copy-Item (Join-Path $prebuilt "$($p[0]).dll") $to -Force
    }
    'Plugins: the ready-built ones from prebuilt\ (to build them yourself instead: tools\Install.ps1 -Build)'
}
else {
    if (-not (Get-Dotnet)) {
        'No .NET SDK on this PC: fetching one for this project only (nothing is installed in Windows)...'
        Install-LocalSdk
    }
    & (Join-Path $PSScriptRoot 'Build.ps1') -Root $Root
    'Plugins: built on this PC'
}
''
if ($joinOnly) { 'Done. To play:  Play.cmd  (then join your friend''s game as any survivor but V1)' }
else { 'Done. To play:  Play.cmd  (then pick V1 in character select)' }
