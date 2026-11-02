param()
$ErrorActionPreference = 'Stop'
$workflowWorkspace = Split-Path $PSScriptRoot -Parent
$workflowOutput = Join-Path $env:TEMP ('Pavise-WorkflowRedesign-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $workflowOutput
Push-Location $workflowWorkspace
try {
    $workflowCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $workflowExe = Join-Path $workflowOutput 'Pavise.workflow-check.exe'
    & $workflowCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_WORKFLOW_REDESIGN' -main:PaviseApp.WorkflowRedesignRegressionRunner ('-out:' + $workflowExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\WorkflowRedesignRegressionRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Workflow redesign compilation failed' }
    $workflowStart = New-Object Diagnostics.ProcessStartInfo
    $workflowStart.FileName = $workflowExe
    $workflowStart.Arguments = '"' + $workflowOutput + '"'
    $workflowStart.UseShellExecute = $false
    $workflowStart.CreateNoWindow = $true
    $workflowStart.RedirectStandardOutput = $true
    $workflowStart.RedirectStandardError = $true
    $workflowProcess = New-Object Diagnostics.Process
    $workflowProcess.StartInfo = $workflowStart
    try {
        $null = $workflowProcess.Start()
        $workflowStdout = $workflowProcess.StandardOutput.ReadToEndAsync()
        $workflowStderr = $workflowProcess.StandardError.ReadToEndAsync()
        if (!$workflowProcess.WaitForExit(45000)) { $workflowProcess.Kill(); throw 'Owned workflow runner timed out' }
        $workflowText = $workflowStdout.Result + $workflowStderr.Result
        [IO.File]::WriteAllText((Join-Path $workflowOutput 'result.txt'), $workflowText)
        Write-Output $workflowText
        Write-Output ('OUTPUT ' + $workflowOutput)
        if ($workflowProcess.ExitCode -ne 0) { throw 'Workflow redesign regression failed' }
    } finally { $workflowProcess.Dispose() }
} finally { Pop-Location }
