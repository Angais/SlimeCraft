<#
.SYNOPSIS
  Removes SlimeCraft from Slime Rancher (and optionally BepInEx itself).
.DESCRIPTION
  Default: deletes BepInEx\plugins\SlimeCraft.
  -RemoveData     also deletes SlimeCraft's own data: BepInEx\config\SlimeCraft (caches, per-save Minecraft data,
                  test sandbox), BepInEx\config\com.angais.slimecraft.cfg and BepInEx\SlimeCraft_test.
  -RemoveBepInEx  also removes BepInEx: winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt and the
                  whole BepInEx\ folder (including any other plugins!). Asks first unless -Yes.
  Your Slime Rancher saves (%USERPROFILE%\AppData\LocalLow\Monomi Park\Slime Rancher) are never touched.
.EXAMPLE
  .\scripts\uninstall.ps1
  .\scripts\uninstall.ps1 -RemoveBepInEx
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$RemoveData,
    [switch]$RemoveBepInEx,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$sr = Find-SlimeRancherDir -GameDir $GameDir
if (-not $sr) { exit 1 }
if (Test-GameRunning) { Write-Fail "Slime Rancher is running - close it first."; exit 1 }
Write-Step "Slime Rancher: $sr"

function Remove-IfPresent([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
        Write-Ok "removed $Path"
    }
}

$plugin = Join-Path $sr 'BepInEx\plugins\SlimeCraft'
if (Test-Path -LiteralPath $plugin) { Remove-IfPresent $plugin }
else { Write-Warn "SlimeCraft plugin folder not found ($plugin)" }

if ($RemoveData -and -not $RemoveBepInEx) {
    Remove-IfPresent (Join-Path $sr 'BepInEx\config\SlimeCraft')
    Remove-IfPresent (Join-Path $sr 'BepInEx\config\com.angais.slimecraft.cfg')
    Remove-IfPresent (Join-Path $sr 'BepInEx\SlimeCraft_test')
}

if ($RemoveBepInEx) {
    $pluginsDir = Join-Path $sr 'BepInEx\plugins'
    $others = @()
    if (Test-Path -LiteralPath $pluginsDir) { $others = @(Get-ChildItem -LiteralPath $pluginsDir -Force | Select-Object -ExpandProperty Name) }
    Write-Warn "This removes BepInEx completely from $sr :"
    Write-Host "    winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt, BepInEx\ (config, logs, cache, plugins)" -ForegroundColor White
    if ($others.Count -gt 0) { Write-Warn ("Other BepInEx plugins that will be deleted too: " + ($others -join ', ')) }
    if (-not $Yes) {
        $answer = Read-Host "Remove BepInEx? [y/N]"
        if ($answer -notmatch '^\s*(y|yes|s|si)\s*$') { Write-Warn "BepInEx kept."; exit 0 }
    }
    foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) { Remove-IfPresent (Join-Path $sr $f) }
    Remove-IfPresent (Join-Path $sr 'BepInEx')
    Write-Ok "BepInEx removed - Slime Rancher is vanilla again."
} else {
    Write-Ok "SlimeCraft removed (BepInEx kept; use -RemoveBepInEx to remove it as well)."
}
