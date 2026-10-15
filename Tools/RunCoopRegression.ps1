param([string]$Prefix = 'ds-reg', [switch]$Quick)
# Runs the regression suites from the handover one after another, waits for each suite's
# completion marker, then scans every log of that suite for FAIL/Exception classes of error.
# This script starts and stops the players itself; it never decides "pass" from a sleep alone.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$logs = Join-Path $root 'Logs\Performance-20261014-Continue'
$start = Join-Path $root 'Tools\StartPerformanceRun.ps1'
$stop = Join-Path $root 'Tools\StopCoopTestPlayers.ps1'

$suites = @(
    @{ Name = 'graph';        Players = 2; Timeout = 260; Switches = @('-Graph', '-Appearance', '-Recipe', '-View', '-Burst', '-Validate'); Marker = '\[Coop(Graph|Appearance|Recipe|View|InputBurst)Test\] PASS' },
    @{ Name = 'identity';     Players = 3; Timeout = 330; Switches = @('-Identity', '-View', '-Validate');                                    Marker = '\[CoopIdentity\] PASS' },
    @{ Name = 'motion';       Players = 2; Timeout = 260; Switches = @('-MotionHands', '-View', '-Validate');                                 Marker = '\[CoopMotionHands\] HOST DONE' },
    @{ Name = 'interactions'; Players = 2; Timeout = 360; Switches = @('-Interactions', '-Validate');                                         Marker = '\[CoopInteraction\] COMPLETE' },
    @{ Name = 'lifecycle';    Players = 2; Timeout = 420; Switches = @('-Scenario', '-Render', '-Lifecycle', '-Validate', '-Graph');         Marker = '\[CoopLifecycle\]' }
)
if ($Quick) { $suites = $suites[0..2] }

$summary = @()
foreach ($suite in $suites) {
    $tag = "$Prefix-$($suite.Name)"
    & $stop -Tag "before-$tag" | Out-Null
    Write-Output "=== $tag : starting ($($suite.Players) players) ==="
    # Hashtable splatting binds switches by name; an array splat would pass them positionally.
    $named = @{}
    foreach ($switchName in $suite.Switches) { $named[$switchName.TrimStart('-')] = $true }
    & $start -Tag $tag -PlayerCount $suite.Players @named | Out-Null
    $deadline = (Get-Date).AddSeconds($suite.Timeout)
    $seen = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        foreach ($file in Get-ChildItem -LiteralPath $logs -Filter "*-$tag.log" -ErrorAction SilentlyContinue) {
            if (Select-String -LiteralPath $file.FullName -Pattern $suite.Marker -Quiet -ErrorAction SilentlyContinue) { $seen = $true; break }
        }
        if ($seen) { break }
    }
    Start-Sleep -Seconds 20   # let every process reach its own final report
    & $stop -Tag $tag | Out-Null
    $files = Get-ChildItem -LiteralPath $logs -Filter "*-$tag.log" -ErrorAction SilentlyContinue
    $failures = @()
    $markers = @()
    foreach ($file in $files) {
        foreach ($pattern in @('Exception', 'FAIL', 'mismatch', 'State publish failed', 'State encode failed', 'failed to unpack', 'larger than the max allowed')) {
            $hits = Select-String -LiteralPath $file.FullName -Pattern $pattern -ErrorAction SilentlyContinue
            foreach ($hit in $hits) { $failures += "$($file.Name): $($hit.Line.Trim())" }
        }
        foreach ($mark in (Select-String -LiteralPath $file.FullName -Pattern $suite.Marker -ErrorAction SilentlyContinue)) {
            $markers += "$($file.Name): $($mark.Line.Trim())"
        }
    }
    $summary += [pscustomobject]@{
        Suite = $suite.Name; Tag = $tag; Completed = $seen; Logs = $files.Count
        Markers = $markers.Count; Failures = $failures.Count
    }
    Write-Output "--- $tag markers=$($markers.Count) failures=$($failures.Count) completed=$seen ---"
    foreach ($line in $markers) { Write-Output "  OK  $line" }
    foreach ($line in $failures) { Write-Output "  BAD $line" }
}
Write-Output ''
Write-Output '=== regression summary ==='
$summary | Format-Table -AutoSize | Out-String | Write-Output
