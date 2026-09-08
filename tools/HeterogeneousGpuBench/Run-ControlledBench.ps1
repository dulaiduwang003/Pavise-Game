[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BenchPath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'results'),
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$CaseName = 'manual',
    [ValidateRange(0, 64)][int]$PrimaryIndex = 0,
    [ValidateRange(0, 64)][int]$SecondaryIndex = 1,
    [ValidateRange(64, 7680)][int]$Width = 1920,
    [ValidateRange(64, 4320)][int]$Height = 1080,
    [ValidateRange(1, 1000)][int]$SceneLoops = 100,
    [ValidateRange(1, 1000)][int]$PostLoops = 4,
    [ValidateSet('direct', 'copy')][string]$TransferPath = 'copy',
    [ValidateSet('shared', 'local')][string]$SecondaryInput = 'shared',
    [ValidateRange(0, 100000)][int]$WarmupFrames = 120,
    [ValidateRange(30, 100000)][int]$MeasureFrames = 600,
    [ValidateRange(1, 20)][int]$Repeats = 3,
    [ValidateRange(0, 300)][int]$PreconditionSeconds = 10,
    [ValidateRange(5, 120)][int]$MinimumIdleSeconds = 10,
    [ValidateRange(10, 600)][int]$IdleTimeoutSeconds = 120,
    [ValidateRange(10, 3600)][int]$TimeoutSeconds = 900,
    [ValidateRange(60, 90)][int]$StopTemperatureC = 83,
    [switch]$ValidateOnly,
    [switch]$AllowBackgroundLoadScreening
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 这层包装只观察本机 只管自己拉起的台架进程
# 不改电源方案 不改优先级 不改驱动设置 不碰别的进程
function Write-JsonFile {
    param([object]$Value, [string]$Path)
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 16) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Convert-InvariantNumber {
    param([string]$Value)
    $number = 0.0
    if ([double]::TryParse($Value, [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and
            -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)) { return $number }
    return $null
}

function Test-FiniteJsonNumber {
    param([object]$Value)
    return (($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) -and
        -not [double]::IsNaN([double]$Value) -and -not [double]::IsInfinity([double]$Value))
}

if (-not ('Pavise.BenchObservation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Pavise {
    public static class BenchObservation {
        [StructLayout(LayoutKind.Sequential)]
        public struct LastInput { public uint cbSize; public uint dwTime; }
        [StructLayout(LayoutKind.Sequential)]
        public struct PowerStatus {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public uint BatteryLifeTime, BatteryFullLifeTime;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime {
            public uint Low, High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }
        [DllImport("user32.dll", SetLastError=true)]
        public static extern bool GetLastInputInfo(ref LastInput value);
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();
        [DllImport("kernel32.dll", SetLastError=true)]
        public static extern bool GetSystemPowerStatus(out PowerStatus value);
        [DllImport("kernel32.dll", SetLastError=true)]
        public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        public static ulong[] CpuTimes() {
            FileTime idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user))
                throw new System.ComponentModel.Win32Exception();
            return new ulong[] { idle.Value, kernel.Value, user.Value };
        }
        public static uint InputTick() {
            LastInput value = new LastInput();
            value.cbSize = (uint)Marshal.SizeOf(typeof(LastInput));
            if (!GetLastInputInfo(ref value)) throw new System.ComponentModel.Win32Exception();
            return value.dwTime;
        }
        public static double IdleSeconds() {
            return ((GetTickCount64() + 4294967296UL - InputTick()) % 4294967296UL) / 1000.0;
        }
    }
}
'@
}

function Read-PowerStatus {
    $powerState = New-Object Pavise.BenchObservation+PowerStatus
    if (-not [Pavise.BenchObservation]::GetSystemPowerStatus([ref]$powerState)) {
        throw 'Unable to observe the AC/battery state.'
    }
    return [pscustomobject]@{
        ac_line_status = [int]$powerState.ACLineStatus
        battery_percent = if ($powerState.BatteryLifePercent -eq 255) { $null } else { [int]$powerState.BatteryLifePercent }
        battery_flags = [int]$powerState.BatteryFlag
    }
}

