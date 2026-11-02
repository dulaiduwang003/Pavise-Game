param()
$ErrorActionPreference = 'Stop'
$awakeWorkspace = Split-Path $PSScriptRoot -Parent
$awakeOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseDisplayAwake-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $awakeOutput
Push-Location $awakeWorkspace
try {
    $awakeCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $awakeExe = Join-Path $awakeOutput 'Pavise.display-awake-tests.exe'
    & $awakeCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_DISPLAY_AWAKE_RUNNER' -main:PaviseApp.DisplayAwakeRegressionRunner `
        ('-out:' + $awakeExe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\DisplayAwakeRegressionRunner.cs *> (Join-Path $awakeOutput 'compile.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $awakeOutput 'compile.log'); throw 'Display-awake regression build failed.' }
    $awakeStart = New-Object Diagnostics.ProcessStartInfo
    $awakeStart.FileName = $awakeExe
    $awakeStart.Arguments = '"' + $awakeOutput + '"'
    $awakeStart.UseShellExecute = $false
    $awakeStart.CreateNoWindow = $true
    $awakeStart.RedirectStandardOutput = $true
    $awakeStart.RedirectStandardError = $true
    $awakeProcess = New-Object Diagnostics.Process
    $awakeProcess.StartInfo = $awakeStart
    try {
        $null = $awakeProcess.Start()
        $awakeStdout = $awakeProcess.StandardOutput.ReadToEndAsync()
        $awakeStderr = $awakeProcess.StandardError.ReadToEndAsync()
        if (-not $awakeProcess.WaitForExit(60000)) { $awakeProcess.Kill(); throw 'Display-awake isolated regression timed out.' }
        $awakeProcess.WaitForExit()
        $awakeResult = $awakeStdout.Result + $awakeStderr.Result
        [IO.File]::WriteAllText((Join-Path $awakeOutput 'results.txt'), $awakeResult)
        Write-Output $awakeResult
        Write-Output ('OUTPUT ' + $awakeOutput)
        if ($awakeProcess.ExitCode -ne 0) { throw 'Display-awake regression failed; inspect results.txt.' }
    } finally { $awakeProcess.Dispose() }
} finally { Pop-Location }
