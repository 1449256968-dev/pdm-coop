param([int]$Warmup = 10, [int]$Duration = 30)
# Decisive measurement: focus the client window with a real click, hold W across the whole
# measurement window, then wait for the probe report and print the client's OWN rendered
# movement (ownerStep) plus the interpolation health.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Live-Input-Test'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'pre-decisive' | Out-Null
$common = @('-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '600', '-coopInputTrace')
$probe = @('-coopPerf', '-coopPerfPlayers', '2', '-coopPerfWarmup', "$Warmup", '-coopPerfDuration', "$Duration")
$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $probe + $common + @('-logFile', "$logs\dec-host.log")
$clientArgs = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', 'ClientB') + $probe + $common + @('-logFile', "$logs\dec-client.log")
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

$deadline = (Get-Date).AddSeconds(240)
$clientWindow = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $clientWindow = Get-Process -Name 'Pandemonium*' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Sort-Object StartTime -Descending | Select-Object -First 1
    if ($clientWindow -ne $null -and (Select-String -LiteralPath "$logs\dec-client.log" -Pattern 'measurement window open' -Quiet -ErrorAction SilentlyContinue)) { break }
}
if ($clientWindow -ne $null) {
    $handle = $clientWindow.MainWindowHandle
    [Win.Api]::ShowWindow($handle, 9) | Out-Null
    $rect = New-Object Win.Api+RECT
    [Win.Api]::GetWindowRect($handle, [ref]$rect) | Out-Null
    [Win.Api]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2)) | Out-Null
    Start-Sleep -Milliseconds 400
    [Win.Api]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win.Api]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    [Win.Api]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 500
    Write-Output "holding W for the whole window (pid=$($clientWindow.Id))"
    [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds ($Duration + 12)
    [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
}
# Wait for the probe report rather than guessing.
$reportDeadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $reportDeadline) {
    Start-Sleep -Seconds 5
    if (Select-String -LiteralPath "$logs\dec-client.log" -Pattern '\[CoopPerf\] frames' -Quiet -ErrorAction SilentlyContinue) { break }
}
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'decisive' | Out-Null
Write-Output '--- client captured input (samples during the hold) ---'
Select-String -LiteralPath "$logs\dec-client.log" -Pattern '\[CoopInput\]' -ErrorAction SilentlyContinue | Where-Object { $_.Line -match 'forward=1' } | Select-Object -First 2 | ForEach-Object { $_.Line }
Write-Output '--- client own rendered movement / interpolation ---'
Select-String -LiteralPath "$logs\dec-client.log" -Pattern '\[CoopPerf\] (frames|ownerStep|motion |interpDelay|transportAge|context)' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line }
Write-Output '--- host: consumed input + avatar movement ---'
Select-String -LiteralPath "$logs\dec-host.log" -Pattern '\[CoopInputSim\].*forward=1|\[CoopMove\]|Remote player .* moved to|\[CoopPerf\] frames' -ErrorAction SilentlyContinue | Select-Object -Last 12 | ForEach-Object { $_.Line }
Write-Output 'done'
