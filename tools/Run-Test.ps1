# Unattended test run: starts Risk of Rain 2 on the bridge profile in test mode, waits for it to finish, collects
# results.json, both screenshots and both BepInEx logs, and proves the real saves were not touched.
#   tools\Run-Test.ps1 -Scenario handshake
param(
    [string]$Scenario = 'handshake',
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [int]$TimeoutSec = 600,
    [string[]]$ExtraArgs = @()
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$steam = Get-SteamInfo
$rorProfile = Join-Path $Root 'profiles\ror2'
$ukProfile = Join-Path $Root 'profiles\uk'
$out = Join-Path $Root ("test-output\{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Scenario)
New-Item -ItemType Directory -Force $out | Out-Null

if (Get-Process 'Risk of Rain 2', 'ULTRAKILL' -ErrorAction SilentlyContinue) { throw 'Risk of Rain 2 or ULTRAKILL is already running: close them first' }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running' }

# what must not change: the real saves and settings of both games, and their folders' mod loader files
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
        # steam_autocloud.vdf is the Steam client's own marker (account id only), rewritten whenever the app runs
        foreach ($f in Get-ChildItem $w -Recurse -File -Force | Where-Object Name -ne 'steam_autocloud.vdf') { $h[$f.FullName] = '{0}|{1}' -f $f.Length, $f.LastWriteTimeUtc.Ticks }
    }
    $h
}
$before = Get-Snapshot
# and a copy of the save folders, so a run that did touch something can be undone
$backup = Join-Path $Root ("backups\saves\{0}" -f (Split-Path $out -Leaf))
New-Item -ItemType Directory -Force $backup | Out-Null
$i = 0
foreach ($w in $watched) {
    $i++
    if ((Test-Path $w -PathType Container) -and $w -notmatch 'BepInEx$|Mods$') { Copy-Item $w (Join-Path $backup ("{0:00}-{1}" -f $i, (Split-Path $w -Leaf))) -Recurse -Force }
}
Set-Content (Join-Path $backup 'sources.txt') ($watched -join "`r`n")

$preloader = Join-Path $rorProfile 'BepInEx\core\BepInEx.Preloader.dll'
$launch = @('-applaunch', '632360', '--doorstop-enabled', 'true', '--doorstop-target-assembly', "`"$preloader`"",
    '--v1bridge-test', $Scenario, '--v1bridge-out', "`"$out`"") + $ExtraArgs
foreach ($log in (Join-Path $rorProfile 'BepInEx\LogOutput.log'), (Join-Path $ukProfile 'BepInEx\LogOutput.log')) { if (Test-Path $log) { Remove-Item $log -Force } }
Start-Process -FilePath $steam.SteamExe -ArgumentList ($launch -join ' ')

$sw = [Diagnostics.Stopwatch]::StartNew()
$ror = $null
while (-not $ror -and $sw.Elapsed.TotalSeconds -lt 90) { Start-Sleep -Milliseconds 500; $ror = Get-Process 'Risk of Rain 2' -ErrorAction SilentlyContinue | Select-Object -First 1 }
$notes = @()
if (-not $ror) { $notes += 'Risk of Rain 2 never started' }
else {
    while (-not $ror.HasExited -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) { Start-Sleep -Seconds 1 }
    if (-not $ror.HasExited) { $notes += "Risk of Rain 2 still running after $TimeoutSec s: killed"; $ror | Stop-Process -Force }
}
Start-Sleep -Seconds 8
$uk = Get-Process ULTRAKILL -ErrorAction SilentlyContinue
if ($uk) { $notes += 'ULTRAKILL was still running after Risk of Rain 2 closed: killed'; $uk | Stop-Process -Force }

$logs = @{ 'ror2-LogOutput.log' = (Join-Path $rorProfile 'BepInEx\LogOutput.log'); 'uk-LogOutput.log' = (Join-Path $ukProfile 'BepInEx\LogOutput.log') }
foreach ($k in $logs.Keys) { if (Test-Path $logs[$k]) { Copy-Item $logs[$k] (Join-Path $out $k) -Force } else { $notes += "no log: $($logs[$k])" } }

$after = Get-Snapshot
$changed = @()
foreach ($k in $after.Keys) { if (-not $before.ContainsKey($k)) { $changed += "added   $k" } elseif ($before[$k] -ne $after[$k]) { $changed += "changed $k" } }
foreach ($k in $before.Keys) { if (-not $after.ContainsKey($k)) { $changed += "removed $k" } }
$changed | Set-Content (Join-Path $out 'real-files-changed.txt')

"output: $out"
"elapsed: {0:0} s" -f $sw.Elapsed.TotalSeconds
$results = Join-Path $out 'results.json'
if (Test-Path $results) { Get-Content $results -Raw } else { $notes += 'no results.json' }
"real save/config files watched: $($before.Count), changed: $($changed.Count)"
$changed | ForEach-Object { "  $_" }
$notes | ForEach-Object { "NOTE: $_" }
$pass = (Test-Path $results) -and ((Get-Content $results -Raw) -match '"pass":\s*true') -and $changed.Count -eq 0 -and $notes.Count -eq 0
"RESULT: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
if (-not $pass) { exit 1 }
