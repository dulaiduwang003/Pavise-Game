param([string]$OutputDirectory = '', [switch]$CoreAudit)
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-LaboratoryPower-' + [Guid]::NewGuid().ToString('N')) }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $taskOutput -Force
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskExe = Join-Path $taskOutput 'Pavise.laboratory-power-checks.exe'
$taskSources = @((Join-Path $taskWorkspace 'src/PaviseApp/LaboratoryPower.cs'), (Join-Path $taskWorkspace 'tests/LaboratoryPowerChecks.cs'))
$taskDefines = '-define:PAVISE_LAB_POWER_CHECKS;PAVISE_LAB_POWER_ISOLATED'
if ($CoreAudit) {
    $taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
    $taskSources += Join-Path $taskWorkspace 'tests/LaboratoryPowerChecks.cs'
    $taskDefines = '-define:PAVISE_SELFTEST;PAVISE_LAB_POWER_CHECKS;PAVISE_LAB_POWER_CORE_AUDIT'
}
$taskResponse = Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse, [string[]]@($taskSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
& $taskCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 $taskDefines -main:PaviseApp.LaboratoryPowerChecks ('-out:' + $taskExe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@' + $taskResponse) *> (Join-Path $taskOutput 'compile.txt')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskOutput 'compile.txt'); throw 'Laboratory power isolated regression build failed.' }
$taskStart = New-Object Diagnostics.ProcessStartInfo
$taskStart.FileName = $taskExe
$taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardOutput = $true; $taskStart.RedirectStandardError = $true
$taskProcess = New-Object Diagnostics.Process
$taskProcess.StartInfo = $taskStart
try {
    $null = $taskProcess.Start()
    $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync(); $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(30000)) { $taskProcess.Kill(); throw 'Laboratory power regression timed out.' }
    $taskProcess.WaitForExit()
    $taskResult = $taskStdout.Result + $taskStderr.Result
    [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'), $taskResult)
    Write-Output $taskResult
    if ($taskProcess.ExitCode -ne 0) { throw 'Laboratory power regression failed.' }
} finally { $taskProcess.Dispose(); Write-Output ('OUTPUT ' + $taskOutput) }
