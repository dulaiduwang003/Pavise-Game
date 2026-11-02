$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Threading;
public sealed class PaviseLabStopPipe {
    public volatile bool Stop;
    private Thread reader;
    public void Begin() { reader = new Thread(delegate() { if (Console.ReadLine() != "stop") Environment.Exit(2); Stop = true; }); reader.IsBackground = true; reader.Start(); }
    public void Drain() { if (!reader.Join(3000)) throw new Exception("Owned stop reader timeout"); }
}
'@
[Console]::WriteLine('ready')
[Console]::Out.Flush()
if ([Console]::ReadLine() -ne 'go') { throw 'Owned start IPC failed' }
$helperStop = New-Object PaviseLabStopPipe
$helperStop.Begin()
$helperClock = [Diagnostics.Stopwatch]::StartNew()
$helperProcess = [Diagnostics.Process]::GetCurrentProcess()
$helperCpuStart = $helperProcess.TotalProcessorTime.TotalSeconds
$helperLoops = 0
while (!$helperStop.Stop) {
    $helperAccumulator = 17
    for ($helperIndex = 0; $helperIndex -lt 6000; $helperIndex++) {
        $helperAccumulator = (($helperAccumulator * 17) + $helperIndex) -band 0x7fffffff
    }
    $helperLoops++
}
$helperStop.Drain()
$helperProcess.Refresh()
$helperResult = @{loopsPerSecond=$helperLoops/$helperClock.Elapsed.TotalSeconds;cpuSeconds=$helperProcess.TotalProcessorTime.TotalSeconds-$helperCpuStart;restored=$true}
[Console]::WriteLine(($helperResult | ConvertTo-Json -Compress))
