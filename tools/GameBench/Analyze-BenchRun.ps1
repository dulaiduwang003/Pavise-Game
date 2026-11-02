param([Parameter(Mandatory = $true)][string]$RunDir, [switch]$Quiet)
# Frame statistics of one bench run plus Pavise's own CPU cost during the measured window, and whether Pavise
# was busy during the stutter frames. Writes summary.json next to the run.
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
$benchDir = Join-Path $RunDir 'bench'
$manifest = @{}
if (Test-Path (Join-Path $benchDir 'manifest.tsv')) {
    foreach ($line in Get-Content (Join-Path $benchDir 'manifest.tsv')) { $p = $line.Split("`t", 2); if ($p.Count -eq 2) { $manifest[$p[0]] = $p[1] } }
}
$freq = if ($manifest['qpc_frequency']) { [double]$manifest['qpc_frequency'] } else { [double][Diagnostics.Stopwatch]::Frequency }
$frames = New-Object System.Collections.Generic.List[object]
$csv = Join-Path $benchDir 'frames.csv'
if (Test-Path $csv) {
    $first = $true
    foreach ($line in [IO.File]::ReadLines($csv)) {
        if ($first) { $first = $false; continue }
        $c = $line.Split(',')
        if ($c.Count -lt 8 -or $c[0] -eq '0' -or $c[7] -ne 'foreground') { continue }
        $frames.Add([pscustomobject]@{ qpc = [double]::Parse($c[1], $inv); ms = [double]::Parse($c[2], $inv);
            cpu = $(if ($c[3]) { [double]::Parse($c[3], $inv) } else { [double]::NaN }); gpu = $(if ($c[4]) { [double]::Parse($c[4], $inv) } else { [double]::NaN }) })
    }
}
function Pct([double[]]$sorted, [double]$q) { if ($sorted.Count -eq 0) { return [double]::NaN }; $i = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($q * $sorted.Count) - 1)); return $sorted[$i] }
$ms = [double[]]($frames | ForEach-Object { $_.ms })
$sorted = [double[]]($ms | Sort-Object)
$sum = ($ms | Measure-Object -Sum).Sum
$stats = [ordered]@{}
if ($ms.Count -gt 0) {
    $median = Pct $sorted 0.5
    $worst1 = $sorted[[int]([Math]::Floor($sorted.Count * 0.99))..($sorted.Count - 1)]
    $worst01 = $sorted[[int]([Math]::Floor($sorted.Count * 0.999))..($sorted.Count - 1)]
    $stutterLimit = [Math]::Max($median * 2.0, $median + 4.0)
    $stutters = @($frames | Where-Object { $_.ms -gt $stutterLimit })
    $minutes = $sum / 60000.0
    $stats.frames = $ms.Count
    $stats.avg_fps = [Math]::Round($ms.Count * 1000.0 / $sum, 1)
    $stats.low1_fps = [Math]::Round(1000.0 / (($worst1 | Measure-Object -Average).Average), 1)
    $stats.low01_fps = [Math]::Round(1000.0 / (($worst01 | Measure-Object -Average).Average), 1)
    $stats.median_ms = [Math]::Round($median, 3)
    $stats.p95_ms = [Math]::Round((Pct $sorted 0.95), 3)
    $stats.p99_ms = [Math]::Round((Pct $sorted 0.99), 3)
    $stats.p999_ms = [Math]::Round((Pct $sorted 0.999), 3)
    $stats.max_ms = [Math]::Round($sorted[$sorted.Count - 1], 3)
    $stats.stutter_limit_ms = [Math]::Round($stutterLimit, 3)
    $stats.stutters = $stutters.Count
    $stats.stutters_per_min = [Math]::Round($stutters.Count / [Math]::Max($minutes, 0.001), 2)
    $stats.hitches_25ms = @($frames | Where-Object { $_.ms -gt 25 }).Count
    $stats.hitches_50ms = @($frames | Where-Object { $_.ms -gt 50 }).Count
    $cpuSorted = [double[]]($frames | Where-Object { -not [double]::IsNaN($_.cpu) } | ForEach-Object { $_.cpu } | Sort-Object)
    $gpuSorted = [double[]]($frames | Where-Object { -not [double]::IsNaN($_.gpu) } | ForEach-Object { $_.gpu } | Sort-Object)
    $stats.cpu_p50_ms = [Math]::Round((Pct $cpuSorted 0.5), 3); $stats.cpu_p99_ms = [Math]::Round((Pct $cpuSorted 0.99), 3)
    $stats.gpu_p50_ms = [Math]::Round((Pct $gpuSorted 0.5), 3); $stats.gpu_p99_ms = [Math]::Round((Pct $gpuSorted 0.99), 3)
}

