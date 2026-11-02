param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('build/LaptopRuntime-' + [Guid]::NewGuid().ToString('N')) }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $taskOutput -Force)
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$taskSources += Join-Path $taskRoot 'tests/LaptopPerformanceRuntimeChecks.cs'
$taskManifest = @($taskSources | ForEach-Object { [pscustomobject]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$taskManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'source-manifest.json') -Encoding UTF8
$taskExe = Join-Path $taskOutput 'LaptopRuntimeChecks.exe'
$taskResponse = Join-Path $taskOutput 'sources.rsp'
$taskArguments = @('/nologo', '/unsafe+', '/target:exe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', '/nowarn:0649,0414,0675,1718',
    '/define:PAVISE_SELFTEST;PAVISE_LAPTOP_RUNTIME_CHECKS', '/main:PaviseApp.LaptopPerformanceRuntimeChecks', ('/out:"' + $taskExe + '"'),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll', '/reference:System.Xml.dll')
$taskArguments += @($taskSources | ForEach-Object { '"' + $_ + '"' })
[IO.File]::WriteAllLines($taskResponse, [string[]]$taskArguments, (New-Object Text.UTF8Encoding($true)))
function Invoke-LaptopRuntimeHidden([string]$File, [string]$Arguments) {
    $taskInfo = New-Object Diagnostics.ProcessStartInfo
    $taskInfo.FileName = $File; $taskInfo.Arguments = $Arguments; $taskInfo.WorkingDirectory = $taskOutput
    $taskInfo.UseShellExecute = $false; $taskInfo.CreateNoWindow = $true; $taskInfo.WindowStyle = 'Hidden'
    $taskInfo.RedirectStandardOutput = $true; $taskInfo.RedirectStandardError = $true
    $taskInfo.StandardOutputEncoding = New-Object Text.UTF8Encoding($false); $taskInfo.StandardErrorEncoding = $taskInfo.StandardOutputEncoding
    $taskProcess = [Diagnostics.Process]::Start($taskInfo)
    try {
        $taskOutRead = $taskProcess.StandardOutput.ReadToEndAsync(); $taskErrRead = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(45000)) { $taskProcess.Kill(); throw 'Hidden laptop check timed out' }
        $taskProcess.WaitForExit()
        return [pscustomobject]@{ Exit = $taskProcess.ExitCode; Text = $taskOutRead.Result + $taskErrRead.Result }
    } finally { $taskProcess.Dispose() }
}
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskCompile = Invoke-LaptopRuntimeHidden $taskCompiler ('@"' + $taskResponse + '"')
[IO.File]::WriteAllText((Join-Path $taskOutput 'compile.log'), $taskCompile.Text)
if ($taskCompile.Exit -ne 0) { throw ('Laptop runtime checks did not compile: ' + $taskCompile.Text) }
foreach ($taskSuite in @('admission', 'apply', 'queued-cancel', 'write-cancel', 'external', 'no-switch', 'session-release', 'restore-order', 'restore-debt', 'shutdown-drain')) {
    $taskResult = Invoke-LaptopRuntimeHidden $taskExe ('"' + $taskOutput + '" ' + $taskSuite)
    [IO.File]::WriteAllText((Join-Path $taskOutput ($taskSuite + '.log')), $taskResult.Text)
    Write-Output $taskResult.Text
    if ($taskResult.Exit -ne 0 -or -not $taskResult.Text.Contains('PASS laptop-runtime suite=' + $taskSuite)) { throw ('Laptop runtime suite failed: ' + $taskSuite) }
}
$taskChanged = @($taskManifest | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
if ($taskChanged.Count -gt 0) { throw 'Sources changed during the check; rerun against final sources.' }
Write-Output ('OUTPUT ' + $taskOutput)
