# Starts Risk of Rain 2 on the bridge profile for normal play (no test mode). Pick V1 in character select;
# ULTRAKILL starts by itself and closes with Risk of Rain 2. Your real saves are not touched: both games run on the
# copies inside profiles\.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$steam = Get-SteamInfo
if (Get-Process 'Risk of Rain 2', 'ULTRAKILL' -ErrorAction SilentlyContinue) { throw 'Risk of Rain 2 or ULTRAKILL is already running: close them first' }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running' }
$preloader = Join-Path $Root 'profiles\ror2\BepInEx\core\BepInEx.Preloader.dll'
if (-not (Test-Path $preloader)) { throw 'run tools\Setup-Profiles.ps1 and tools\Build.ps1 first' }
Start-Process -FilePath $steam.SteamExe -ArgumentList "-applaunch 632360 --doorstop-enabled true --doorstop-target-assembly `"$preloader`""
'Risk of Rain 2 is starting on the bridge profile. Logs: profiles\ror2\BepInEx\LogOutput.log and profiles\uk\BepInEx\LogOutput.log'
