# ULTRAKILL (Unity 2022.3) needs Doorstop 4 to load BepInEx; an older Doorstop 3 proxy next to ULTRAKILL.exe is
# ignored by the game. This is the one change the bridge makes to a game folder: winhttp.dll, doorstop_config.ini and
# .doorstop_version, taken from the official BepInEx 5 release. The files it replaces are backed up first, and the
# installed config has Doorstop DISABLED, so a normal launch of ULTRAKILL stays unmodded; the bridge switches it on
# per launch from the command line.
#   tools\Install-UkDoorstop.ps1            install (backs up first)
#   tools\Install-UkDoorstop.ps1 -Restore   put the newest backup back
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$Restore)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$uk = (Get-SteamInfo).UkDir
if (-not $uk) { throw 'ULTRAKILL not found in any Steam library' }
if (Get-Process ULTRAKILL -ErrorAction SilentlyContinue) { throw 'ULTRAKILL is running: close it first' }
$names = 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version'
$backups = Join-Path $Root 'backups\ULTRAKILL-doorstop'

if ($Restore) {
    $last = Get-ChildItem $backups -Directory | Sort-Object Name | Select-Object -Last 1
    if (-not $last) { throw "no backup in $backups" }
    foreach ($n in $names) {
        $b = Join-Path $last.FullName $n
        $t = Join-Path $uk $n
        if (Test-Path $b) { Copy-Item $b $t -Force } elseif (Test-Path $t) { Remove-Item $t -Force }
    }
    "restored $($last.FullName) to $uk"
    return
}

$dll = Join-Path $uk 'winhttp.dll'
if ((Test-Path $dll) -and (Get-Item $dll).VersionInfo.FileVersion -like '4.*') { "Doorstop $((Get-Item $dll).VersionInfo.FileVersion) is already in $uk"; return }

$zip = Get-ChildItem (Join-Path $Root 'downloads') -Filter 'BepInEx_win_x64_5.*.zip' | Sort-Object Name | Select-Object -Last 1
if (-not $zip) { throw 'run tools\Setup-Profiles.ps1 first (it downloads BepInEx)' }

$dest = Join-Path $backups (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Force $dest | Out-Null
foreach ($n in $names) { $f = Join-Path $uk $n; if (Test-Path $f) { Copy-Item $f $dest } }
"backed up to $dest"

$z = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    foreach ($n in $names) {
        $e = $z.Entries | Where-Object { $_.FullName -eq $n }
        if (-not $e) { throw "$n is not in $($zip.Name)" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $uk $n), $true)
    }
} finally { $z.Dispose() }

# off unless a launch asks for it
$ini = Join-Path $uk 'doorstop_config.ini'
$text = Get-Content $ini -Raw
$text = [regex]::Replace($text, '(?m)^enabled\s*=\s*true\s*$', 'enabled = false')
[IO.File]::WriteAllText($ini, $text)
"installed Doorstop $((Get-Item $dll).VersionInfo.FileVersion) in $uk (disabled by default)"
Select-String -Path $ini -Pattern '^\s*(enabled|target_assembly)\s*=' | ForEach-Object { "  " + $_.Line }
