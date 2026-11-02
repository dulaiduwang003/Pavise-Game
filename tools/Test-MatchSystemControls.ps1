param()
$ErrorActionPreference = 'Stop'
$matchWorkspace = Split-Path $PSScriptRoot -Parent
$matchOutput = Join-Path $env:TEMP ('Pavise-MatchSystemControls-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $matchOutput
Push-Location $matchWorkspace
try {
    $matchCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $matchExe = Join-Path $matchOutput 'Pavise.match-check.exe'
    & $matchCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_MATCH_SYSTEM_CONTROLS' -main:PaviseApp.MatchSystemControlsChecks ('-out:' + $matchExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\MatchSystemControlsChecks.cs
    if ($LASTEXITCODE -ne 0) { throw 'Match controls compilation failed' }
    $matchStart = New-Object Diagnostics.ProcessStartInfo
    $matchStart.FileName = $matchExe
    $matchStart.UseShellExecute = $false
    $matchStart.CreateNoWindow = $true
    $matchStart.RedirectStandardOutput = $true
    $matchStart.RedirectStandardError = $true
    $matchProcess = New-Object Diagnostics.Process
    $matchProcess.StartInfo = $matchStart
    try {
        $null = $matchProcess.Start()
        $matchStdout = $matchProcess.StandardOutput.ReadToEndAsync()
        $matchStderr = $matchProcess.StandardError.ReadToEndAsync()
        if (!$matchProcess.WaitForExit(45000)) { $matchProcess.Kill(); throw 'Owned match runner timed out' }
        $matchText = $matchStdout.Result + $matchStderr.Result
        [IO.File]::WriteAllText((Join-Path $matchOutput 'result.txt'), $matchText)
        Write-Output $matchText
        Write-Output ('OUTPUT ' + $matchOutput)
        if ($matchProcess.ExitCode -ne 0) { throw 'Match controls regression failed' }
    } finally { $matchProcess.Dispose() }
} finally { Pop-Location }
