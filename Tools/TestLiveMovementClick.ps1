param([int]$HoldSeconds = 8, [int]$Warmup = 6, [int]$Duration = 20)
# Same as the probe test, but focuses the client window with a real synthesized CLICK first
# (Windows only gives the foreground/keyboard to a window the user or a click activated),
# then holds W and reports the new [CoopInput]/[CoopInputSim] trace plus rendered movement.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Live-Input-Test'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'pre-click' | Out-Null
$common = @('-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '600', '-coopInputTrace')
$perf = @('-coopPerf', '-coopPerfPlayers', '2', '-coopPerfWarmup', "$Warmup", '-coopPerfDuration', "$Duration")
$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $perf + $common + @('-logFile', "$logs\click-host.log")
$clientArgs = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', 'ClientB') + $perf + $common + @('-logFile', "$logs\click-client.log")
$hostProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $hostArgs -PassThru
Start-Sleep -Seconds 25
$clientProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $clientArgs -PassThru

Add-Type -Namespace Win -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

$deadline = (Get-Date).AddSeconds(220)
$clientWindow = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $clientWindow = Get-Process -Name 'Pandemonium*' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Sort-Object StartTime -Descending | Select-Object -First 1
    if ($clientWindow -ne $null -and (Select-String -LiteralPath "$logs\click-client.log" -Pattern 'ready; detail' -Quiet -ErrorAction SilentlyContinue)) { break }
}
if ($clientWindow -ne $null) {
    $handle = $clientWindow.MainWindowHandle
    [Win.Api]::ShowWindow($handle, 9) | Out-Null
    $rect = New-Object Win.Api+RECT
    [Win.Api]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $cx = [int](($rect.Left + $rect.Right) / 2); $cy = [int](($rect.Top + $rect.Bottom) / 2)
    Write-Output "client pid=$($clientWindow.Id) rect=$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom) centre=$cx,$cy"
    [Win.Api]::SetCursorPos($cx, $cy) | Out-Null
    Start-Sleep -Milliseconds 400
    # A real click gives the window the keyboard.
    [Win.Api]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win.Api]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    [Win.Api]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 500
    Write-Output "holding W for $HoldSeconds s"
    [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds $HoldSeconds
    [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
}
Start-Sleep -Seconds 4
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'click' | Out-Null
Write-Output '--- client captured input (watch rawW / forward) ---'
Select-String -LiteralPath "$logs\click-client.log" -Pattern '\[CoopInput\]' -ErrorAction SilentlyContinue | Select-Object -Last 8 | ForEach-Object { $_.Line }
Write-Output '--- client rendered movement ---'
Select-String -LiteralPath "$logs\click-client.log" -Pattern '\[CoopPerf\] (frames|ownerStep|motion )' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line }
Write-Output '--- host consumed / moved ---'
Select-String -LiteralPath "$logs\click-host.log" -Pattern '\[CoopInputSim\]|Remote player .* moved to|\[CoopPerf\] frames' -ErrorAction SilentlyContinue | Select-Object -Last 8 | ForEach-Object { $_.Line }
Write-Output 'done'
