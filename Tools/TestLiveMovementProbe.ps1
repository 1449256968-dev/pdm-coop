param([int]$Warmup = 8, [int]$Duration = 25)
# Drives REAL keyboard input into the client window while the performance probe measures the
# client's own rendered movement (ownerStep) and interpolation health (heldFrames/interpDelay).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Live-Input-Test'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'pre-liveperf' | Out-Null
$common = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
$perf = @('-coopPerf', '-coopPerfPlayers', '2', '-coopPerfWarmup', "$Warmup", '-coopPerfDuration', "$Duration")
$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $perf + $common + @('-logFile', "$logs\perf-host.log")
$clientArgs = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', 'ClientB') + $perf + $common + @('-logFile', "$logs\perf-client.log")
$hostProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $hostArgs -PassThru
Start-Sleep -Seconds 25
$clientProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $clientArgs -PassThru

Add-Type -Namespace Win -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
'@
# The client window is the newest visible player window.
$deadline = (Get-Date).AddSeconds(200)
$clientWindow = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $clientWindow = Get-Process -Name 'Pandemonium*' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Sort-Object StartTime -Descending | Select-Object -First 1
    if ($clientWindow -ne $null -and (Select-String -LiteralPath "$logs\perf-client.log" -Pattern 'ready; detail' -Quiet -ErrorAction SilentlyContinue)) { break }
}
Start-Sleep -Seconds 4
if ($clientWindow -ne $null) {
    $handle = $clientWindow.MainWindowHandle
    [Win.Api]::ShowWindow($handle, 9) | Out-Null
    [Win.Api]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Seconds 1
    Write-Output "driving W into pid=$($clientWindow.Id)"
    [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds ($Warmup + $Duration + 6)
    [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
}
Start-Sleep -Seconds 6
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'liveperf' | Out-Null
Write-Output '--- client probe ---'
Select-String -LiteralPath "$logs\perf-client.log" -Pattern '\[CoopPerf\] (context|frames|ownerStep|interpDelay|transportAge|motion )' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line }
Write-Output '--- host remote movement ---'
Select-String -LiteralPath "$logs\perf-host.log" -Pattern 'Remote player .* moved to|Input stream active' -ErrorAction SilentlyContinue | Select-Object -Last 8 | ForEach-Object { $_.Line }
Write-Output 'done'
