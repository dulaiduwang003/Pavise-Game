# @author bdth 2074055628@qq.com
# file: orchestrate an activated-game-mode overhead measurement against one Pavise build

param(
    [Parameter(Mandatory = $true)][string] $PaviseExe,
    [Parameter(Mandatory = $true)][string] $Label,
    [int] $GameSeconds = 130,
    [int] $SettleSeconds = 28,
    [int] $MeasureSeconds = 85
)

$tools = 'C:\Code\Aegis\tools\OverheadProbe'
$scratch = 'C:\Users\Star\AppData\Local\Temp\claude\c--Code-Aegis\47ed3f21-d1b4-4678-96c5-8ab818071ea2\scratchpad'
$log = Join-Path $env:APPDATA 'Pavise\Pavise.log'

function Stop-Pavise {
    try { [System.Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit').Set() | Out-Null } catch { }
    for ($i = 0; $i -lt 25; $i++) {
        if (-not (Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 700
    }
    Write-Output "  exit event ignored, force-killing stale instance"
    Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    return -not (Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue)
}

Write-Output "=== active-mode test: $Label ($PaviseExe) ==="
Get-Process -Name 'FrameBench' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (-not (Stop-Pavise)) { Write-Output "FAILED: could not clear previous Pavise instance"; exit 1 }

$logMark = 0
if (Test-Path $log) {
    $logMark = ([IO.File]::ReadAllText($log, [Text.Encoding]::UTF8) -split "`r?`n").Length
}

Start-Process -FilePath $PaviseExe -ArgumentList '--autostart' -Verb RunAs
$pid0 = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    $proc = Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { $pid0 = $proc.Id; break }
}
if (-not $pid0) { Write-Output "FAILED: Pavise did not start"; exit 1 }
Start-Sleep -Seconds 12
$still = Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $still) {
    Write-Output "  first attempt exited (crash-recovery restart?), retrying once"
    Start-Process -FilePath $PaviseExe -ArgumentList '--autostart' -Verb RunAs
    Start-Sleep -Seconds 18
    $still = Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $still) { Write-Output "FAILED: Pavise did not stay up"; exit 1 }
Write-Output "Pavise up (pid $($still.Id)). Launching fullscreen frame bench for ${GameSeconds}s..."

$frameOut = Join-Path $scratch "frames-$Label.txt"
Start-Process -FilePath (Join-Path $tools 'FrameBench.exe') `
    -ArgumentList '--fps', '144', '--seconds', "$GameSeconds", '--label', $Label, '--out', $frameOut

Start-Sleep -Seconds $SettleSeconds
Write-Output "Settled. Measuring overhead for ${MeasureSeconds}s..."

& (Join-Path $tools 'OverheadProbe.exe') --seconds $MeasureSeconds --interval 1000 `
    --label $Label --out (Join-Path $scratch "probe-$Label.csv")

Write-Output ""
Write-Output "--- waiting for frame bench to finish ---"
for ($i = 0; $i -lt 60; $i++) {
    if (-not (Get-Process -Name 'FrameBench' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 1
}

Write-Output ""
Write-Output "--- frame stats ---"
if (Test-Path $frameOut) { Get-Content $frameOut } else { Write-Output "(no frame output)" }

Write-Output ""
Write-Output "--- Pavise log (this run) ---"
if (Test-Path $log) {
    $all = [IO.File]::ReadAllText($log, [Text.Encoding]::UTF8) -split "`r?`n"
    if ($all.Length -gt $logMark) {
        $all[$logMark..($all.Length - 1)] |
            Where-Object { $_ -ne '' -and $_ -notmatch '(Isolated|Restrained|Eco)$' } |
            Select-Object -Last 16
    }
    else { Write-Output "(no new log lines)" }
}
