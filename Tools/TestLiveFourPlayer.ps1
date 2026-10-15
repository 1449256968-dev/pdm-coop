param([int]$Players = 4, [int]$WaitSeconds = 180, [int]$HoldSeconds = 5, [switch]$Validate)
# Live 4-player session with real keyboard input driven into one client window and optional
# -coopValidate state verification. Reports the host's remote-movement lines and any
# validation/exception lines from every process.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe'
$logs = Join-Path $root 'Logs\Live-Input-Test'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'pre-live4' | Out-Null

$common = @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
$validateArgs = @(); if ($Validate) { $validateArgs = @('-coopValidate') }
$hostArgs = @('-coopHost', '7786', '-coopStartGame', '-coopUsername', 'HostA') + $validateArgs + $common + @('-logFile', "$logs\host.log")
$hostProc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $hostArgs -PassThru
Start-Sleep -Seconds 25
$clientProcs = @()
for ($i = 1; $i -lt $Players; $i++) {
    $args = @('-coopJoin', '127.0.0.1:7786', '-coopUsername', "Client$i") + $validateArgs + $common + @('-logFile', "$logs\client$i.log")
    $clientProcs += Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $args -PassThru
    Start-Sleep -Seconds 3
}

$deadline = (Get-Date).AddSeconds($WaitSeconds)
$registered = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $registered = (Select-String -LiteralPath "$logs\host.log" -Pattern 'character registered; remote=True' -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($registered -ge ($Players - 1)) { break }
}
Write-Output "remote characters registered: $registered / $($Players - 1)"
Start-Sleep -Seconds 15

Add-Type -Namespace Win -Name Api -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.UIntPtr dwExtraInfo);
'@

$windows = @(Get-Process -Name 'Pandemonium*' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Sort-Object StartTime)
Write-Output "visible windows: $($windows.Count)"
foreach ($target in $windows) {
    $handle = $target.MainWindowHandle
    [Win.Api]::ShowWindow($handle, 9) | Out-Null
    [Win.Api]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Seconds 1
    [Win.Api]::keybd_event(0x57, 0x11, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds $HoldSeconds
    [Win.Api]::keybd_event(0x57, 0x11, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    [Win.Api]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [Win.Api]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    Write-Output "drove pid=$($target.Id)"
}
Start-Sleep -Seconds 4
Write-Output '--- host remote movement ---'
Select-String -LiteralPath "$logs\host.log" -Pattern 'Input stream active|Remote player .* moved to' -ErrorAction SilentlyContinue | Select-Object -Last 14 | ForEach-Object { $_.Line }
Write-Output '--- validation / errors across processes ---'
foreach ($file in (Get-ChildItem $logs -Filter '*.log')) {
    $hits = Select-String -LiteralPath $file.FullName -Pattern 'verification failed|InvalidOperation|Exception|FAIL|mismatch|State publish failed|State encode failed' -ErrorAction SilentlyContinue
    Write-Output "$($file.Name): $($hits.Count) hits"
    $hits | Select-Object -First 5 | ForEach-Object { "   $($_.Line)" }
}
& (Join-Path $PSScriptRoot 'StopCoopTestPlayers.ps1') -Tag 'live4' | Out-Null
Write-Output 'done'
