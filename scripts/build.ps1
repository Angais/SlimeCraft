<#
.SYNOPSIS
  Builds SlimeCraft.dll (dotnet build, Release by default) and prints the path of the dll.
.EXAMPLE
  .\scripts\build.ps1
  .\scripts\build.ps1 -Configuration Debug -Modules "Core;Hud"
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    # Slime Rancher folder (its Managed\*.dll are the compile references). Auto-detected when omitted.
    [string]$GameDir,
    # Optional partial build, e.g. "Core;Hud" (Contracts + Plugin.cs are always compiled).
    [string]$Modules
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root = Get-ProjectRoot
$proj = Join-Path $root 'SlimeCraft\SlimeCraft.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail "The .NET SDK ('dotnet') was not found. Install it from https://dotnet.microsoft.com/download (any SDK 6+ works)."
    exit 1
}

$sr = Find-SlimeRancherDir -GameDir $GameDir -Quiet
if (-not $sr) {
    Write-Fail "Slime Rancher not found: the build references its SlimeRancher_Data\Managed\*.dll. Pass -GameDir."
    exit 1
}

$buildArgs = @('build', $proj, '-c', $Configuration, '-nologo', "-p:SRDir=$sr")
# ';' separates properties on the msbuild command line, so the module list is passed escaped (%3B).
if ($Modules) { $buildArgs += ("-p:SCModules=" + ($Modules -replace '[;,]', '%3B')) }

Write-Step "dotnet build ($Configuration) against $sr"
& dotnet @buildArgs | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Build failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$dll = Get-DefaultDllPath $Configuration
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Fail "Build reported success but $dll is missing."
    exit 1
}
$info = Get-Item -LiteralPath $dll
Write-Ok ("Built {0} ({1:N0} KB, {2})" -f $dll, ($info.Length / 1KB), $info.LastWriteTime)
# The dll path is the script's only pipeline output (install.ps1 / run-autotest.ps1 capture it).
Write-Output $dll