function Read-CpuPercent {
    $currentCpu = [Pavise.BenchObservation]::CpuTimes()
    $cpuPercent = $null
    if ($null -ne $script:previousCpu) {
        $idleDelta = [double]$currentCpu[0] - [double]$script:previousCpu[0]
        $totalDelta = ([double]$currentCpu[1] - [double]$script:previousCpu[1]) +
            ([double]$currentCpu[2] - [double]$script:previousCpu[2])
        if ($totalDelta -gt 0) { $cpuPercent = [Math]::Round(100.0 * (1.0 - $idleDelta / $totalDelta), 2) }
    }
    $script:previousCpu = $currentCpu
    return $cpuPercent
}

function Read-GpuActivity {
    # 快照放在计时阶段之外 CIM 枚举太重 不能每帧跑
    try {
        return @(Get-CimInstance -ClassName Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -OperationTimeoutSec 8 |
            Where-Object { $_.UtilizationPercentage -gt 0 } | ForEach-Object {
                $ownerId = 0
                if ($_.Name -match '^pid_(\d+)_') { $ownerId = [int]$Matches[1] }
                $ownerProcess = Get-Process -Id $ownerId -ErrorAction SilentlyContinue
                [pscustomobject]@{
                    instance = $_.Name
                    process_id = $ownerId
                    process_name = if ($ownerProcess) { $ownerProcess.ProcessName } else { 'exited_or_unknown' }
                    utilization_percent = [int]$_.UtilizationPercentage
                }
            })
    } catch {
        $script:observationWarnings.Add('GPU engine snapshot unavailable: ' + $_.Exception.Message)
        return @()
    }
}

function Read-NvidiaTelemetry {
    if (-not $script:nvidiaPath) { throw 'Temperature guard unavailable: nvidia-smi was not found. This controlled runner requires NVIDIA temperature telemetry.' }
    $telemetryProcess = $null
    try {
        $telemetryStart = [Diagnostics.ProcessStartInfo]::new()
        $telemetryStart.FileName = $script:nvidiaPath
        $telemetryStart.Arguments = '--query-gpu=index,temperature.gpu,utilization.gpu,power.draw,clocks.gr,clocks.mem,pstate --format=csv,noheader,nounits'
        $telemetryStart.UseShellExecute = $false
        $telemetryStart.CreateNoWindow = $true
        $telemetryStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $telemetryStart.RedirectStandardOutput = $true
        $telemetryStart.RedirectStandardError = $true
        $telemetryProcess = [Diagnostics.Process]::new()
        $telemetryProcess.StartInfo = $telemetryStart
        if (-not $telemetryProcess.Start()) { throw 'Unable to start temperature telemetry.' }
        $telemetryOut = $telemetryProcess.StandardOutput.ReadToEndAsync()
        $telemetryError = $telemetryProcess.StandardError.ReadToEndAsync()
        if (-not $telemetryProcess.WaitForExit(2000)) { throw 'Temperature telemetry exceeded its 2-second timeout.' }
        if ($telemetryProcess.ExitCode -ne 0) { throw "nvidia-smi exited $($telemetryProcess.ExitCode)" }
        if (-not $telemetryOut.Wait(1000) -or -not $telemetryError.Wait(1000)) { throw 'Temperature telemetry output did not close.' }
        $lines = $telemetryOut.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $gpuTelemetry = @($lines | ConvertFrom-Csv -Header index, temperature, utilization, power, core_clock, memory_clock, pstate |
            ForEach-Object {
                [pscustomobject]@{
                    index = $_.index.Trim()
                    temperature_c = Convert-InvariantNumber $_.temperature
                    utilization_percent = Convert-InvariantNumber $_.utilization
                    power_w = Convert-InvariantNumber $_.power
                    core_clock_mhz = Convert-InvariantNumber $_.core_clock
                    memory_clock_mhz = Convert-InvariantNumber $_.memory_clock
                    pstate = $_.pstate.Trim()
                }
            })
        if ($gpuTelemetry.Count -eq 0 -or @($gpuTelemetry | Where-Object {
            $null -eq $_.temperature_c -or $_.temperature_c -lt 0 -or $_.temperature_c -gt 120
        }).Count -gt 0) {
            throw 'NVIDIA temperature telemetry was empty or non-numeric.'
        }
        return $gpuTelemetry
    } catch {
        throw ('Temperature guard unavailable: ' + $_.Exception.Message)
    } finally {
        if ($telemetryProcess) {
            try {
                if (-not $telemetryProcess.HasExited) {
                    $telemetryProcess.Kill()
                    $null = $telemetryProcess.WaitForExit(2000)
                }
            } finally { $telemetryProcess.Dispose() }
        }
    }
}

