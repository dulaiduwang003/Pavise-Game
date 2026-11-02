param([Parameter(Mandatory=$true)][string]$OfficialProviderDirectory)
$ErrorActionPreference = 'Stop'
$samplingWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$samplingOutput = Join-Path $samplingWorkspace 'build\monitoring-research-20261026\collector'
New-Item -ItemType Directory -Path $samplingOutput -Force | Out-Null
$samplingCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$samplingExe = Join-Path $samplingOutput 'MeasureHardware.exe'
& $samplingCompiler /nologo /target:exe /platform:x86 /optimize+ /codepage:65001 ('/out:' + $samplingExe) /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll (Join-Path $PSScriptRoot 'MeasureHardware.cs') *> (Join-Path $samplingOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $samplingOutput 'compile.log'); throw 'Collector research compilation failed' }
$samplingOrder = @('default','cpu-gpu','gpu-only','gpu-only','cpu-gpu','default')
$samplingAllResults = New-Object Text.StringBuilder
for ($samplingRun = 0; $samplingRun -lt $samplingOrder.Count; $samplingRun++) {
    $samplingProfile = $samplingOrder[$samplingRun]
    $samplingDirectory = Join-Path $samplingOutput ($samplingRun.ToString('00') + '-' + $samplingProfile)
    New-Item -ItemType Directory -Path $samplingDirectory -Force | Out-Null
    foreach ($samplingFile in @('Exporter.dll','LibreHardwareMonitorLib.dll','HidSharp.dll')) {
        [IO.File]::Copy((Join-Path $OfficialProviderDirectory $samplingFile),(Join-Path $samplingDirectory $samplingFile),$true)
    }
    [IO.File]::Copy($samplingExe,(Join-Path $samplingDirectory 'MeasureHardware.exe'),$true)
    $samplingBoard = if ($samplingProfile -eq 'default') { 'true' } else { 'false' }
    $samplingMemory = $samplingBoard
    $samplingCpu = if ($samplingProfile -eq 'gpu-only') { 'false' } else { 'true' }
    $samplingConfig = '<configuration><motherboard>' + $samplingBoard + '</motherboard><cpu>' + $samplingCpu + '</cpu><memory>' + $samplingMemory + '</memory><gpu>true</gpu><controller>false</controller><storage>false</storage><network>false</network><psu>false</psu><battery>false</battery></configuration>'
    [IO.File]::WriteAllText((Join-Path $samplingDirectory 'Exporter.config'),$samplingConfig)
    $samplingStart = New-Object Diagnostics.ProcessStartInfo
    $samplingStart.FileName = Join-Path $samplingDirectory 'MeasureHardware.exe'
    $samplingStart.Arguments = $samplingProfile
    $samplingStart.WorkingDirectory = $samplingDirectory
    $samplingStart.UseShellExecute = $false
    $samplingStart.CreateNoWindow = $true
    $samplingStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $samplingStart.RedirectStandardOutput = $true
    $samplingStart.RedirectStandardError = $true
    $samplingProcess = New-Object Diagnostics.Process
    $samplingProcess.StartInfo = $samplingStart
    try {
        $null = $samplingProcess.Start()
        $samplingStdout = $samplingProcess.StandardOutput.ReadToEndAsync()
        $samplingStderr = $samplingProcess.StandardError.ReadToEndAsync()
        if (-not $samplingProcess.WaitForExit(25000)) { $samplingProcess.Kill(); $samplingProcess.WaitForExit(); throw 'Owned collector research exceeded 25 seconds' }
        $samplingProcess.WaitForExit()
        $samplingText = $samplingStdout.Result + $samplingStderr.Result
        [IO.File]::WriteAllText((Join-Path $samplingDirectory 'results.txt'),$samplingText)
        Write-Output $samplingText
        $null = $samplingAllResults.AppendLine($samplingText)
        [IO.File]::WriteAllText((Join-Path $samplingOutput 'results.txt'),$samplingAllResults.ToString())
        if ($samplingProcess.ExitCode -ne 0 -or $samplingStderr.Result.Length -gt 0) { throw 'Official collector research failed' }
    } finally { $samplingProcess.Dispose() }
}
Write-Output ('OUTPUT ' + $samplingOutput)
