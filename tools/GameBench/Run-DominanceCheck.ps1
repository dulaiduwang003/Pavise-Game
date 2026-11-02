# Pavise must stay in charge of the game's process and critical-thread affinity: once the render thread is bound, an
# outside program rewrites both, and Pavise has to write them back (process: next drift audit, about 1 s; thread: next
# critical-thread audit, about 5 s). Pavise must already be running with the bench game in its library.
param([int]$Seconds = 90, [int]$TamperAt = 55, [int]$ObserveSeconds = 12)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace Bench -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] public static extern System.IntPtr OpenThread(int access, bool inherit, int tid);
[DllImport("kernel32.dll", SetLastError = true)] public static extern System.UIntPtr SetThreadAffinityMask(System.IntPtr thread, System.UIntPtr mask);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(System.IntPtr h);
'@
$log = Join-Path $env:APPDATA 'PaviseAuto\Pavise.log'
$logStart = (Get-Item $log).Length
$bench = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $PSScriptRoot 'Run-PaviseBench.ps1'), '-Label', 'dominance', '-Seconds', $Seconds, '-Pavise', 'keep', '-Uncapped'
Start-Sleep -Seconds $TamperAt
function NewLog { $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $fs.Seek($logStart, 'Begin') | Out-Null; $r = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $t = $r.ReadToEnd(); $r.Close(); $t }
$text = NewLog
$m = [regex]::Matches($text, 'BenchGame TID (\d+) -> ') | Select-Object -Last 1
if (-not $m) { throw 'the render thread was not bound yet' }
$tid = [int]$m.Groups[1].Value
$game = Get-Process BenchGame | Where-Object { $_.Threads.Id -contains $tid } | Select-Object -First 1
"game pid $($game.Id) render tid $tid process affinity before 0x{0:X}" -f [int64]$game.ProcessorAffinity
$game.ProcessorAffinity = [IntPtr]3
$h = [Bench.Native]::OpenThread(0x0060, $false, $tid)   # THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION
$old = [Bench.Native]::SetThreadAffinityMask($h, (New-Object UIntPtr 2)); [Bench.Native]::CloseHandle($h) | Out-Null
"tampered: process -> 0x3, render thread -> 0x2 (thread was 0x{0:X})" -f $old.ToUInt64()
$deadline = (Get-Date).AddSeconds($ObserveSeconds)
while ((Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
$game.Refresh()
"process affinity {0}s later: 0x{1:X}" -f $ObserveSeconds, [int64]$game.ProcessorAffinity
$bench.WaitForExit(($Seconds + 120) * 1000) | Out-Null
(NewLog) -split "`r?`n" | Where-Object { $_ -match '改动了游戏亲和性|reasserted|写回|written back|withdrawn|revoked' } | ForEach-Object { $_.Substring(0, [Math]::Min(220, $_.Length)) }
