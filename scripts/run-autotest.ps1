<#
.SYNOPSIS
  Runs the SlimeCraft automated in-game test and verifies that your real Slime Rancher saves were not touched.
.DESCRIPTION
  1. backs up %USERPROFILE%\AppData\LocalLow\Monomi Park\Slime Rancher to _backup\<timestamp>
  2. sets [Testing] AutoTest = true in BepInEx\config\com.angais.slimecraft.cfg
  3. creates steam_appid.txt (433340) if missing so SlimeRancher.exe can start directly while Steam runs
  4. launches SlimeRancher.exe windowed (-screen-fullscreen 0 -screen-width 1600 -screen-height 900)
  5. waits for BepInEx\SlimeCraft_test\report.json (or the timeout)
  6. restores AutoTest = false (+ removes the steam_appid.txt it created, restores Unity's window prefs)
  7. verifies the save folder is byte-identical to the backup and prints the report
  While AutoTest is on the plugin redirects SR's save folder (saves, profile, settings) to
  BepInEx\config\SlimeCraft\test_saves, so the test game never lands in your save folder.
.EXAMPLE
  .\scripts\run-autotest.ps1
  .\scripts\run-autotest.ps1 -Install -Scenario smoke
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [int]$TimeoutMinutes = 10,
    # full | smoke | boot | comma separated step ids/names (see SlimeCraft\src\Testing\README.md)
    [string]$Scenario = 'full',
    [int]$Width = 1600,
    [int]$Height = 900,
    # Build + install the plugin before testing.
    [switch]$Install,
    # Leave the game running when the run is finished (Testing.QuitWhenDone = false).
    [switch]$NoQuit,
    # Keep a steam_appid.txt this script created.
    [switch]$KeepSteamAppId,
    # Do not restore Unity's Screenmanager*/UnityGraphicsQuality prefs (HKCU\Software\Monomi Park\Slime Rancher) after the run.
    [switch]$SkipRegistryRestore
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root = Get-ProjectRoot
$sr = Find-SlimeRancherDir -GameDir $GameDir
if (-not $sr) { exit 1 }
Write-Step "Slime Rancher: $sr"
if (-not (Test-BepInEx $sr)) { Show-BepInExHelp $sr; exit 1 }
if (Test-GameRunning) { Write-Fail "Slime Rancher is already running - close it first."; exit 1 }

if ($Install) {
    & (Join-Path $PSScriptRoot 'install.ps1') -GameDir $sr -Build
    if ($LASTEXITCODE -ne 0) { Write-Fail "Install failed."; exit 1 }
}
$pluginDll = Join-Path $sr 'BepInEx\plugins\SlimeCraft\SlimeCraft.dll'
if (-not (Test-Path -LiteralPath $pluginDll)) {
    Write-Fail "SlimeCraft is not installed ($pluginDll). Run .\scripts\install.ps1 -Build (or pass -Install)."
    exit 1
}
if (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue)) {
    Write-Warn "Steam does not seem to be running - start Steam first or Slime Rancher may fail to initialise."
}

# ------------------------------------------------------------------ 0. Steam account guard
# Steam AutoCloud MOVES the save folder's *.sav / slimerancher.prf into the previous account's userdata folder when
# the game is started by a different Steam account than the one recorded in steam_autocloud.vdf (the files then
# seem to vanish until that account signs in again). Never trigger that from a test run.
$saveDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Monomi Park\Slime Rancher'
$vdf = Join-Path $saveDir 'steam_autocloud.vdf'
if (Test-Path -LiteralPath $vdf) {
    $owner = $null
    $m = [regex]::Match((Get-Content -LiteralPath $vdf -Raw), '"accountid"\s+"(\d+)"')
    if ($m.Success) { $owner = $m.Groups[1].Value }
    $active = $null
    try { $active = [string](Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction Stop).ActiveUser } catch { }
    if ($owner -and $active -and $active -ne '0' -and $owner -ne $active) {
        Write-Fail "The Slime Rancher save folder belongs to Steam account $owner, but Steam is signed in as $active."
        Write-Fail "Launching now would make Steam AutoCloud move those saves away. Sign in to the right account and retry."
        exit 2
    }
}

# ------------------------------------------------------------------ 1. backup the real save folder
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupRoot = Join-Path $root "_backup\$stamp"
$backupSaves = Join-Path $backupRoot 'Slime Rancher'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
if (Test-Path -LiteralPath $saveDir) {
    Copy-Item -LiteralPath $saveDir -Destination $backupSaves -Recurse -Force
    $n = @(Get-ChildItem -LiteralPath $backupSaves -Recurse -File -Force).Count
    Write-Ok "Backed up $n files of $saveDir"
    Write-Ok "        -> $backupSaves"
} else {
    New-Item -ItemType Directory -Force -Path $backupSaves | Out-Null
    Write-Warn "No Slime Rancher save folder yet ($saveDir) - nothing to back up."
}