function Add-Observation {
    param([string]$Stage)
    $powerState = Read-PowerStatus
    $gpuState = @(Read-NvidiaTelemetry)
    $entry = [pscustomobject]@{
        utc = [DateTime]::UtcNow.ToString('o')
        elapsed_seconds = [Math]::Round($script:runClock.Elapsed.TotalSeconds, 3)
        stage = $Stage
        idle_seconds = [Math]::Round([Pavise.BenchObservation]::IdleSeconds(), 3)
        input_tick = [Pavise.BenchObservation]::InputTick()
        cpu_percent = Read-CpuPercent
        ac_line_status = $powerState.ac_line_status
        battery_percent = $powerState.battery_percent
        nvidia = $gpuState
    }
    $script:observations.Add($entry)
    return $entry
}

if ($PrimaryIndex -eq $SecondaryIndex) { throw 'Two different adapter indices are required.' }
$BenchPath = (Resolve-Path -LiteralPath $BenchPath).ProviderPath
$manifestPath = Join-Path (Split-Path -Parent $BenchPath) 'build-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'A successful build-manifest.json is required next to the executable. Rebuild with build.ps1.'
}
$buildManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($buildManifest.status -ne 'success') { throw 'The build manifest is not marked successful.' }
$exeHash = (Get-FileHash -LiteralPath $BenchPath -Algorithm SHA256).Hash
$targetManifest = @($buildManifest.targets | Where-Object {
    [IO.Path]::GetFullPath($_.exe_path) -eq $BenchPath -and $_.exe_sha256 -eq $exeHash
})
if ($targetManifest.Count -ne 1) { throw 'The executable does not match exactly one attested build target.' }
$sourcePath = $targetManifest[0].source_path
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $targetManifest[0].source_sha256) {
    throw 'The benchmark source has changed since compilation. Rebuild before measuring.'
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).ProviderPath
$runName = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $CaseName, ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = (New-Item -ItemType Directory -Path (Join-Path $OutputRoot $runName)).FullName
$benchDirectory = Join-Path $runDirectory 'bench'
$script:observations = New-Object 'System.Collections.Generic.List[object]'
$script:observationWarnings = New-Object 'System.Collections.Generic.List[string]'
$violations = New-Object 'System.Collections.Generic.List[string]'
$script:previousCpu = $null
$script:runClock = [Diagnostics.Stopwatch]::StartNew()
$script:nvidiaPath = $null
$nvidiaCommand = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if ($nvidiaCommand) { $script:nvidiaPath = $nvidiaCommand.Source }
$script:observationWarnings.Add('Temperature protection observes NVIDIA GPU only; CPU and Intel GPU temperature are not measured.')

