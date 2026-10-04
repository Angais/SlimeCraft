<#
.SYNOPSIS
  Installs BepInEx 5.4.23.5 (x64) into the Slime Rancher folder from the official release zip.
.DESCRIPTION
  BepInEx is not bundled with SlimeCraft. Download BepInEx_win_x64_5.4.23.5.zip from the official release page
  (https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) and either pass it with -Zip, put it in the project's
  lib\ folder, or leave it in your Downloads folder. -Download fetches it from that release page for you.
  Lists every file the zip will add (or overwrite) and asks for confirmation unless -Yes is passed.
  Existing BepInEx\config and BepInEx\plugins content is never touched (the zip does not contain them).
.EXAMPLE
  .\scripts\install-bepinex.ps1 -Zip "$env:USERPROFILE\Downloads\BepInEx_win_x64_5.4.23.5.zip"
  .\scripts\install-bepinex.ps1 -Download
  .\scripts\install-bepinex.ps1 -Yes
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Zip,
    # Download the zip from the official BepInEx GitHub release when it is not found locally.
    [switch]$Download,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$zipName = 'BepInEx_win_x64_5.4.23.5.zip'
$releasePage = 'https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5'
$downloadUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/$zipName"

if (-not $Zip) {
    $candidates = @(
        (Join-Path (Get-ProjectRoot) "lib\$zipName"),
        (Join-Path $env:USERPROFILE "Downloads\$zipName")
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { $Zip = $c; break } }
}
if (-not $Zip -and $Download) {
    $libDir = Join-Path (Get-ProjectRoot) 'lib'
    if (-not (Test-Path -LiteralPath $libDir)) { New-Item -ItemType Directory -Force -Path $libDir | Out-Null }
    $Zip = Join-Path $libDir $zipName
    Write-Step "Downloading $downloadUrl"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $downloadUrl -OutFile $Zip -UseBasicParsing
    } catch {
        if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
        Write-Fail "Download failed: $($_.Exception.Message)"
        Write-Host "Download $zipName by hand from $releasePage and pass -Zip <path>." -ForegroundColor White
        exit 1
    }
}
if (-not $Zip -or -not (Test-Path -LiteralPath $Zip)) {
    Write-Fail "BepInEx zip not found$(if ($Zip) { ": $Zip" } else { '' })"
    Write-Host "Download $zipName from $releasePage and pass -Zip <path>" -ForegroundColor White
    Write-Host "(or put it in lib\ or your Downloads folder, or run this script with -Download)." -ForegroundColor White
    exit 1
}

$sr = Find-SlimeRancherDir -GameDir $GameDir
if (-not $sr) { exit 1 }
if (Test-GameRunning) { Write-Fail "Slime Rancher is running - close it first."; exit 1 }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$srFull = [System.IO.Path]::GetFullPath($sr).TrimEnd('\') + '\'
$archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
try {
    $entries = @($archive.Entries | Where-Object { $_.Name -ne '' })   # skip directory entries
    $plan = @()
    foreach ($e in $entries) {
        $rel = $e.FullName -replace '/', '\'
        $target = [System.IO.Path]::GetFullPath((Join-Path $sr $rel))
        if (-not $target.StartsWith($srFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Fail "Refusing unsafe zip entry: $($e.FullName)"
            exit 1
        }
        $plan += [pscustomobject]@{ Entry = $e; Rel = $rel; Target = $target; Exists = (Test-Path -LiteralPath $target) }
    }

    Write-Step "BepInEx $([System.IO.Path]::GetFileName($Zip)) will be extracted into:"
    Write-Host "    $sr" -ForegroundColor White
    $top = $plan | Group-Object { ($_.Rel -split '\\')[0] }
    foreach ($g in $top) {
        $new = @($g.Group | Where-Object { -not $_.Exists }).Count
        $over = @($g.Group | Where-Object { $_.Exists }).Count
        $what = if ($g.Count -eq 1 -and $g.Group[0].Rel -eq $g.Name) { 'file' } else { "$($g.Count) files" }
        $state = @()
        if ($new -gt 0) { $state += "$new new" }
        if ($over -gt 0) { $state += "$over overwritten" }
        Write-Host ("    + {0,-22} {1,-10} ({2})" -f $g.Name, $what, ($state -join ', ')) -ForegroundColor White
    }
    foreach ($p in $plan) { Write-Verbose ("      {0}{1}" -f $p.Rel, $(if ($p.Exists) { '  (overwrite)' } else { '' })) }
    if (Test-BepInEx $sr) { Write-Warn "BepInEx is already installed here; its core files will be replaced (config/plugins are kept)." }
    Write-Host "    winhttp.dll is the Doorstop loader that starts BepInEx with the game; uninstall with .\scripts\uninstall.ps1 -RemoveBepInEx" -ForegroundColor DarkGray

    if (-not $Yes) {
        $answer = Read-Host "Proceed? [y/N]"
        if ($answer -notmatch '^\s*(y|yes|s|si)\s*$') { Write-Warn "Cancelled - nothing was changed."; exit 1 }
    }

    foreach ($p in $plan) {
        $dir = Split-Path -Parent $p.Target
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($p.Entry, $p.Target, $true)
    }
} finally {
    $archive.Dispose()
}

if (Test-BepInEx $sr) {
    Write-Ok "BepInEx installed ($($plan.Count) files)."
    Write-Host "Next: .\scripts\install.ps1 -Build   (BepInEx creates BepInEx\config\BepInEx.cfg on the first game start)" -ForegroundColor White
} else {
    Write-Fail "Extraction finished but winhttp.dll / BepInEx\core\BepInEx.dll are missing."
    exit 1
}
