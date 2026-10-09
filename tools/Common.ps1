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

function Get-Dotnet {
    $d = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $d) { return $d }
    return 'dotnet'
}
