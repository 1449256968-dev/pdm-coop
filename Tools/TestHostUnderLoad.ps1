param([int]$Players = 4, [int]$Warmup = 10, [int]$Duration = 60, [switch]$Compat, [string]$Tag = 'load')
# Realistic host-load measurement: every player window is focused with a real click and then
# driven with held movement keys plus attack clicks for the whole shared measurement window, so
# the host is measured while characters actually move and effects spawn (the earlier static
# four-player runs had no gameplay load at all).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Performance-20261014-Continue'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag "before-$Tag" | Out-Null
if (Get-NetUDPEndpoint -LocalPort 7786 -ErrorAction SilentlyContinue) { throw 'UDP 7786 is still bound.' }

$compatArgs = @(); if ($Compat) { $compatArgs = @('-coopCompatInput30', '-coopCompatMotion30', '-coopCompatFullApply') }
$probe = @('-coopPerf', '-coopPerfPlayers', "$Players", '-coopPerfWarmup', "$Warmup", '-coopPerfDuration', "$Duration")
$window = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $compatArgs + $probe + $window + @('-logFile', "$logs\$Tag-host.log")
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $hostArgs | Out-Null
Start-Sleep -Seconds 25
for ($i = 1; $i -lt $Players; $i++) {
    $clientArgs = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', "Client$i") + $compatArgs + $probe + $window + @('-logFile', "$logs\$Tag-client$i.log")
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $clientArgs | Out-Null
    Start-Sleep -Seconds 3
}

Add-Type -Namespace Win -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

# Wait until the shared window is open (or its start is imminent) before driving input.
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Select-String -LiteralPath "$logs\$Tag-host.log" -Pattern 'measurement window open' -Quiet -ErrorAction SilentlyContinue) { break }
}
Write-Output "window open; driving $Players player windows for ~$Duration s"

$windows = @(Get-Process -Name 'Pandemonium*' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Sort-Object StartTime)
$stopAt = (Get-Date).AddSeconds($Duration + 4)
while ((Get-Date) -lt $stopAt) {
    foreach ($target in $windows) {
        if ((Get-Date) -ge $stopAt) { break }
        $handle = $target.MainWindowHandle
        if ($handle -eq 0) { continue }
        [Win.Api]::ShowWindow($handle, 9) | Out-Null
        $rect = New-Object Win.Api+RECT
        [Win.Api]::GetWindowRect($handle, [ref]$rect) | Out-Null
        $cx = [int](($rect.Left + $rect.Right) / 2); $cy = [int](($rect.Top + $rect.Bottom) / 2)
        [Win.Api]::SetCursorPos($cx, $cy) | Out-Null
        Start-Sleep -Milliseconds 250
        [Win.Api]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        [Win.Api]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 300
        [Win.Api]::SetForegroundWindow($handle) | Out-Null
        [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)   # W held
        for ($tick = 0; $tick -lt 8; $tick++) {
            Start-Sleep -Milliseconds 900
            [Win.Api]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 60
            [Win.Api]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        }
        [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
    }
}
# Let every process finish its window and write the report.
$reportDeadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $reportDeadline) {
    Start-Sleep -Seconds 5
    if (Select-String -LiteralPath "$logs\$Tag-host.log" -Pattern '\[CoopPerf\] frames' -Quiet -ErrorAction SilentlyContinue) { break }
}
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag $Tag | Out-Null

foreach ($role in @('host', 'client1', 'client2', 'client3')) {
    $path = Join-Path $logs "$Tag-$role.log"
    if (-not (Test-Path -LiteralPath $path)) { continue }
    Write-Output "== $role =="
    Select-String -LiteralPath $path -Pattern '\[CoopPerf\] (frames|context)' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line }
}
Write-Output '== host stages =='
Select-String -LiteralPath "$logs\$Tag-host.log" -Pattern '\[CoopPerf\] (capture |captureEntities|captureObjects|captureRooms|settingsCompare|encodeWorker|sendSnapshot|motionPublish)' -ErrorAction SilentlyContinue | ForEach-Object { $_.Line }
Write-Output 'done'
