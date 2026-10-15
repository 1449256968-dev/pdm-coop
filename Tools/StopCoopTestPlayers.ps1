param([string]$Tag)
# Stops only this build's test players. The launcher PID file can be stale because
# Unity relaunches itself, so the executable path plus build directory is the check.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
$taskExe = Join-Path $taskRoot 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$taskBuildDir = Split-Path $taskExe
$stopped = @()
foreach ($taskProc in Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'Pandemonium*' }) {
    $taskPath = $taskProc.ExecutablePath
    if ($taskPath -and $taskPath.StartsWith($taskBuildDir, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $taskProc.ProcessId -Force -ErrorAction SilentlyContinue
        $stopped += $taskProc.ProcessId
    }
    else { Write-Output "SKIPPED (not this build): $($taskProc.ProcessId) $taskPath" }
}
Write-Output "stopped: $($stopped -join ',') (tag=$Tag)"
Start-Sleep -Seconds 2
$remaining = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'Pandemonium*' })
Write-Output "remaining test players: $($remaining.Count)"
