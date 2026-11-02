param()
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskWorkspace ('build/log-stream-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskOutput -Force
Push-Location $taskWorkspace
try {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $taskExe = Join-Path $taskOutput 'Pavise.log-stream-check.exe'
    & $taskCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_LOG_STREAM_RUNNER' `
        -main:PaviseApp.LogStreamRegressionRunner ('-out:' + $taskExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\SelfTests.LogSeverity.cs tests\SelfTests.LogWrites.cs tests\LogStreamRegressionRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Log stream regression build failed.' }
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $taskExe
    $taskStart.Arguments = '"' + $taskOutput + '"'
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    $taskProcess = New-Object Diagnostics.Process
    $taskProcess.StartInfo = $taskStart
    try {
        $null = $taskProcess.Start()
        $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); throw 'Isolated log stream regression timed out.' }
        $taskProcess.WaitForExit()
        $taskResult = $taskStdout.Result + $taskStderr.Result
        [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'), $taskResult)
        Write-Output $taskResult
        Write-Output ('OUTPUT ' + $taskOutput)
        if ($taskProcess.ExitCode -ne 0) { throw 'Log stream regression failed; inspect result.txt.' }
    } finally { $taskProcess.Dispose() }
} finally { Pop-Location }
