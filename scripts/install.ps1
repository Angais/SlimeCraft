<#
.SYNOPSIS
  Installs SlimeCraft.dll into <Slime Rancher>\BepInEx\plugins\SlimeCraft\.
.DESCRIPTION
  Checks that BepInEx 5 is installed in the game folder (winhttp.dll + BepInEx\core\BepInEx.dll) and prints
  instructions if it is not. Builds the plugin first when -Build is given or no build exists yet.
.EXAMPLE
  .\scripts\install.ps1 -Build
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    # Rebuild before installing.
    [switch]$Build,
    # Install this dll instead of the default build output.
    [string]$Dll
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$sr = Find-SlimeRancherDir -GameDir $GameDir
if (-not $sr) { exit 1 }
Write-Step "Slime Rancher: $sr"

if (-not (Test-BepInEx $sr)) {
    Show-BepInExHelp $sr
    exit 1
}
Write-Ok "BepInEx found"

if (-not $Dll) {
    $Dll = Get-DefaultDllPath $Configuration
    if ($Build -or -not (Test-Path -LiteralPath $Dll)) {
        $built = & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -GameDir $sr
        if ($LASTEXITCODE -ne 0 -or -not $built) { Write-Fail "Build failed, nothing installed."; exit 1 }
        $Dll = @($built)[-1]
    }
}
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Fail "Plugin dll not found: $Dll  (run .\scripts\build.ps1 first)"
    exit 1
}

if (Test-GameRunning) {
    Write-Fail "Slime Rancher is running - close it first (the dll is locked while the game runs)."
    exit 1
}

$dest = Join-Path $sr 'BepInEx\plugins\SlimeCraft'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -LiteralPath $Dll -Destination (Join-Path $dest 'SlimeCraft.dll') -Force
Write-Ok "Installed $Dll"
Write-Ok "        -> $(Join-Path $dest 'SlimeCraft.dll')"

$mc = Test-MinecraftInstall
if ($mc) {
    Write-Ok "Minecraft Java install found: $mc (textures/sounds are read from it at runtime)"
} else {
    Write-Warn "No Minecraft Java install found in %APPDATA%\.minecraft. Install Minecraft Java Edition with the official"
    Write-Warn "launcher and start any release version once - SlimeCraft loads its textures and sounds from there."
}

Write-Host ""
Write-Host "Start Slime Rancher normally (Steam). Settings: $(Join-Path $sr 'BepInEx\config\com.angais.slimecraft.cfg') (created on first start)." -ForegroundColor White
Write-Host "Log: $(Join-Path $sr 'BepInEx\LogOutput.log')" -ForegroundColor White