# Files Unity itself rewrites on every launch (logs/analytics cache) - not save data, excluded from the comparison.
$ignored = @('Player.log', 'Player-prev.log', 'output_log.txt', 'Unity\*')

function Get-Manifest([string]$Dir) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $Dir)) { return $map }
    $base = (Resolve-Path -LiteralPath $Dir).ProviderPath.TrimEnd('\')
    foreach ($f in @(Get-ChildItem -LiteralPath $Dir -Recurse -File -Force)) {
        $rel = $f.FullName.Substring($base.Length + 1)
        $skip = $false
        foreach ($p in $ignored) { if ($rel -like $p) { $skip = $true } }
        if ($skip) { continue }
        $map[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

# ------------------------------------------------------------------ 2. registry: Unity's window prefs
$regSubKey = 'Software\Monomi Park\Slime Rancher'
function Get-ScreenPrefs {
    $h = @{}
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($regSubKey)
    if ($null -eq $k) { return $h }
    try {
        foreach ($name in $k.GetValueNames()) {
            # Unity writes these PlayerPrefs on quit (window size/mode from the -screen-* args, quality level)
            if ($name -like 'Screenmanager*' -or $name -like 'UnityGraphicsQuality*') { $h[$name] = @{ Kind = $k.GetValueKind($name); Value = $k.GetValue($name) } }
        }
    } finally { $k.Close() }
    return $h
}
function Format-RegValue($v) { if ($v -is [byte[]]) { return ($v -join ',') } return [string]$v }
$screenPrefsBefore = Get-ScreenPrefs

# ------------------------------------------------------------------ 3. config: enable the test
$cfg = Join-Path $sr 'BepInEx\config\com.angais.slimecraft.cfg'
if (Test-Path -LiteralPath $cfg) { Copy-Item -LiteralPath $cfg -Destination (Join-Path $backupRoot 'com.angais.slimecraft.cfg.before') -Force }
Set-BepInExCfgValue -Path $cfg -Section 'Testing' -Key 'AutoTest' -Value 'true'
Set-BepInExCfgValue -Path $cfg -Section 'Testing' -Key 'Scenario' -Value $Scenario
Set-BepInExCfgValue -Path $cfg -Section 'Testing' -Key 'QuitWhenDone' -Value $(if ($NoQuit) { 'false' } else { 'true' })
Set-BepInExCfgValue -Path $cfg -Section 'Testing' -Key 'Width' -Value ([string]$Width)
Set-BepInExCfgValue -Path $cfg -Section 'Testing' -Key 'Height' -Value ([string]$Height)
Write-Ok "Testing.AutoTest = true (scenario '$Scenario') in $cfg"

# ------------------------------------------------------------------ 4. steam_appid.txt
$appIdFile = Join-Path $sr 'steam_appid.txt'
$createdAppId = $false
if (-not (Test-Path -LiteralPath $appIdFile)) {
    [System.IO.File]::WriteAllText($appIdFile, '433340')
    $createdAppId = $true
    Write-Ok "Created steam_appid.txt (433340)"
}

# ------------------------------------------------------------------ 5. launch + wait
$outDir = Join-Path $sr 'BepInEx\SlimeCraft_test'
$report = Join-Path $outDir 'report.json'
$reportTxt = Join-Path $outDir 'report.txt'
$progress = Join-Path $outDir 'progress.txt'
$launchTime = Get-Date
$exe = Join-Path $sr 'SlimeRancher.exe'
$gameArgs = "-screen-fullscreen 0 -screen-width $Width -screen-height $Height"
Write-Step "Launching $exe $gameArgs"
$proc = Start-Process -FilePath $exe -ArgumentList $gameArgs -WorkingDirectory $sr -PassThru

$deadline = $launchTime.AddMinutes($TimeoutMinutes)
$reportReady = $false
$lastProgress = ''
$exitedEarly = $false
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ((Test-Path -LiteralPath $progress) -and ((Get-Item -LiteralPath $progress).LastWriteTime -ge $launchTime)) {
            try {
                $p = ([System.IO.File]::ReadAllText($progress)).Trim()
                if ($p -and $p -ne $lastProgress) { Write-Host "    test: $p" -ForegroundColor DarkCyan; $lastProgress = $p }
            } catch { }
        }
        if ((Test-Path -LiteralPath $report) -and ((Get-Item -LiteralPath $report).LastWriteTime -ge $launchTime)) { $reportReady = $true; break }
        if ($proc.HasExited) {
            Start-Sleep -Seconds 3
            if ((Test-Path -LiteralPath $report) -and ((Get-Item -LiteralPath $report).LastWriteTime -ge $launchTime)) { $reportReady = $true }
            else { $exitedEarly = $true }
            break
        }
    }

    if ($reportReady -and -not $NoQuit) {
        Write-Step "Report written - waiting for the game to quit"
        if (-not $proc.WaitForExit(120000)) {
            Write-Warn "Game still running after 2 min - asking it to close"
            $null = $proc.CloseMainWindow()
            if (-not $proc.WaitForExit(30000)) { Write-Warn "Stopping SlimeRancher.exe (sandboxed test game, your saves are not involved)"; Stop-Process -Id $proc.Id -Force }
        }
    } elseif (-not $reportReady) {
        if ($exitedEarly) { Write-Fail "Slime Rancher exited (code $($proc.ExitCode)) without writing a report." }
        else {
            Write-Fail "No report after $TimeoutMinutes min (last progress: '$lastProgress')."
            if (-not $proc.HasExited) {
                $null = $proc.CloseMainWindow()
                if (-not $proc.WaitForExit(30000)) { Write-Warn "Stopping SlimeRancher.exe (sandboxed test game)"; Stop-Process -Id $proc.Id -Force }
            }
        }
    }
} finally {
    # ------------------------------------------------------------------ 6. restore
    try {
        if (-not $proc.HasExited -and -not $NoQuit) { $null = $proc.WaitForExit(15000) }
    } catch { }
    Set-BepInExCfgValue -Path $cfg -Section 'Testing' -Key 'AutoTest' -Value 'false'
    Write-Ok "Testing.AutoTest = false restored"
    if ($createdAppId -and -not $KeepSteamAppId) {
        if (Test-Path -LiteralPath $appIdFile) { Remove-Item -LiteralPath $appIdFile -Force; Write-Ok "Removed the steam_appid.txt created for the test" }
    }
    if (-not $SkipRegistryRestore -and ($NoQuit -eq $false)) {
        $after = Get-ScreenPrefs
        $changed = @()
        foreach ($name in $after.Keys) {
            if (-not $screenPrefsBefore.ContainsKey($name) -or (Format-RegValue $screenPrefsBefore[$name].Value) -ne (Format-RegValue $after[$name].Value)) { $changed += $name }
        }
        foreach ($name in $screenPrefsBefore.Keys) { if (-not $after.ContainsKey($name)) { $changed += $name } }
        if ($changed.Count -gt 0) {
            $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($regSubKey, $true)
            if ($null -ne $k) {
                try {
                    foreach ($name in ($changed | Select-Object -Unique)) {
                        if ($screenPrefsBefore.ContainsKey($name)) { $k.SetValue($name, $screenPrefsBefore[$name].Value, $screenPrefsBefore[$name].Kind) }
                        else { $k.DeleteValue($name, $false) }
                    }
                    Write-Ok "Restored Unity's window/quality prefs ($($changed.Count) registry values) changed by the windowed test launch"
                } finally { $k.Close() }
            }
        }
    }
}

# ------------------------------------------------------------------ 7. verify the real save folder
Write-Step "Verifying $saveDir against the backup"
$before = Get-Manifest $backupSaves
$now = Get-Manifest $saveDir
$diffs = @()
foreach ($rel in $before.Keys) {
    if (-not $now.ContainsKey($rel)) { $diffs += "REMOVED  $rel" }
    elseif ($now[$rel] -ne $before[$rel]) { $diffs += "CHANGED  $rel" }
}
foreach ($rel in $now.Keys) { if (-not $before.ContainsKey($rel)) { $diffs += "ADDED    $rel" } }
$savesOk = $diffs.Count -eq 0
if ($savesOk) {
    Write-Ok "Save folder is byte-identical to the backup ($($before.Count) files; Unity logs ignored)"
} else {
    Write-Fail "Save folder differs from the backup:"
    foreach ($d in ($diffs | Sort-Object)) { Write-Host "    $d" -ForegroundColor Red }
    Write-Warn "Your untouched copy is in $backupSaves"
}

# ------------------------------------------------------------------ 8. report
$exitCode = 1
if ($reportReady -and (Test-Path -LiteralPath $reportTxt)) {
    Write-Host ""
    Get-Content -LiteralPath $reportTxt | Out-Host
    Write-Host ""
    try {
        $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $s = $json.summary
        $fmt = "Outcome: {0} - {1} passed, {2} failed, {3} timeout, {4} error, {5} skipped; log: {6} errors, {7} exceptions; avg {8} fps"
        $values = @($json.outcome, $s.passed, $s.failed, $s.timeouts, $s.errors, $s.skipped, $s.logErrors, $s.logExceptions, [math]::Round([double]$s.avgFps, 1))
        Write-Step ($fmt -f $values)
        if ($json.outcome -eq 'passed' -and $savesOk) { $exitCode = 0 }
    } catch {
        Write-Warn "Could not parse report.json: $($_.Exception.Message)"
    }
    Write-Host "Screenshots + report: $outDir" -ForegroundColor White
} else {
    Write-Fail "No test report. Check $(Join-Path $sr 'BepInEx\LogOutput.log')"
}
Write-Host "Backup: $backupRoot" -ForegroundColor White
exit $exitCode
