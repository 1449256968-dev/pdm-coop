param([Parameter(Mandatory=$true)][string[]]$Tags, [string]$OutFile = '')
# Extracts the [CoopPerf]/[CoopProfiler] report lines from a set of tags into Markdown so the
# performance report is generated from the logs instead of transcribed by hand.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$logs = Join-Path $root 'Logs\Performance-20261014-Continue'
$lines = New-Object System.Collections.Generic.List[string]

function Field([string]$text, [string]$name) {
    if (-not $text) { return '' }
    $m = [regex]::Match($text, [regex]::Escape($name) + '=([^;]+)')
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ''
}

foreach ($tag in $Tags) {
    $files = Get-ChildItem -LiteralPath $logs -Filter "*-$tag.log" -ErrorAction SilentlyContinue | Sort-Object Name
    if (-not $files) { $lines.Add("### $tag"); $lines.Add(''); $lines.Add('no logs'); $lines.Add(''); continue }
    $lines.Add("### $tag")
    $lines.Add('')
    $lines.Add('| log | role | entities/replicas | fpsTotal | meanMs | p95Ms | p99Ms | maxMs | >50ms | gc0 | bufferMs | rttMs |')
    $lines.Add('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
    foreach ($file in $files) {
        $context = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] context' | Select-Object -Last 1).Line
        $frames = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] frames' | Select-Object -Last 1).Line
        if (-not $frames) { $lines.Add("| $($file.BaseName) | - | - | - | - | - | - | - | - | - | - | - |"); continue }
        $role = Field $context 'role'
        $count = (Field $context 'entities') + '/' + (Field $context 'replicas')
        $lines.Add("| $($file.BaseName) | $role | $count | $(Field $frames 'fpsTotal') | $(Field $frames 'meanMs') | $(Field $frames 'p95Ms') | $(Field $frames 'p99Ms') | $(Field $frames 'maxMs') | $(Field $frames 'over50ms') | $(Field $frames 'gc0') | $(Field $context 'bufferTimeMs') | $(Field $context 'rttMs') |")
    }
    $lines.Add('')
    # Movement metrics are per process and only meaningful for clients (the host publishes).
    $lines.Add('| log | heldFrames | poses | chunks | interpDelay meanMs | transportAge meanMs | publishInterval meanMs | inputAge meanMs | ownerStep meanMs |')
    $lines.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|')
    foreach ($file in $files) {
        $motion = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] motion ' | Select-Object -Last 1).Line
        $interp = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] interpDelay' | Select-Object -Last 1).Line
        $transport = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] transportAge' | Select-Object -Last 1).Line
        $publish = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] publishInterval' | Select-Object -Last 1).Line
        $inputAge = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] inputAge' | Select-Object -Last 1).Line
        $ownerStep = (Select-String -LiteralPath $file.FullName -Pattern '\[CoopPerf\] ownerStep' | Select-Object -Last 1).Line
        $lines.Add("| $($file.BaseName) | $(Field $motion 'heldFrames') | $(Field $motion 'poses') | $(Field $motion 'chunks') | $(Field $interp 'meanMs') | $(Field $transport 'meanMs') | $(Field $publish 'meanMs') | $(Field $inputAge 'meanMs') | $(Field $ownerStep 'meanMs') |")
    }
    $lines.Add('')
    $lines.Add('| stage | n | meanMs | p95Ms | p99Ms | maxMs |')
    $lines.Add('|---|---:|---:|---:|---:|---:|')
    foreach ($file in $files) {
        foreach ($hit in (Select-String -LiteralPath $file.FullName -Pattern '^\[CoopPerf\] [a-zA-Z]+ n=')) {
            $text = $hit.Line
            $name = [regex]::Match($text, '^\[CoopPerf\] ([a-zA-Z]+) ').Groups[1].Value
            $lines.Add("| $($file.BaseName)/$name | $(Field $text 'n') | $(Field $text 'meanMs') | $(Field $text 'p95Ms') | $(Field $text 'p99Ms') | $(Field $text 'maxMs') |")
        }
    }
    $lines.Add('')
}
if ($OutFile) { $lines -join "`r`n" | Set-Content -LiteralPath $OutFile -Encoding UTF8; Write-Output "written: $OutFile" }
else { $lines -join "`r`n" }
