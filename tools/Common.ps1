# Shared by the tools: where Steam and the two games are.
function Get-SteamInfo {
    $reg = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop
    $steam = $reg.SteamPath -replace '/', '\'
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
    }
    $find = {
        param($appId, $exe)
        foreach ($lib in ($libs | Select-Object -Unique)) {
            $manifest = Join-Path $lib "steamapps\appmanifest_$appId.acf"
            if (-not (Test-Path $manifest)) { continue }
            $dir = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"').Groups[1].Value
            $game = Join-Path $lib "steamapps\common\$dir"
            if (Test-Path (Join-Path $game $exe)) { return $game }
        }
        return $null
    }
    [pscustomobject]@{
        SteamExe = ($reg.SteamExe -replace '/', '\')
        SteamDir = $steam
        UkDir    = & $find 1229490 'ULTRAKILL.exe'
        RorDir   = & $find 632360 'Risk of Rain 2.exe'
    }
}

# The project's own copy of the .NET SDK, for PCs that have none (see Install-LocalSdk).
$script:LocalSdkDir = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\dotnet'

# A dotnet.exe that has an SDK to build with: an installed one first, then the project's own copy. $null: there is none.
# (A PC can have dotnet.exe with runtimes only, or no dotnet at all.)
function Get-Dotnet {
    $candidates = @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $script:LocalSdkDir 'dotnet.exe')
    foreach ($d in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path $d)) { continue }
        $sdks = $null
        try { $ErrorActionPreference = 'Continue'; $sdks = & $d --list-sdks 2>$null } catch { }
        if ($sdks) { return $d }
    }
    return $null
}

# The .NET 8 SDK as Microsoft's plain ZIP, unpacked into .tools\dotnet: no installer, no administrator rights, nothing
# registered with Windows (the installer is what fails on some PCs). Checked against the hash Microsoft publishes.
function Install-LocalSdk {
    $root = Split-Path $PSScriptRoot -Parent
    $dl = Join-Path $root 'downloads'
    New-Item -ItemType Directory -Force $dl | Out-Null
    $ProgressPreference = 'SilentlyContinue'
    $meta = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
    $release = $meta.releases | Where-Object { $_.'release-version' -eq $meta.'latest-release' } | Select-Object -First 1
    $file = $release.sdk.files | Where-Object { $_.name -eq 'dotnet-sdk-win-x64.zip' } | Select-Object -First 1
    if (-not $file) { throw 'no .NET SDK ZIP for 64-bit Windows in the release list' }
    $zip = Join-Path $dl (Split-Path $file.url -Leaf)
    $ok = { (Test-Path $zip) -and (Get-FileHash $zip -Algorithm SHA512).Hash -eq $file.hash }
    if (-not (& $ok)) {
        "downloading $(Split-Path $zip -Leaf) (about 270 MB)..."
        Invoke-WebRequest $file.url -OutFile $zip -UseBasicParsing
        if (-not (& $ok)) { Remove-Item $zip -Force; throw 'the .NET SDK download is damaged (its hash does not match): run this again' }
    }
    "unpacking it to $script:LocalSdkDir ..."
    if (Test-Path $script:LocalSdkDir) { Remove-Item $script:LocalSdkDir -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $script:LocalSdkDir)
    if (-not (Get-Dotnet)) { throw "the .NET SDK did not unpack properly into $script:LocalSdkDir" }
}
