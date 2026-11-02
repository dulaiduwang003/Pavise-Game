param(
    [string]$Label = 'run',
    [ValidateSet('dx11','dx12')][string]$Api = 'dx11',
    [ValidateSet('main','workers','gpu','mixed','stream','memory','startup','switch','wake','thermal')][string]$Scene = 'main',
    [double]$Seconds = 60,
    [int]$Width = 1600,
    [int]$Height = 900,
    [ValidateSet('keep','on','off')][string]$Pavise = 'keep',
    [int]$NoiseThreads = 0,
    [int]$NoiseDuty = 50,
    [switch]$NoiseIo,
    [ValidateSet('none','cpu','io','gpu')][string]$BackgroundLoad = 'none',
    [int]$Seed = 1,
    [switch]$Uncapped,
    [int]$AfterSeconds = 14,
    [string]$OutRoot = (Join-Path $env:TEMP 'PaviseBench-runs'),
    [string]$BenchExe = 'C:\Game\PaviseBench\BenchGame.exe',
    [string]$ProbeExe = (Join-Path $env:LOCALAPPDATA 'PaviseBenchProbe\BenchProbe.exe'),
    [string]$NoiseExe = (Join-Path $env:LOCALAPPDATA 'PaviseBenchNoise\BenchNoise.exe'),
    [string]$PaviseExe = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pavise.exe')
)
# One bench experiment: optional Pavise on/off, optional noise, the probe, one visible bench run, the Pavise log
# of that window, then Analyze-BenchRun.ps1. The bench needs the real foreground; run it on an idle desktop.
$ErrorActionPreference = 'Stop'
$paviseLog = Join-Path $env:APPDATA 'PaviseAuto\Pavise.log'

function Stop-Pavise {
    $running = @(Get-Process Pavise -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }
    try { $ev = [Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit', [Security.AccessControl.EventWaitHandleRights]::Modify); [void]$ev.Set(); $ev.Dispose() } catch { }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and @($running | Where-Object { Get-Process -Id $_.Id -ErrorAction SilentlyContinue }).Count -gt 0) { Start-Sleep -Milliseconds 500 }
    if (@(Get-Process Pavise -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Pavise did not exit' }
}
function Start-Pavise {
    if (@(Get-Process Pavise -ErrorAction SilentlyContinue).Count -gt 0) { return }
    Start-Process $PaviseExe -WorkingDirectory (Split-Path $PaviseExe)
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and @(Get-Process Pavise -ErrorAction SilentlyContinue).Count -eq 0) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 10
}

if ($Pavise -eq 'off') { Stop-Pavise } elseif ($Pavise -eq 'on') { Start-Pavise }
$paviseRunning = @(Get-Process Pavise -ErrorAction SilentlyContinue).Count -gt 0

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $OutRoot ($stamp + '-' + $Label)
New-Item -ItemType Directory -Force $runDir | Out-Null
$benchOut = Join-Path $runDir 'bench'
$logStart = if (Test-Path $paviseLog) { (Get-Item $paviseLog).Length } else { 0 }

$noise = $null; $noiseStop = Join-Path $runDir 'noise-stop.flag'
if ($NoiseThreads -gt 0) {
    $noiseArgs = @('--threads', $NoiseThreads, '--duty', $NoiseDuty, '--seconds', ($Seconds + 120), '--stop-flag', "`"$noiseStop`"")
    if ($NoiseIo) { $noiseArgs += '--io' }
    $noise = Start-Process $NoiseExe -ArgumentList $noiseArgs -PassThru -WindowStyle Hidden
}
$probe = Start-Process $ProbeExe -Verb RunAs -ArgumentList @('--out', "`"$runDir`"", '--seconds', ($Seconds + $AfterSeconds + 60), '--interval-ms', 25) -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 800

$benchArgs = @('--api', $Api, '--scene', $Scene, '--seconds', $Seconds, '--seed', $Seed, '--width', $Width, '--height', $Height,
    '--output', "`"$benchOut`"", '--nonce', ('n' + $stamp.Replace('-', '')), '--background-load', $BackgroundLoad)
if ($Uncapped) { $benchArgs += '--uncapped' }
$started = Get-Date
$bench = Start-Process $BenchExe -ArgumentList $benchArgs -PassThru
if (-not $bench.WaitForExit([int](($Seconds + 180) * 1000))) { try { $bench.Kill() } catch { }; $benchExit = 'timeout' } else { $benchExit = $bench.ExitCode }
$ended = Get-Date
# Pavise keeps its exit grace and restores afterwards; the probe watches that too.
Start-Sleep -Seconds $AfterSeconds
New-Item -ItemType File -Force (Join-Path $runDir 'probe-stop.flag') | Out-Null
New-Item -ItemType File -Force $noiseStop | Out-Null
if (-not $probe.WaitForExit(15000)) { try { $probe.Kill() } catch { } }
if ($noise -and -not $noise.WaitForExit(15000)) { try { $noise.Kill() } catch { } }

if (Test-Path $paviseLog) {
    $fs = [IO.File]::Open($paviseLog, 'Open', 'Read', 'ReadWrite')
    try {
        $len = $fs.Length
        if ($len -lt $logStart) { $logStart = 0 }
        $fs.Seek($logStart, 'Begin') | Out-Null
        $buf = New-Object byte[] ($len - $logStart)
        [void]$fs.Read($buf, 0, $buf.Length)
        [IO.File]::WriteAllBytes((Join-Path $runDir 'pavise-log.txt'), $buf)
    } finally { $fs.Dispose() }
}
$meta = [ordered]@{ label = $Label; api = $Api; scene = $Scene; seconds = $Seconds; width = $Width; height = $Height; pavise = $paviseRunning;
    noise_threads = $NoiseThreads; noise_duty = $NoiseDuty; noise_io = [bool]$NoiseIo; background_load = $BackgroundLoad; seed = $Seed; uncapped = [bool]$Uncapped;
    bench_exit = $benchExit; started = $started.ToString('o'); ended = $ended.ToString('o') }
($meta | ConvertTo-Json) | Set-Content -LiteralPath (Join-Path $runDir 'run.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'Analyze-BenchRun.ps1') -RunDir $runDir
