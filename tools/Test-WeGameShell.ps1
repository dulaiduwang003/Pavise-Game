param()
$ErrorActionPreference = 'Stop'
$shellWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$shellOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseWeGameShell-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $shellOutput
$shellSources = @(Get-ChildItem -LiteralPath (Join-Path $shellWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$shellSources += Join-Path $shellWorkspace 'tests\WeGameShellChecks.cs'
$shellResponse = Join-Path $shellOutput 'sources.rsp'
[IO.File]::WriteAllLines($shellResponse, [string[]]@($shellSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$shellExe = Join-Path $shellOutput 'Pavise.wegame-shell-checks.exe'
$shellCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $shellCompiler /nologo /nowarn:0649 /target:exe /platform:x64 /optimize+ /unsafe+ /codepage:65001 `
    '/define:PAVISE_SELFTEST;PAVISE_WEGAME_SHELL_CHECKS' /main:PaviseApp.WeGameShellChecks ('/out:' + $shellExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll `
    ('@' + $shellResponse) *> (Join-Path $shellOutput 'compile.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath (Join-Path $shellOutput 'compile.log')
    Write-Output ('OUTPUT ' + $shellOutput)
    throw 'WeGame shell model-check compilation failed.'
}
$shellStart = New-Object Diagnostics.ProcessStartInfo
$shellStart.FileName = $shellExe
$shellStart.UseShellExecute = $false
$shellStart.CreateNoWindow = $true
$shellStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$shellStart.RedirectStandardOutput = $true
$shellStart.RedirectStandardError = $true
$shellProcess = New-Object Diagnostics.Process
$shellProcess.StartInfo = $shellStart
try {
    $null = $shellProcess.Start()
    $shellStdout = $shellProcess.StandardOutput.ReadToEndAsync()
    $shellStderr = $shellProcess.StandardError.ReadToEndAsync()
    if (-not $shellProcess.WaitForExit(10000)) { $shellProcess.Kill(); throw 'Owned WeGame shell model-check runner timed out.' }
    $shellProcess.WaitForExit()
    $shellResult = $shellStdout.Result + $shellStderr.Result
    [IO.File]::WriteAllText((Join-Path $shellOutput 'result.txt'), $shellResult)
    Write-Output $shellResult
    if ($shellProcess.ExitCode -ne 0) { throw 'WeGame shell model checks failed.' }
} finally { $shellProcess.Dispose(); Write-Output ('OUTPUT ' + $shellOutput) }
