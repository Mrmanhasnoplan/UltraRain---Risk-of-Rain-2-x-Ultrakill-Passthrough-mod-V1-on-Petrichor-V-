# Unattended two-game test on one PC: one Risk of Rain 2 hosts as Commando (scenario mphost), a second one joins it as
# V1 (scenario mpjoin) over localhost, with its ULTRAKILL. Both run on the bridge profile and are the same Steam user:
# it stands in for a host and a joining player, it is not a test with a second person. Collects both results, the
# screenshots and the logs, and proves the real saves were not touched, like Run-Test.ps1.
#   tools\Run-MpTest.ps1
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [int]$TimeoutSec = 720,
    [string]$Stage = 'golemplains'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$steam = Get-SteamInfo
$rorProfile = Join-Path $Root 'profiles\ror2'
$ukProfile = Join-Path $Root 'profiles\uk'
$out = Join-Path $Root ("test-output\{0}-mp" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$hostOut = Join-Path $out 'host'
$joinOut = Join-Path $out 'join'
New-Item -ItemType Directory -Force $hostOut, $joinOut | Out-Null

if (Get-Process 'Risk of Rain 2', 'ULTRAKILL' -ErrorAction SilentlyContinue) { throw 'Risk of Rain 2 or ULTRAKILL is already running: close them first' }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running' }

$watched = @(
    (Join-Path $steam.UkDir 'Saves'), (Join-Path $steam.UkDir 'Preferences'), (Join-Path $steam.UkDir 'BepInEx'),
    (Join-Path $steam.UkDir 'doorstop_config.ini'), (Join-Path $steam.UkDir 'winhttp.dll'),
    (Join-Path $steam.RorDir 'Risk of Rain 2_Data\Config'), (Join-Path $steam.RorDir 'doorstop_config.ini'), (Join-Path $steam.RorDir 'winhttp.dll'),
    (Join-Path $steam.RorDir 'BepInEx'), (Join-Path $steam.RorDir 'Mods')
) + @(Get-ChildItem (Join-Path $steam.SteamDir 'userdata') -Directory | ForEach-Object { Join-Path $_.FullName '632360\remote'; Join-Path $_.FullName '1229490\remote' })
function Get-Snapshot {
    $h = @{}
    foreach ($w in $watched) {
        if (-not (Test-Path $w)) { continue }
        foreach ($f in Get-ChildItem $w -Recurse -File -Force | Where-Object Name -ne 'steam_autocloud.vdf') { $h[$f.FullName] = '{0}|{1}' -f $f.Length, $f.LastWriteTimeUtc.Ticks }
    }
    $h
}
$before = Get-Snapshot
$backup = Join-Path $Root ("backups\saves\{0}" -f (Split-Path $out -Leaf))
New-Item -ItemType Directory -Force $backup | Out-Null
$i = 0
foreach ($w in $watched) {
    $i++
    if ((Test-Path $w -PathType Container) -and $w -notmatch 'BepInEx$|Mods$') { Copy-Item $w (Join-Path $backup ("{0:00}-{1}" -f $i, (Split-Path $w -Leaf))) -Recurse -Force }
}

$preloader = Join-Path $rorProfile 'BepInEx\core\BepInEx.Preloader.dll'
$doorstop = "--doorstop-enabled true --doorstop-target-assembly `"$preloader`""
Get-ChildItem (Join-Path $rorProfile 'BepInEx') -Filter 'LogOutput*' | ForEach-Object { Remove-Item $_.FullName -Force }
$ukLog = Join-Path $ukProfile 'BepInEx\LogOutput.log'
if (Test-Path $ukLog) { Remove-Item $ukLog -Force }

# the host, through Steam; then the joining game, started directly as the same Steam app (Steam starts a game once)
Start-Process -FilePath $steam.SteamExe -ArgumentList "-applaunch 632360 $doorstop --v1bridge-test mphost --v1bridge-out `"$hostOut`" --v1bridge-mp `"$out`" --v1bridge-stage $Stage"
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-Process 'Risk of Rain 2' -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalSeconds -lt 90) { Start-Sleep -Milliseconds 500 }
$notes = @()
if (-not (Get-Process 'Risk of Rain 2' -ErrorAction SilentlyContinue)) { $notes += 'the hosting Risk of Rain 2 never started' }
else {
    # not before the host is in its lobby: two games loading the one profile at once collide on its config file
    # (the second then starts without the plugin and sits at the menu)
    while (-not (Test-Path (Join-Path $out 'host-ready')) -and $sw.Elapsed.TotalSeconds -lt 300) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path (Join-Path $out 'host-ready'))) { $notes += 'the hosting game never reached its lobby' }
    $env:SteamAppId = '632360'
    $env:SteamGameId = '632360'
    Start-Process -FilePath (Join-Path $steam.RorDir 'Risk of Rain 2.exe') -WorkingDirectory $steam.RorDir `
        -ArgumentList "$doorstop -screen-fullscreen 0 -screen-width 1280 -screen-height 720 --v1bridge-test mpjoin --v1bridge-out `"$joinOut`" --v1bridge-mp `"$out`""
    Start-Sleep -Seconds 15
    if (@(Get-Process 'Risk of Rain 2' -ErrorAction SilentlyContinue).Count -lt 2) { $notes += 'the joining Risk of Rain 2 did not start beside the host' }
    while ((Get-Process 'Risk of Rain 2' -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) { Start-Sleep -Seconds 1 }
    $left = Get-Process 'Risk of Rain 2' -ErrorAction SilentlyContinue
    if ($left) { $notes += "$(@($left).Count) Risk of Rain 2 still running after $TimeoutSec s: killed"; $left | Stop-Process -Force }
}
Start-Sleep -Seconds 8
$uk = Get-Process ULTRAKILL -ErrorAction SilentlyContinue
if ($uk) { $notes += 'ULTRAKILL was still running after Risk of Rain 2 closed: killed'; $uk | Stop-Process -Force }

