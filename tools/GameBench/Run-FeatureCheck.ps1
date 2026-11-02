# Apply/restore check of a whole Pavise session on this machine: system snapshot before the game, during the match
# (after critical-thread binding has settled) and after the session has been restored, then a diff report.
# Pavise must already be running with the bench game in its library.
param([string]$Label = 'feature', [int]$Seconds = 100, [int]$DuringAt = 60, [int]$AfterWait = 25,
      [string]$Scene = 'main', [string]$OutRoot = "$env:TEMP\PaviseFeatureChecks")
$ErrorActionPreference = 'Stop'
$dir = Join-Path $OutRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Label)
New-Item -ItemType Directory -Force $dir | Out-Null
$snap = Join-Path $PSScriptRoot 'Snapshot-System.ps1'
function Snapshot([string]$name) {
    $out = Join-Path $dir "$name.tsv"
    $p = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $snap, '-Out', $out
    if (-not (Test-Path $out)) { throw "snapshot $name failed" }
    "$name snapshot: $((Get-Content $out).Count) facts"
}
if (-not (Get-Process Pavise -ErrorAction SilentlyContinue)) { throw 'Pavise is not running' }
$logPath = Join-Path $env:APPDATA 'PaviseAuto\Pavise.log'
$logStart = (Get-Item $logPath).Length
Snapshot 'before'
$bench = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $PSScriptRoot 'Run-PaviseBench.ps1'), '-Label', $Label, '-Seconds', $Seconds, '-Pavise', 'keep', '-Scene', $Scene, '-Uncapped'
Start-Sleep -Seconds $DuringAt
Snapshot 'during'
$bench.WaitForExit(($Seconds + 120) * 1000) | Out-Null
Start-Sleep -Seconds $AfterWait
Snapshot 'after'
# The session's own log lines, for reading the report against what Pavise says it did.
$fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite'); $fs.Seek($logStart, 'Begin') | Out-Null
$reader = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); [IO.File]::WriteAllText((Join-Path $dir 'pavise-log.txt'), $reader.ReadToEnd()); $reader.Close()
python (Join-Path $PSScriptRoot 'Compare-Snapshots.py') (Join-Path $dir 'before.tsv') (Join-Path $dir 'during.tsv') (Join-Path $dir 'after.tsv') (Join-Path $dir 'report.md') | Out-Null
"report: " + (Join-Path $dir 'report.md')
