param()
$ErrorActionPreference = 'Stop'
$privacyWorkspace = Split-Path $PSScriptRoot -Parent
$privacyOutput = Join-Path $env:TEMP ('Pavise-PrivacyNative-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $privacyOutput
$privacyCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$privacyExe = Join-Path $privacyOutput 'Pavise.privacy-native-check.exe'
Push-Location $privacyWorkspace
try {
    $privacyCompilerArgs = @('-nologo','-unsafe+','-target:exe','-platform:x64','-optimize+','-codepage:65001','-warn:0','-define:PAVISE_SELFTEST;PAVISE_PRIVACY_NATIVE_RUNNER','-main:PaviseApp.PrivacyNativeBackendChecks',('-out:' + $privacyExe),'-reference:System.dll','-reference:System.Core.dll','-reference:System.Drawing.dll','-reference:System.Windows.Forms.dll','-reference:System.Management.dll','-reference:System.Xml.dll','-recurse:src\*.cs','tests\PrivacyNativeBackendChecks.cs','tests\PrivacyServiceScopeChecks.cs')
    & $privacyCompiler @privacyCompilerArgs *> (Join-Path $privacyOutput 'compile.txt')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $privacyOutput 'compile.txt'); throw 'Privacy native checks failed to compile' }
    $privacyStart = New-Object Diagnostics.ProcessStartInfo
    $privacyStart.FileName = $privacyExe
    $privacyStart.UseShellExecute = $false; $privacyStart.CreateNoWindow = $true
    $privacyStart.RedirectStandardOutput = $true; $privacyStart.RedirectStandardError = $true
    $privacyProcess = New-Object Diagnostics.Process
    $privacyProcess.StartInfo = $privacyStart
    try {
        $null = $privacyProcess.Start()
        $privacyStdout = $privacyProcess.StandardOutput.ReadToEndAsync()
        $privacyStderr = $privacyProcess.StandardError.ReadToEndAsync()
        if (!$privacyProcess.WaitForExit(45000)) { $privacyProcess.Kill(); throw 'Owned privacy native runner timed out' }
        $privacyText = $privacyStdout.Result + $privacyStderr.Result
        [IO.File]::WriteAllText((Join-Path $privacyOutput 'result.txt'), $privacyText)
        Write-Output $privacyText
        Write-Output ('OUTPUT ' + $privacyOutput)
        if ($privacyProcess.ExitCode -ne 0) { throw 'Privacy native regression failed' }
    } finally { $privacyProcess.Dispose() }
} finally { Pop-Location }
