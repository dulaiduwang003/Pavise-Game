param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$summaryRoot = (Resolve-Path -LiteralPath $RunDirectory).Path
function Assert-Review([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Median-Review($Values) {
    $sorted = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    if ($sorted.Count % 2) { return $sorted[[int][math]::Floor($sorted.Count / 2)] }
    return ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2
}
$summaryPriority = @(Import-Csv -LiteralPath (Join-Path $summaryRoot 'priority.csv'))
Assert-Review ($summaryPriority.Count -eq 16) 'Expected 16 priority arms'
Assert-Review (@($summaryPriority | Group-Object layout,arm | Where-Object Count -ne 1).Count -eq 0) 'Duplicate arm key'
foreach ($row in $summaryPriority) {
    Assert-Review ($row.layout -in @('same-logical','separate-logical') -and $row.treatment -in @('normal','high','guarded') -and [int]$row.arm -ge 0 -and [int]$row.arm -lt 8) 'Unexpected arm labels'
    if ($row.treatment -eq 'guarded') {
        Assert-Review ($row.game_priority -eq 'Normal' -and [int]$row.completed -gt 0) 'CPU-domain guard failed'
    }
    Assert-Review ($row.restored -eq 'True' -and [int]$row.completed -ge 0) 'Invalid completion/restoration'
    Assert-Review ([double]$row.system_cpu_percent -ge 0 -and [double]$row.system_cpu_percent -le 100) 'Invalid CPU utilization'
    $rawFile = Join-Path $summaryRoot ($row.layout + '-' + $row.arm + '-' + $row.treatment + '.samples.txt')
    $raw = @(Get-Content -LiteralPath $rawFile | ForEach-Object { [double]$_ } | Sort-Object)
    Assert-Review ($raw.Count -eq [int]$row.completed) 'Raw completed-sample count mismatch'
    if ($raw.Count -eq 0) {
        Assert-Review ([double]::IsNaN([double]$row.p50_reply_ms) -and [double]::IsNaN([double]$row.p95_reply_ms)) 'Empty sample percentiles must be unavailable'
    } else {
        foreach ($pair in @(@(0.5,'p50_reply_ms'),@(0.95,'p95_reply_ms'))) {
            $calculated = $raw[[math]::Max(0,[int][math]::Ceiling($raw.Count * $pair[0]) - 1)]
            Assert-Review ([math]::Abs($calculated - [double]$row.($pair[1])) -le 0.00011) 'Raw percentile mismatch'
        }
    }
}
$summaryPower = @(Import-Csv -LiteralPath (Join-Path $summaryRoot 'power-inputs.csv'))
$summaryGpu = @(Import-Csv -LiteralPath (Join-Path $summaryRoot 'auto-gpu-filter.csv'))
$summaryCost = @(Import-Csv -LiteralPath (Join-Path $summaryRoot 'read-cost.csv'))
$summaryFixed = @($summaryPriority | Where-Object treatment -eq 'guarded').Count -gt 0
Assert-Review ($summaryPower.Count -eq $(if ($summaryFixed) {20} else {15}) -and $summaryGpu.Count -eq 10 -and $summaryCost.Count -eq $(if ($summaryFixed) {40} else {32})) 'Incomplete decision/cost data'
if ($summaryFixed) {
    foreach ($row in $summaryPower) {
        Assert-Review ($row.observe_stage -eq 'Engaged') 'Valid observation baseline was lost'
        Assert-Review $(if ($row.case -like 'verify-*') { $row.verify_stage -eq 'Reverted' -and $row.verdict -eq 'Inconclusive' } else { $row.verify_stage -eq 'Held' -and $row.verdict -eq 'Kept' }) 'Telemetry verdict incorrect'
    }
    Assert-Review (@($summaryGpu | Where-Object mock_low_power_written -eq 'True').Count -eq 0) 'Visible application preference was written'
}
$summaryVerification = Get-Content -LiteralPath (Join-Path $summaryRoot 'verification.json') -Raw | ConvertFrom-Json
Assert-Review ($summaryVerification.SettingsUnchanged -and $summaryVerification.ActivePlanUnchanged -and $summaryVerification.SourcesTestsProductionUnchanged -and $summaryVerification.RemainingBenchProcessIds.Count -eq 0) 'Run isolation did not verify'
[ordered]@{
    QA='Complete expected row counts; unique priority keys; raw counts and nearest-rank percentiles recomputed; no fabricated empty percentiles; isolation checked'
    Priority=@($summaryPriority | Group-Object layout,treatment | ForEach-Object {
        [ordered]@{Group=$_.Name;Arms=$_.Count;MedianCompleted=(Median-Review @($_.Group.completed));MinCompleted=($_.Group | Measure-Object completed -Minimum).Minimum;MaxCompleted=($_.Group | Measure-Object completed -Maximum).Maximum;ZeroCompleted=@($_.Group | Where-Object completed -eq '0').Count}
    })
    Power=@($summaryPower | Group-Object case | ForEach-Object { [ordered]@{Case=$_.Name;Rows=$_.Count;Skipped=@($_.Group | Where-Object observe_stage -eq 'Skipped').Count;Kept=@($_.Group | Where-Object verdict -eq 'Kept').Count} })
    AutoGpu=@($summaryGpu | Group-Object case | ForEach-Object { [ordered]@{Case=$_.Name;Rows=$_.Count;MockLowPowerWrites=@($_.Group | Where-Object mock_low_power_written -eq 'True').Count} })
    Cost=@($summaryCost | Group-Object kind | ForEach-Object {
        $warm = @($_.Group | Where-Object sample -ne '0')
        [ordered]@{Kind=$_.Name;Samples=$_.Count;FirstMs=[double]$_.Group[0].elapsed_ms;WarmMedianMs=(Median-Review @($warm.elapsed_ms));WarmMaxMs=($warm | Measure-Object elapsed_ms -Maximum).Maximum}
    })
    Caveat='Priority is a two-second synthetic busy-wait dependency, not game frametime. Power and GPU preference gates use synthetic inputs; CPU cold first reading is real. Costs are worker wall time, not FPS.'
} | ConvertTo-Json -Depth 6
