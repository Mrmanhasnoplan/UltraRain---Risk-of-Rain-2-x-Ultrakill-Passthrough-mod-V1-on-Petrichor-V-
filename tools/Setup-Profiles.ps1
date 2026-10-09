# Builds the two isolated BepInEx profiles the bridge runs from. Nothing is written to either game folder:
# the profiles live under <repo>\profiles (git-ignored) and are selected per launch with Doorstop's command-line switches.
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = $Root
$dl   = Join-Path $root 'downloads'
$ror  = Join-Path $root 'profiles\ror2'
$uk   = Join-Path $root 'profiles\uk'
New-Item -ItemType Directory -Force $dl, $ror, $uk | Out-Null

$thunderstore = @(
    'bbepis/BepInExPack', 'RiskofThunder/RoR2BepInExPack', 'RiskofThunder/FixPluginTypesSerialization',
    'RiskofThunder/HookGenPatcher', 'RiskofThunder/R2API_Core', 'RiskofThunder/R2API_ContentManagement',
    'RiskofThunder/R2API_Prefab', 'RiskofThunder/R2API_Language'
)

function Expand-Entries([string]$zip, [string]$dest, [scriptblock]$map) {
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $z.Entries) {
            if ($e.FullName.EndsWith('/')) { continue }
            $rel = & $map $e.FullName
            if (-not $rel) { continue }
            $to = Join-Path $dest $rel
            New-Item -ItemType Directory -Force (Split-Path $to -Parent) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $to, $true)
        }
    } finally { $z.Dispose() }
}

# --- Risk of Rain 2
if ($Force -or -not (Test-Path (Join-Path $ror 'BepInEx\core\BepInEx.dll'))) {
    $versions = @()
    foreach ($p in $thunderstore) {
        $j = Invoke-RestMethod "https://thunderstore.io/api/experimental/package/$p/"
        $zip = Join-Path $dl "$($j.latest.full_name).zip"
        if (-not (Test-Path $zip)) { Invoke-WebRequest $j.latest.download_url -OutFile $zip }
        $versions += $j.latest.full_name
        Expand-Entries $zip $ror {
            param($n)
            if ($n -match '^BepInExPack/(BepInEx/.+)$') { return $Matches[1] }
            if ($n -match '^BepInEx/.+$') { return $n }
            if ($n -match '^(plugins|patchers|monomod|config)/.+$') { return "BepInEx/$n" }
            return $null
        }
    }
    $versions | Set-Content (Join-Path $ror 'packages.txt')
    "RoR2 profile: $ror"
    $versions | ForEach-Object { "  $_" }
}

# --- ULTRAKILL
if ($Force -or -not (Test-Path (Join-Path $uk 'BepInEx\core\BepInEx.dll'))) {
    $rel = Invoke-RestMethod 'https://api.github.com/repos/BepInEx/BepInEx/releases' -Headers @{ 'User-Agent' = 'v1bridge' }
    $r5 = $rel | Where-Object { $_.tag_name -like 'v5.4.*' -and -not $_.prerelease } | Select-Object -First 1
    $asset = $r5.assets | Where-Object { $_.name -match 'win_x64' } | Select-Object -First 1
    $zip = Join-Path $dl $asset.name
    if (-not (Test-Path $zip)) { Invoke-WebRequest $asset.browser_download_url -OutFile $zip }
    Expand-Entries $zip $uk { param($n) if ($n -match '^BepInEx/.+$') { $n } }
    New-Item -ItemType Directory -Force (Join-Path $uk 'BepInEx\plugins'), (Join-Path $uk 'BepInEx\config') | Out-Null
    $asset.name | Set-Content (Join-Path $uk 'packages.txt')
    "ULTRAKILL profile: $uk ($($asset.name))"
}
