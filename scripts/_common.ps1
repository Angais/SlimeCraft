# Shared helpers for the SlimeCraft scripts (dot-sourced). Windows PowerShell 5.1 compatible.

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "OK  $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "!!  $Text" -ForegroundColor Yellow }
function Write-Fail([string]$Text) { Write-Host "XX  $Text" -ForegroundColor Red }

function Get-ProjectRoot {
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
}

# Locates the Slime Rancher folder: -GameDir, $env:SLIMERANCHER_DIR, the default Steam path, then every Steam library.
function Find-SlimeRancherDir {
    param([string]$GameDir, [switch]$Quiet)
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($GameDir) {
        $candidates.Add($GameDir)
    } else {
        if ($env:SLIMERANCHER_DIR) { $candidates.Add($env:SLIMERANCHER_DIR) }
        $candidates.Add('C:\Program Files (x86)\Steam\steamapps\common\Slime Rancher')
        $steam = $null
        try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath } catch { }
        if ($steam) {
            $steam = $steam -replace '/', '\'
            $candidates.Add((Join-Path $steam 'steamapps\common\Slime Rancher'))
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                $text = [System.IO.File]::ReadAllText($vdf)
                foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                    $lib = $m.Groups[1].Value -replace '\\\\', '\'
                    $candidates.Add((Join-Path $lib 'steamapps\common\Slime Rancher'))
                }
            }
        }
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath (Join-Path $c 'SlimeRancher.exe'))) {
            return (Resolve-Path -LiteralPath $c).ProviderPath.TrimEnd('\')
        }
    }
    if (-not $Quiet) {
        if ($GameDir) { Write-Fail "SlimeRancher.exe not found in '$GameDir'." }
        else { Write-Fail "Slime Rancher was not found. Pass -GameDir 'D:\SteamLibrary\steamapps\common\Slime Rancher' (or set `$env:SLIMERANCHER_DIR)." }
    }
    return $null
}

function Test-BepInEx([string]$Dir) {
    return (Test-Path -LiteralPath (Join-Path $Dir 'winhttp.dll')) -and (Test-Path -LiteralPath (Join-Path $Dir 'BepInEx\core\BepInEx.dll'))
}

function Show-BepInExHelp([string]$Dir) {
    Write-Fail "BepInEx 5 (x64) is not installed in: $Dir"
    Write-Host ""
    Write-Host "SlimeCraft is a BepInEx plugin, so BepInEx has to be installed into the game folder once:" -ForegroundColor White
    Write-Host "  Option A (script):       .\scripts\install-bepinex.ps1 -Download   (or -Zip <path to the zip>)" -ForegroundColor White
    Write-Host "  Option B (manual):       download BepInEx_win_x64_5.4.23.5.zip from" -ForegroundColor White
    Write-Host "                           https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5" -ForegroundColor White
    Write-Host "                           and extract it into the game folder, so that winhttp.dll sits next to SlimeRancher.exe." -ForegroundColor White
    Write-Host "Then run .\scripts\install.ps1 again." -ForegroundColor White
}

function Test-GameRunning {
    return $null -ne (Get-Process -Name 'SlimeRancher' -ErrorAction SilentlyContinue)
}

# The mod reads Minecraft's textures/sounds at runtime from the official launcher's folder.
function Test-MinecraftInstall {
    $mc = Join-Path $env:APPDATA '.minecraft'
    $versions = Join-Path $mc 'versions'
    if (-not (Test-Path -LiteralPath $versions)) { return $null }
    $jars = @(Get-ChildItem -LiteralPath $versions -Recurse -Filter '*.jar' -ErrorAction SilentlyContinue)
    if ($jars.Count -eq 0) { return $null }
    return $mc
}

function Get-DefaultDllPath([string]$Configuration) {
    return Join-Path (Get-ProjectRoot) ("SlimeCraft\bin\{0}\SlimeCraft.dll" -f $Configuration)
}

# Sets "Key = Value" inside [Section] of a BepInEx .cfg file (creates file/section/key as needed, keeps everything else).
function Set-BepInExCfgValue {
    param([string]$Path, [string]$Section, [string]$Key, [string]$Value)
    $lines = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $Path) {
        foreach ($l in [System.IO.File]::ReadAllLines($Path)) { $lines.Add($l) }
    }
    $header = "[$Section]"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -eq $header) { $start = $i; break }
    }
    if ($start -lt 0) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Trim() -ne '') { $lines.Add('') }
        $lines.Add($header)
        $lines.Add('')
        $lines.Add("$Key = $Value")
        $lines.Add('')
    } else {
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i].TrimStart().StartsWith('[')) { $end = $i; break }
        }
        $found = $false
        $pattern = '^\s*' + [regex]::Escape($Key) + '\s*='
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($lines[$i] -match $pattern) { $lines[$i] = "$Key = $Value"; $found = $true; break }
        }
        if (-not $found) { $lines.Insert($start + 1, "$Key = $Value") }
    }
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllLines($Path, $lines.ToArray(), (New-Object System.Text.UTF8Encoding($false)))
}
