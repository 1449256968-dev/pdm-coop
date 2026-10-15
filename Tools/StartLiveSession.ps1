param([int]$Players = 4, [string]$Tag = 'live', [switch]$Hidden, [switch]$Compat)
# Starts a normal playable session with the opt-in input trace enabled and one log per process,
# so a movement report can be diagnosed from logs. Leaves the players running: play, then tell
# the assistant (or stop them with Tools\StopCoopTestPlayers.ps1 -Tag $Tag).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Live-Input-Test'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag "before-$Tag" | Out-Null
if (Get-NetUDPEndpoint -LocalPort 7786 -ErrorAction SilentlyContinue) { throw 'UDP 7786 is still bound; stop the previous session first.' }

$trace = @('-coopInputTrace')
# -Compat restores the pre-round behaviour (30Hz input, 30Hz poses, always re-apply payloads),
# which is the fastest way to prove whether this optimisation round caused a symptom.
if ($Compat) { $trace += @('-coopCompatInput30', '-coopCompatMotion30', '-coopCompatFullApply') }
# Also run the measurement probe: the same session then reports each process's rendered
# movement (ownerStep), interpolation health and FPS with no extra action from the tester.
$probe = @('-coopPerf', '-coopPerfPlayers', "$Players", '-coopPerfWarmup', '10', '-coopPerfDuration', '60')
$window = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
if ($Hidden) { $window = @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '640') }

$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $trace + $probe + $window + @('-logFile', "$logs\$Tag-host.log")
$startArgs = @{ FilePath = $exe; WorkingDirectory = (Split-Path $exe); ArgumentList = $hostArgs; PassThru = $true }
if ($Hidden) { $startArgs['WindowStyle'] = 'Hidden' }
$processes = @((Start-Process @startArgs).Id)
Start-Sleep -Seconds 25
for ($i = 1; $i -lt $Players; $i++) {
    $clientArgs = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', "Client$i") + $trace + $probe + $window + @('-logFile', "$logs\$Tag-client$i.log")
    $startArgs = @{ FilePath = $exe; WorkingDirectory = (Split-Path $exe); ArgumentList = $clientArgs; PassThru = $true }
    if ($Hidden) { $startArgs['WindowStyle'] = 'Hidden' }
    $processes += (Start-Process @startArgs).Id
    Start-Sleep -Seconds 3
}

$deadline = (Get-Date).AddSeconds(240)
$registered = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $registered = (Select-String -LiteralPath "$logs\$Tag-host.log" -Pattern 'character registered; remote=True' -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($registered -ge ($Players - 1)) { break }
}
Write-Output "remote characters registered: $registered / $($Players - 1)"
Write-Output ''
Write-Output 'The session is running. Please:'
Write-Output '  1) focus your own (non-host) window, press and hold W/A/S/D for a few seconds;'
Write-Output '  2) attack something and watch the effect;'
Write-Output '  3) tell the assistant, then stop with:'
Write-Output "     & '$PSScriptRoot\StopCoopTestPlayers.ps1' -Tag '$Tag'"
Write-Output ''
Write-Output 'logs:'
Get-ChildItem $logs -Filter "$Tag-*.log" | ForEach-Object { Write-Output "  $($_.FullName)" }
