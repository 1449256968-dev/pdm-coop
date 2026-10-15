param([Parameter(Mandatory=$true)][string]$Tag, [int]$Count = 4, [int]$TimeoutSec = 600)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
$taskLogs = Join-Path $taskRoot 'Logs\Performance-20261014-Continue'
$roles = @('host') + (1..([Math]::Max(0, $Count - 1)) | ForEach-Object { "client$_" })
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ($true) {
    $pending = @()
    foreach ($role in $roles) {
        $path = Join-Path $taskLogs "$role-$Tag.log"
        if (!(Test-Path -LiteralPath $path)) { $pending += $role; continue }
        if (-not (Select-String -LiteralPath $path -Pattern '\[CoopPerf\] context' -Quiet -ErrorAction SilentlyContinue)) { $pending += $role }
    }
    if ($pending.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) {
        Write-Output "TIMEOUT waiting for: $($pending -join ', ')"
        break
    }
    Start-Sleep -Seconds 5
}
foreach ($role in $roles) {
    $path = Join-Path $taskLogs "$role-$Tag.log"
    if (!(Test-Path -LiteralPath $path)) { Write-Output "== $role : no log =="; continue }
    $context = Select-String -LiteralPath $path -Pattern '\[CoopPerf\] context' | Select-Object -Last 1
    $frames = Select-String -LiteralPath $path -Pattern '\[CoopPerf\] frames' | Select-Object -Last 1
    $motion = Select-String -LiteralPath $path -Pattern '\[CoopPerf\] motion ' | Select-Object -Last 1
    $transport = Select-String -LiteralPath $path -Pattern '\[CoopPerf\] transportAge' | Select-Object -Last 1
    $interp = Select-String -LiteralPath $path -Pattern '\[CoopPerf\] interpDelay' | Select-Object -Last 1
    $publish = Select-String -LiteralPath $path -Pattern '\[CoopPerf\] publishInterval' | Select-Object -Last 1
    Write-Output "== $role =="
    if ($context) { Write-Output $context.Line }
    if ($frames) { Write-Output $frames.Line }
    if ($motion) { Write-Output $motion.Line }
    if ($transport) { Write-Output $transport.Line }
    if ($interp) { Write-Output $interp.Line }
    if ($publish) { Write-Output $publish.Line }
}
