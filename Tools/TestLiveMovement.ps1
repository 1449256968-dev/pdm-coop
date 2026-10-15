param([int]$WaitSeconds = 150, [int]$HoldSeconds = 4)
# Reproduces "a non-host player cannot move" with REAL keyboard input: two visible players,
# then W is held on the focused client window while the host log tells whether the remote
# avatar actually moved. Scripted fixtures inject input, so they cannot cover this path.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Live-Input-Test'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'pre-live' | Out-Null

$common = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $common + @('-logFile', "$logs\host.log")
$clientArgs = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', 'ClientB') + $common + @('-logFile', "$logs\client.log")
$hostProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $hostArgs -PassThru
Start-Sleep -Seconds 25
$clientProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $clientArgs -PassThru

$deadline = (Get-Date).AddSeconds($WaitSeconds)
$registered = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Select-String -LiteralPath "$logs\host.log" -Pattern 'character registered; remote=True' -Quiet -ErrorAction SilentlyContinue) { $registered = $true; break }
}
Write-Output "client avatar registered: $registered"
Start-Sleep -Seconds 12

Add-Type -Namespace Win -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.UIntPtr dwExtraInfo);
'@

$clientProcess = $null
foreach ($candidate in (Get-Process -Name 'Pandemonium*' -ErrorAction SilentlyContinue | Sort-Object StartTime)) {
    $handle = $candidate.MainWindowHandle
    if ($handle -ne 0) { $clientProcess = $candidate }
}
if ($clientProcess -eq $null) { Write-Output 'no visible player window found'; }
else {
    $handle = $clientProcess.MainWindowHandle
    Write-Output "focusing pid=$($clientProcess.Id) handle=$handle"
    [Win.Api]::ShowWindow($handle, 9) | Out-Null
    [Win.Api]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Seconds 2
    # 0x57 = W, 0x11 = its scan code
    [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds $HoldSeconds
    [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    # left click for a primary attack, then a short second move burst
    [Win.Api]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win.Api]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 2
    [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
}
Start-Sleep -Seconds 3
Write-Output '--- host: remote movement lines ---'
Select-String -LiteralPath "$logs\host.log" -Pattern 'Remote player .* moved to|Input stream active|character registered' -ErrorAction SilentlyContinue | Select-Object -Last 15 | ForEach-Object { $_.Line }
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'live-input-test' | Out-Null
Write-Output 'done'
