param([Parameter(Mandatory=$true)][string]$Tag, [switch]$Raw, [switch]$Assimilation, [switch]$Validate, [switch]$Recipe, [switch]$Graph, [switch]$Appearance, [switch]$Scenario, [switch]$Render, [switch]$Lifecycle, [switch]$View, [switch]$Burst, [switch]$MonsterControl, [switch]$MonsterRoster, [int]$RosterSkip = 0, [int]$RosterCount = 0, [string]$RosterOnly = '', [switch]$Identity, [switch]$Interactions, [switch]$MotionHands, [int]$PlayerCount = 0, [int]$Warmup = 0, [int]$Duration = 0, [switch]$SkipState)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
$taskLogs = Join-Path $taskRoot 'Logs\Performance-20261014-Continue'
$taskExe = Join-Path $taskRoot 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
# A leftover player keeps UDP 7786 bound, which silently turns the whole run into
# a "host failed to listen" failure. Refuse to start until the port is free.
$taskUdp = Get-NetUDPEndpoint -LocalPort 7786 -ErrorAction SilentlyContinue
if ($taskUdp) { throw "UDP 7786 is still bound by PID $($taskUdp.OwningProcess); stop the previous run first (Tools\StopCoopTestPlayers.ps1)." }
$taskPlayers = @()
$taskCount = if ($PlayerCount -gt 0) { $PlayerCount } elseif ($Assimilation -or $MonsterRoster -or $Identity) { 3 } else { 2 }
for ($i=0; $i -lt $taskCount; $i++) {
    $role = if ($i -eq 0) { 'host' } else { "client$i" }
    $taskArgs = if ($i -eq 0) { @('-coopHost','7786','-coopStartGame') } else { @('-coopJoin','127.0.0.1:7786') }
    if ($Assimilation -and !$MonsterRoster) { $taskArgs += @('-coopValidate','-coopAssimilationValidate') }
    elseif ($MonsterRoster) { $taskArgs += '-coopValidate' }
    else { $taskArgs += '-coopPerf' }
    if ($Raw) { $taskArgs += '-coopProfilerRaw' }
    # Only an explicit -PlayerCount switches the probe to the roster-ready barrier and
    # the shared wall-clock window; the older single-number modes keep their timing.
    if ($PlayerCount -gt 0) { $taskArgs += @('-coopPerfPlayers', "$taskCount") }
    if ($Warmup -gt 0) { $taskArgs += @('-coopPerfWarmup', "$Warmup") }
    if ($Duration -gt 0) { $taskArgs += @('-coopPerfDuration', "$Duration") }
    # Diagnostic only: freezes world-state replication inside the measurement window so the
    # run quantifies how much of each frame the snapshot path actually costs.
    if ($SkipState) { $taskArgs += '-coopPerfSkipState' }
    if ($Validate) { $taskArgs += '-coopValidate' }
    if ($Recipe) { $taskArgs += '-coopRecipeValidate' }
    if ($Graph) { $taskArgs += '-coopGraphValidate' }
    if ($Appearance) { $taskArgs += '-coopAppearanceValidate' }
    if ($Scenario) { $taskArgs += '-coopScenario' }
    if ($Render) { $taskArgs += '-coopRenderValidate' }
    if ($Lifecycle) { $taskArgs += '-coopLifecycleValidate' }
    if ($View) { $taskArgs += '-coopViewValidate' }
    if ($Burst) { $taskArgs += '-coopInputBurstValidate' }
    if ($MonsterControl) { $taskArgs += '-coopMonsterControlValidate' }
    # Percent-escaped UTF-8: the player decodes its command line through the system ANSI code
    # page, so raw non-ASCII text handed over by PowerShell would arrive as mojibake.
    if ($Identity) { $taskArgs += @('-coopIdentityValidate','-coopUsername',@('%E6%88%BF%E4%B8%BBAlpha','%E7%8E%A9%E5%AE%B6Beta','%E7%8E%A9%E5%AE%B6Gamma')[$i]) }
    if ($MotionHands) { $taskArgs += '-coopMotionHandsValidate' }
    if ($Interactions) { $taskArgs += '-coopInteractionValidate' }
    if ($MonsterRoster) { $taskArgs += @('-coopMonsterControlValidate','-coopMonsterRoster','-coopMonsterRosterSkip',"$RosterSkip",'-coopMonsterRosterCount',"$RosterCount") }
    if ($RosterOnly) { $taskArgs += @('-coopMonsterRosterOnly',"$RosterOnly") }
    $taskArgs += @('-screen-fullscreen','0','-screen-width','960','-screen-height','640','-logFile',"$taskLogs/$role-$Tag.log")
    # Keep automated test windows in the background; the players still use graphics.
    $taskPlayers += Start-Process -FilePath $taskExe -WorkingDirectory (Split-Path $taskExe) -ArgumentList $taskArgs -WindowStyle Hidden -PassThru
}
$taskPlayers.Id | ConvertTo-Json | Set-Content "$taskLogs\$Tag-pids.json"
Copy-Item "$taskRoot\RecoveredUnityProject\ExportedProject\CoopBuild.result.json" "$taskLogs\build-$Tag.json"
$taskPlayers | Select-Object Id,StartTime