# Pavise's own CPU from the probe, within the measured bench window.
$probeCsv = Join-Path $RunDir 'probe-process.csv'
$pav = [ordered]@{}
$startQpc = if ($manifest['measurement_start_qpc']) { [double]$manifest['measurement_start_qpc'] } else { 0 }
$endQpc = if ($manifest['measurement_end_qpc']) { [double]$manifest['measurement_end_qpc'] } else { [double]::MaxValue }
$samples = New-Object System.Collections.Generic.List[object]
# Precise CPU comes from cycle counters (the 100 ns times move in 15.625 ms ticks); cycles_per_ms is calibrated by the probe.
$cyclesPerMs = 0.0
$metaFile = Join-Path $RunDir 'probe-meta.tsv'
if (Test-Path $metaFile) { foreach ($line in Get-Content $metaFile) { $kv = $line.Split("`t"); if ($kv[0] -eq 'cycles_per_ms') { $cyclesPerMs = [double]::Parse($kv[1], $inv) } } }
if (Test-Path $probeCsv) {
    $first = $true
    foreach ($line in [IO.File]::ReadLines($probeCsv)) {
        if ($first) { $first = $false; continue }
        $c = $line.Split(',')
        if ($c[1] -ne 'Pavise' -and $c[1] -ne 'Pavise.HardThreadGuardian') { continue }
        $q = [double]::Parse($c[0], $inv)
        $tickMs = [double]::Parse($c[3], $inv) / 10000.0
        $cpu = if ($cyclesPerMs -gt 0 -and $c.Count -gt 9) { [double]::Parse($c[9], $inv) / $cyclesPerMs } else { $tickMs }
        $samples.Add([pscustomobject]@{ qpc = $q; name = $c[1]; cpu = $cpu; threads = [int]$c[4]; handles = [int]$c[5]; ws = [double]::Parse($c[6], $inv); sw = [double]::Parse($c[7], $inv) })
    }
}
# Per-thread precise cost and wakeups of Pavise's own threads.
$threadTable = @()
$threadSwitches = 0.0
$threadCsv = Join-Path $RunDir 'probe-threads.csv'
if ($cyclesPerMs -gt 0 -and (Test-Path $threadCsv)) {
    $acc = @{}
    $first = $true
    foreach ($line in [IO.File]::ReadLines($threadCsv)) {
        if ($first) { $first = $false; continue }
        $c = $line.Split(',')
        if ($c[1] -ne 'Pavise' -or $c.Count -lt 8) { continue }
        $q = [double]::Parse($c[0], $inv)
        if ($q -lt $startQpc -or $q -gt $endQpc) { continue }
        $tid = $c[3]
        if (-not $acc.ContainsKey($tid)) { $acc[$tid] = [pscustomobject]@{ tid = $tid; ms = 0.0; switches = 0.0; priority = $c[6]; peak_ms = 0.0 } }
        $ms = [double]::Parse($c[7], $inv) / $cyclesPerMs
        $sw = [double]::Parse($c[5], $inv); $threadSwitches += $sw
        $acc[$tid].ms += $ms; $acc[$tid].switches += $sw; $acc[$tid].priority = $c[6]
        if ($ms -gt $acc[$tid].peak_ms) { $acc[$tid].peak_ms = $ms }
    }
    $seconds = [Math]::Max(0.001, ($endQpc - $startQpc) / $freq)
    $threadTable = @($acc.Values | Sort-Object ms -Descending | Select-Object -First 12 | ForEach-Object {
        [ordered]@{ tid = $_.tid; priority = $_.priority; cpu_ms = [Math]::Round($_.ms, 1); peak_ms_in_sample = [Math]::Round($_.peak_ms, 2); wakeups_per_s = [Math]::Round($_.switches / $seconds, 1) } })
}
$inWindow = @($samples | Where-Object { $_.qpc -ge $startQpc -and $_.qpc -le $endQpc })
$main = @($inWindow | Where-Object { $_.name -eq 'Pavise' })
if ($main.Count -gt 0) {
    $windowSeconds = ($endQpc - $startQpc) / $freq
    $cpuMs = ($main | Measure-Object cpu -Sum).Sum
    $pav.cpu_ms = [Math]::Round($cpuMs, 1)
    $pav.cpu_percent_of_one_core = [Math]::Round($cpuMs / ($windowSeconds * 10.0), 3)
    # The process-level delta comes from a 32-bit counter sum that wraps; the per-thread deltas do not.
    $pav.context_switches_per_s = if ($threadSwitches -gt 0) { [Math]::Round($threadSwitches / $windowSeconds, 1) } else { [Math]::Round((($main | Measure-Object sw -Sum).Sum) / $windowSeconds, 1) }
    $pav.max_cpu_in_25ms_ms = [Math]::Round((($main | Measure-Object cpu -Maximum).Maximum), 2)
    $pav.samples_over_5ms = @($main | Where-Object { $_.cpu -ge 5 }).Count
    $pav.threads_max = ($main | Measure-Object threads -Maximum).Maximum
    $pav.handles_first = $main[0].handles; $pav.handles_last = $main[$main.Count - 1].handles
    $pav.working_set_mb_last = [Math]::Round($main[$main.Count - 1].ws / 1MB, 1)
    $guard = @($inWindow | Where-Object { $_.name -eq 'Pavise.HardThreadGuardian' })
    $pav.guardian_cpu_ms = [Math]::Round((($guard | Measure-Object cpu -Sum).Sum), 1)
    $pav.precise = $cyclesPerMs -gt 0
    $pav.threads = $threadTable
    # Pavise CPU inside stutter frames versus everywhere.
    if ($stats.Contains('stutter_limit_ms') -and $frames.Count -gt 0) {
        $limit = $stats.stutter_limit_ms
        $mainArray = $main | Sort-Object qpc
        $qpcs = [double[]]($mainArray | ForEach-Object { $_.qpc })
        $busyDuringStutter = 0; $stutterCount = 0
        foreach ($f in $frames) {
            if ($f.ms -le $limit) { continue }
            $stutterCount++
            $fEnd = $f.qpc + $f.ms * $freq / 1000.0
            $idx = [Array]::BinarySearch($qpcs, $f.qpc); if ($idx -lt 0) { $idx = -bnot $idx }
            # The sample after the frame start covers it (each sample reports the preceding 25 ms).
            $busy = 0.0
            for ($k = $idx; $k -lt $qpcs.Count -and $qpcs[$k] -le $fEnd + 0.03 * $freq; $k++) { $busy += $mainArray[$k].cpu }
            if ($busy -ge 1.0) { $busyDuringStutter++ }
        }
        $pav.stutters_with_pavise_busy_1ms = $busyDuringStutter
        $pav.share_of_samples_busy_1ms = [Math]::Round((@($main | Where-Object { $_.cpu -ge 1.0 }).Count) / [double]$main.Count, 3)
    }
}
# Attributes the game ended up with while measured (last snapshot inside the window).
$attrs = Join-Path $RunDir 'probe-attributes.tsv'
$game = [ordered]@{}
if (Test-Path $attrs) {
    $mainPid = $manifest['pid']
    $lastProc = $null; $lastThreads = @(); $timer = $null; $power = $null; $children = @{}; $noise = @{}; $currentPid = $null
    foreach ($line in [IO.File]::ReadLines($attrs)) {
        $c = $line.Split("`t")
        $q = [double]::Parse($c[1], $inv)
        if ($q -lt $startQpc -or $q -gt $endQpc) { continue }
        if ($c[2] -eq 'timer_100ns') { $timer = $c[3]; $power = $c[5] }
        elseif ($c[2] -eq 'proc' -and $c[3] -eq 'BenchGame') {
            $currentPid = $c[5]
            $value = ($c[6..($c.Count - 1)] -join ' ')
            if ($c[5] -eq $mainPid) { $lastProc = $value; $lastThreads = @() } else { $children[$c[5]] = $value }
        }
        elseif ($c[2] -eq 'proc' -and $c[3] -eq 'BenchNoise') { $noise[$c[5]] = ($c[6..($c.Count - 1)] -join ' ') }
        elseif ($c[2] -eq 'thread' -and $c[3] -eq 'BenchGame' -and $currentPid -eq $mainPid) { $lastThreads += ('tid ' + $c[5] + ' ' + ($c[8..($c.Count - 1)] -join ' ')) }
    }
    $game.process = $lastProc; $game.busiest_threads = $lastThreads; $game.children = @($children.Values); $game.noise = @($noise.Values)
    $game.timer_100ns = $timer; $game.power_scheme = $power
}
$runMeta = if (Test-Path (Join-Path $RunDir 'run.json')) { Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json } else { $null }
$summary = [ordered]@{ run = (Split-Path $RunDir -Leaf); meta = $runMeta; completed = $manifest['completed']; reason = $manifest['reason']; foreground_valid = $manifest['foreground_valid'];
    adapter = $manifest['adapter_name']; frames = $stats; pavise = $pav; game = $game }
($summary | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $RunDir 'summary.json') -Encoding UTF8
if (-not $Quiet) {
    $s = $stats; $p = $pav
    "{0}: done={1} fps={2} low1={3} low01={4} p99={5}ms max={6}ms stutter/min={7} >25ms={8} | Pavise cpu={9}% max25={10}ms busy-stutters={11}/{12}" -f `
        (Split-Path $RunDir -Leaf), $manifest['completed'], $s.avg_fps, $s.low1_fps, $s.low01_fps, $s.p99_ms, $s.max_ms, $s.stutters_per_min, $s.hitches_25ms,
        $p.cpu_percent_of_one_core, $p.max_cpu_in_25ms_ms, $p.stutters_with_pavise_busy_1ms, $s.stutters
}