Get-ChildItem (Join-Path $rorProfile 'BepInEx') -Filter 'LogOutput*' | ForEach-Object { Copy-Item $_.FullName (Join-Path $out ('ror2-' + $_.Name)) -Force }
if (Test-Path $ukLog) { Copy-Item $ukLog (Join-Path $out 'uk-LogOutput.log') -Force } else { $notes += 'no ULTRAKILL log' }

$after = Get-Snapshot
$changed = @()
foreach ($k in $after.Keys) { if (-not $before.ContainsKey($k)) { $changed += "added   $k" } elseif ($before[$k] -ne $after[$k]) { $changed += "changed $k" } }
foreach ($k in $before.Keys) { if (-not $after.ContainsKey($k)) { $changed += "removed $k" } }
$changed | Set-Content (Join-Path $out 'real-files-changed.txt')

"output: $out"
"elapsed: {0:0} s" -f $sw.Elapsed.TotalSeconds
$pass = $changed.Count -eq 0 -and $notes.Count -eq 0
foreach ($side in 'host', 'join') {
    $results = Join-Path $out "$side\results.json"
    if (Test-Path $results) {
        $j = Get-Content $results -Raw | ConvertFrom-Json
        "${side}: $(@($j.checks | Where-Object pass).Count)/$(@($j.checks).Count) checks" + $(if ($j.pass) { '' } else { ' FAILED' })
        $j.checks | Where-Object { -not $_.pass } | ForEach-Object { "  FAIL $($_.name): $($_.detail)" }
        if (-not $j.pass) { $pass = $false }
    }
    else { $notes += "no results from the $side side"; $pass = $false }
}
"real save/config files watched: $($before.Count), changed: $($changed.Count)"
$changed | ForEach-Object { "  $_" }
$notes | ForEach-Object { "NOTE: $_" }
"RESULT: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
if (-not $pass) { exit 1 }
