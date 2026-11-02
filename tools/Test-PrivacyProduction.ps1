param([Parameter(Mandatory = $true)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$privacyExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$privacyOutput = Join-Path $env:TEMP ('PavisePrivacyProduction-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $privacyOutput
$privacyCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$privacyProbe = Join-Path $privacyOutput 'PrivacyProductionChecks.exe'
$privacySource = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/PrivacyProductionChecks.cs'
& $privacyCompiler /nologo /target:exe /platform:x64 /codepage:65001 ('/out:' + $privacyProbe) $privacySource
if ($LASTEXITCODE -ne 0) { throw 'Privacy production probe failed to compile' }
$privacyStart = New-Object Diagnostics.ProcessStartInfo
$privacyStart.FileName = $privacyProbe
$privacyStart.Arguments = '"' + $privacyExecutable + '"'
$privacyStart.UseShellExecute = $false
$privacyStart.CreateNoWindow = $true
$privacyStart.RedirectStandardOutput = $true
$privacyStart.RedirectStandardError = $true
$privacyStart.StandardOutputEncoding = [Text.Encoding]::UTF8
$privacyStart.StandardErrorEncoding = [Text.Encoding]::UTF8
$privacyProcess = New-Object Diagnostics.Process
$privacyProcess.StartInfo = $privacyStart
try {
    $null = $privacyProcess.Start()
    $privacyStdout = $privacyProcess.StandardOutput.ReadToEndAsync()
    $privacyStderr = $privacyProcess.StandardError.ReadToEndAsync()
    if (!$privacyProcess.WaitForExit(45000)) { $privacyProcess.Kill(); throw 'Privacy production probe timed out' }
    $privacyText = $privacyStdout.Result + $privacyStderr.Result
    [IO.File]::WriteAllText((Join-Path $privacyOutput 'result.txt'), $privacyText)
    Write-Output $privacyText
    if ($privacyProcess.ExitCode -ne 0) { throw 'Privacy production verification failed' }
} finally { $privacyProcess.Dispose(); Write-Output ('OUTPUT ' + $privacyOutput) }