$arguments = @(
    $(if ($ValidateOnly) { '--validate-only' } else { '--run' }),
    '--width', $Width, '--height', $Height, '--scene-loops', $SceneLoops,
    '--post-loops', $PostLoops, '--primary-index', $PrimaryIndex, '--secondary-index', $SecondaryIndex,
    '--transfer-path', $TransferPath, '--secondary-input', $SecondaryInput, '--warmup-frames', $WarmupFrames,
    '--measure-frames', $MeasureFrames, '--repeats', $Repeats,
    '--precondition-seconds', $PreconditionSeconds, '--output', ('"' + $benchDirectory + '"')
)
$snapshotDirectory = (New-Item -ItemType Directory -Path (Join-Path $runDirectory 'artifact-snapshot')).FullName
Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $snapshotDirectory 'D3D12ComputeBench.cpp')
Copy-Item -LiteralPath $BenchPath -Destination (Join-Path $snapshotDirectory 'benchmark.exe')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $snapshotDirectory 'Run-ControlledBench.ps1')
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $snapshotDirectory 'original-build-manifest.json')
if ((Get-FileHash -LiteralPath (Join-Path $snapshotDirectory 'D3D12ComputeBench.cpp') -Algorithm SHA256).Hash -ne $targetManifest[0].source_sha256 -or
    (Get-FileHash -LiteralPath (Join-Path $snapshotDirectory 'benchmark.exe') -Algorithm SHA256).Hash -ne $exeHash) {
    throw 'The archived source/executable bytes do not match their attested hashes; no benchmark was started.'
}
$request = [ordered]@{
    schema_version = 1
    created_utc = [DateTime]::UtcNow.ToString('o')
    case_name = $CaseName
    purpose = if ($ValidateOnly) { 'correctness_preflight' } else { 'controlled_owned_compute_experiment' }
    environment_regime = if ($ValidateOnly) { 'correctness_only_not_timed' } elseif ($AllowBackgroundLoadScreening) { 'background_loaded_screening' } else { 'idle_gated' }
    maximum_preflight_cpu_percent = if ($ValidateOnly) { $null } elseif ($AllowBackgroundLoadScreening) { 50 } else { 20 }
    product_ready = $false
    command = $BenchPath
    arguments = $arguments
    secondary_input = $SecondaryInput
    artifact_snapshot_directory = $snapshotDirectory
    executable_sha256 = $exeHash
    source_sha256 = $targetManifest[0].source_sha256
    runner_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    input_abort_enabled = $true
    ac_required = $true
    temperature_abort_c = $StopTemperatureC
    temperature_sensor_scope = 'NVIDIA GPU only; not a whole-laptop thermal guarantee'
    timeout_seconds = $TimeoutSeconds
    minimum_idle_seconds = $MinimumIdleSeconds
    build = $buildManifest
}
Write-JsonFile -Value $request -Path (Join-Path $runDirectory 'request.json')
Write-Output "RUN_DIRECTORY=$runDirectory"

$benchProcess = $null
$stdoutTask = $null
$stderrTask = $null
$benchExitCode = $null
$benchSummary = $null
$startedBenchmark = $false
$environmentBefore = $null
$environmentAfter = $null
$runException = $null
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -OperationTimeoutSec 8
    $environmentBefore = [ordered]@{
        utc = [DateTime]::UtcNow.ToString('o')
        os = [ordered]@{ caption = $os.Caption; version = $os.Version; build = $os.BuildNumber }
        cpu = @(Get-CimInstance -ClassName Win32_Processor -OperationTimeoutSec 8 | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
        video_controllers = @(Get-CimInstance -ClassName Win32_VideoController -OperationTimeoutSec 8 | Select-Object Name, DriverVersion, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution)
        power = Read-PowerStatus
        active_power_scheme = (@(& powercfg.exe /getactivescheme) -join ' ')
        gpu_activity = @(Read-GpuActivity)
        nvidia_smi_path = $script:nvidiaPath
        capture_scope_note = 'GPU engine snapshots are before/after only. NVIDIA telemetry and total CPU/input/power observations are sampled during the run. Background apps are not stopped.'
    }
    Write-JsonFile -Value $environmentBefore -Path (Join-Path $runDirectory 'environment-before.json')

    # 只验正确性的那一轮不看 CPU 空闲 它本来就不计分
    # 显式的带载筛查只把 CPU 预检放宽到 50%
    # 正确性 计时 收益 输入 交流电 温度和超时这些门一个都不放
    # 后台 GPU 活动是记下来 不是默认当成空闲
    $idleClock = [Diagnostics.Stopwatch]::StartNew()
    $quietSamples = 0
    while ($idleClock.Elapsed.TotalSeconds -lt $IdleTimeoutSeconds) {
        $sample = Add-Observation -Stage 'idle_preflight'
        if ($sample.ac_line_status -ne 1) { throw 'AC power is required; no test was started.' }
        foreach ($gpuState in $sample.nvidia) {
            if ($null -ne $gpuState.temperature_c -and $gpuState.temperature_c -ge $StopTemperatureC) {
                throw 'The NVIDIA GPU is already at the configured stop temperature.'
            }
        }
        $cpuGatePassed = $ValidateOnly -or ($null -ne $sample.cpu_percent -and
            $sample.cpu_percent -le $(if ($AllowBackgroundLoadScreening) { 50 } else { 20 }))
        if ($sample.idle_seconds -ge $MinimumIdleSeconds -and $cpuGatePassed) {
            $quietSamples++
        } else { $quietSamples = 0 }
        if ($quietSamples -ge 3) { break }
        Start-Sleep -Seconds 1
    }
    if ($quietSamples -lt 3) { throw 'The CPU/input idle gate was not met within the timeout; no test was started.' }
    $inputTickAtStart = [Pavise.BenchObservation]::InputTick()
    Write-Output ('Preflight passed; regime=' + $request.environment_regime + '. Only the owned benchmark will be stopped if safety gates fail.')

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $BenchPath
    $startInfo.Arguments = $arguments -join ' '
    $startInfo.WorkingDirectory = Split-Path -Parent $BenchPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $benchProcess = [Diagnostics.Process]::new()
    $benchProcess.StartInfo = $startInfo
    if (-not $benchProcess.Start()) { throw 'Unable to start the benchmark.' }
    $startedBenchmark = $true
    $stdoutTask = $benchProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $benchProcess.StandardError.ReadToEndAsync()
    $measurementClock = [Diagnostics.Stopwatch]::StartNew()
    $lastProgressSeconds = -30.0
    while (-not $benchProcess.WaitForExit(1000)) {
        $sample = Add-Observation -Stage 'benchmark'
        if ($sample.input_tick -ne $inputTickAtStart) { $violations.Add('User input resumed during the experiment.') }
        if ($sample.ac_line_status -ne 1) { $violations.Add('AC power was lost or could not be verified.') }
        foreach ($gpuState in $sample.nvidia) {
            if ($null -ne $gpuState.temperature_c -and $gpuState.temperature_c -ge $StopTemperatureC) {
                $violations.Add("NVIDIA GPU temperature reached the $StopTemperatureC C stop threshold.")
            }
        }
        if ($measurementClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $violations.Add('The benchmark exceeded the wall-clock timeout.') }
        if ($violations.Count -gt 0) {
            if (-not $benchProcess.HasExited) { $benchProcess.Kill() }
            if (-not $benchProcess.WaitForExit(5000)) { throw 'The owned benchmark did not exit after termination.' }
            break
        }
        if ($measurementClock.Elapsed.TotalSeconds - $lastProgressSeconds -ge 30) {
            Write-Output ('BENCH_RUNNING elapsed={0:n0}s cpu={1}% idle={2:n0}s gpu={3}' -f
                $measurementClock.Elapsed.TotalSeconds, $sample.cpu_percent, $sample.idle_seconds,
                (($sample.nvidia | ForEach-Object { '{0}C/{1}W/{2}MHz' -f $_.temperature_c, $_.power_w, $_.core_clock_mhz }) -join ';'))
            $lastProgressSeconds = $measurementClock.Elapsed.TotalSeconds
        }
    }
    if (-not $benchProcess.WaitForExit(5000)) { throw 'The owned benchmark did not exit within the final wait.' }
    $benchExitCode = $benchProcess.ExitCode
    $processDuration = ($benchProcess.ExitTime.ToUniversalTime() - $benchProcess.StartTime.ToUniversalTime()).TotalSeconds
    if ($processDuration -gt $TimeoutSeconds) { $violations.Add('The final benchmark process duration exceeded the timeout.') }
    $lastSample = Add-Observation -Stage 'benchmark_end'
    if ($lastSample.input_tick -ne $inputTickAtStart) { $violations.Add('User input changed before the final observation.') }
    if ($lastSample.ac_line_status -ne 1) { $violations.Add('AC power was not verified at the final observation.') }
    foreach ($gpuState in $lastSample.nvidia) {
        if ($gpuState.temperature_c -ge $StopTemperatureC) { $violations.Add('The final GPU observation reached the stop temperature.') }
    }
    $summaryPath = Join-Path $benchDirectory 'summary.json'
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) { $benchSummary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json }
    else { $violations.Add('The benchmark did not emit summary.json.') }
    if ($benchExitCode -notin @(0, 2)) { $violations.Add("Unexpected benchmark exit code: $benchExitCode") }
    if ($null -eq $benchSummary) { $violations.Add('No structured benchmark result was available.') }
    else {
        $expectedMode = if ($ValidateOnly) { 'validate-only' } else { 'run' }
        if ($benchSummary.schema_version -ne 2 -or $benchSummary.mode -ne $expectedMode) {
            $violations.Add('Benchmark schema or action does not match the requested experiment.')
        }
        if ($benchSummary.product_ready -isnot [bool] -or $benchSummary.validation_passed -isnot [bool]) {
            $violations.Add('Benchmark boolean fields must have JSON boolean types.')
        }
        if ($benchSummary.scope -ne 'owned_offscreen_compute_only' -or $benchSummary.product_ready -ne $false -or
            $benchSummary.transfer_path -ne $TransferPath -or $benchSummary.secondary_input -ne $SecondaryInput -or
            $benchSummary.config.secondary_input -ne $SecondaryInput) { $violations.Add('Benchmark scope/path/input metadata is invalid.') }
        if ($benchSummary.config.width -ne $Width -or $benchSummary.config.height -ne $Height -or
            $benchSummary.config.scene_loops -ne $SceneLoops -or $benchSummary.config.post_loops -ne $PostLoops -or
            $benchSummary.config.measure_frames -ne $MeasureFrames -or $benchSummary.config.warmup_frames -ne $WarmupFrames -or
            $benchSummary.config.repeats -ne $Repeats -or $benchSummary.config.precondition_seconds -ne $PreconditionSeconds -or
            $benchSummary.hardware.primary.index -ne $PrimaryIndex -or $benchSummary.hardware.secondary.index -ne $SecondaryIndex) {
            $violations.Add('Benchmark configuration does not match the recorded request.')
        }
        if ($ValidateOnly) {
            if ($benchSummary.validation_passed -ne $true -or $null -ne $benchSummary.verdict -or $benchExitCode -ne 0) {
                $violations.Add('Correctness-only validation did not complete successfully.')
            }
        } else {
            if ($benchSummary.verdict -notin @('INVALID', 'NO_BENEFIT', 'CANDIDATE')) { $violations.Add('Unknown or missing benchmark verdict.') }
            if ($benchSummary.verdict -eq 'INVALID') {
                if ($benchExitCode -ne 2) { $violations.Add('INVALID verdict requires exit code 2.') }
            } else {
                if ($benchExitCode -ne 0 -or $benchSummary.validation_passed -ne $true -or
                    $benchSummary.requested_phases -ne (4 * $Repeats) -or $benchSummary.completed_phases -ne (4 * $Repeats)) {
                    $violations.Add('A scored run requires successful validation, all requested phases, and exit code 0.')
                }
                foreach ($modeName in @('single', 'multi')) {
                    foreach ($metricName in @('avg_completion_rate', 'wall_completion_rate', 'one_percent_low', 'p99_ms')) {
                        $metricValue = $benchSummary.metrics.$modeName.$metricName
                        if (-not (Test-FiniteJsonNumber $metricValue) -or $metricValue -le 0) {
                            $violations.Add('Scored run has invalid metric: ' + $modeName + '.' + $metricName)
                        }
                    }
                }
                $validityGates = @('distinct_adapters', 'correctness_preflight', 'complete_protocol', 'phase_measurements_valid',
                    'protocol_order_valid', 'gpu_wall_consistent', 'baseline_drift_within_5_pct')
                $gainGates = @('average_gain_at_least_2_pct', 'wall_gain_at_least_2_pct', 'low_gain_at_least_1_pct',
                    'p99_regression_at_most_5_pct', 'pair_wins_at_least_two_thirds')
                foreach ($gateName in ($validityGates + $gainGates)) {
                    $gateValue = $benchSummary.gates.$gateName
                    if ($gateValue -isnot [bool] -or (($gateName -in $validityGates -or $benchSummary.verdict -eq 'CANDIDATE') -and -not $gateValue)) {
                        $violations.Add('Benchmark gate is missing, invalid, or inconsistent with its verdict: ' + $gateName)
                    }
                }
            }
        }
    }
    if ((Get-FileHash -LiteralPath $BenchPath -Algorithm SHA256).Hash -ne $exeHash -or
        (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $targetManifest[0].source_sha256) {
        $violations.Add('Executable or source changed during the run.')
    }
    $environmentAfter = [ordered]@{
        utc = [DateTime]::UtcNow.ToString('o')
        power = Read-PowerStatus
        active_power_scheme = (@(& powercfg.exe /getactivescheme) -join ' ')
        gpu_activity = @(Read-GpuActivity)
    }
    if ($environmentAfter.active_power_scheme -ne $environmentBefore.active_power_scheme) {
        $violations.Add('The active power scheme changed during the experiment.')
    }
} catch {
    $runException = $_.Exception.Message
    $violations.Add($runException)
} finally {
    if ($benchProcess -and $startedBenchmark -and -not $benchProcess.HasExited) {
        try {
            $benchProcess.Kill()
            if (-not $benchProcess.WaitForExit(5000)) { $violations.Add('Owned benchmark termination could not be confirmed.') }
        } catch { $violations.Add('Owned benchmark cleanup failed: ' + $_.Exception.Message) }
    }
    foreach ($stream in @(@{ Task = $stdoutTask; Name = 'stdout.txt' }, @{ Task = $stderrTask; Name = 'stderr.txt' })) {
        if ($stream.Task) {
            try {
                if (-not $stream.Task.Wait(2000)) { throw 'Output pipe did not close within 2 seconds.' }
                [IO.File]::WriteAllText((Join-Path $runDirectory $stream.Name), $stream.Task.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
            } catch { $violations.Add('Unable to save ' + $stream.Name + ': ' + $_.Exception.Message) }
        }
    }
    try { Write-JsonFile -Value @($script:observations.ToArray()) -Path (Join-Path $runDirectory 'telemetry.json') }
    catch { $violations.Add('Unable to save telemetry: ' + $_.Exception.Message) }
    if ($environmentAfter) {
        try { Write-JsonFile -Value $environmentAfter -Path (Join-Path $runDirectory 'environment-after.json') }
        catch { $violations.Add('Unable to save final environment: ' + $_.Exception.Message) }
    }
    $result = [ordered]@{
        schema_version = 1
        completed_utc = [DateTime]::UtcNow.ToString('o')
        case_name = $CaseName
        environment_regime = $request.environment_regime
        run_directory = $runDirectory
        benchmark_started = $startedBenchmark
        benchmark_exit_code = $benchExitCode
        environment_valid = ($violations.Count -eq 0)
        environment_valid_definition = 'Observed input/AC/NVIDIA-temperature/timeout/provenance/contract guards passed; NOT proof of a noise-free machine.'
        measurement_valid = ($violations.Count -eq 0 -and $null -ne $benchSummary -and $benchExitCode -eq 0 -and -not $ValidateOnly)
        candidate_eligible_for_independent_confirmation = ($violations.Count -eq 0 -and $null -ne $benchSummary -and $benchExitCode -eq 0 -and $benchSummary.verdict -eq 'CANDIDATE')
        violations = @($violations.ToArray() | Select-Object -Unique)
        observation_warnings = @($script:observationWarnings.ToArray() | Select-Object -Unique)
        background_gpu_activity_observed = ($null -ne $environmentBefore -and $environmentBefore.gpu_activity.Count -gt 0)
        background_gpu_activity_is_not_controlled = $true
        cpu_input_idle_gate_passed = (-not $AllowBackgroundLoadScreening -and -not $ValidateOnly -and $violations.Count -eq 0)
        noise_free_environment_verified = $false
        display_or_game_fps_measured = $false
        product_ready = $false
        benchmark = $benchSummary
    }
    try { Write-JsonFile -Value $result -Path (Join-Path $runDirectory 'controlled-result.json') }
    finally { if ($benchProcess) { $benchProcess.Dispose() } }
}

Write-Output ('ENVIRONMENT_VALID=' + ($violations.Count -eq 0))
if ($benchSummary) { Write-Output ('BENCHMARK_VERDICT=' + $benchSummary.verdict) }
foreach ($violation in @($violations.ToArray() | Select-Object -Unique)) { Write-Output ('INVALID_REASON=' + $violation) }
Write-Output ('RESULT=' + (Join-Path $runDirectory 'controlled-result.json'))
if ($violations.Count -gt 0 -or $benchExitCode -eq 2) { exit 2 }
exit 0
