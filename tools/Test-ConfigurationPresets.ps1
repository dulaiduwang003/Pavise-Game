param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskWorkspace 'build/ConfigurationPresetChecks' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskExe = Join-Path $taskOutput 'ConfigurationPresetChecks.exe'
$taskSources = @('src/PaviseApp/ConfigurationPresetCatalog.cs','src/PaviseApp/ConfigurationPresetCoordinator.cs','src/PaviseApp/ConfigurationPresetRuntime.cs','tests/ConfigurationPresetChecks.cs') | ForEach-Object { '"' + (Join-Path $taskWorkspace $_) + '"' }
$taskResponse = Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse, [string[]]$taskSources, [Text.UTF8Encoding]::new($true))
& $taskCompiler -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 ('-out:'+$taskExe) -reference:System.dll -reference:System.Core.dll ('@'+$taskResponse) *> (Join-Path $taskOutput 'compile.txt')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskOutput 'compile.txt'); throw 'Preset fake-backend compilation failed' }
$taskStart = [Diagnostics.ProcessStartInfo]::new($taskExe)
$taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true
$taskStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$taskStart.RedirectStandardOutput = $true; $taskStart.RedirectStandardError = $true
$taskProcess = [Diagnostics.Process]::new(); $taskProcess.StartInfo = $taskStart
try {
    $taskProcess.Start() | Out-Null
    $taskOut = $taskProcess.StandardOutput.ReadToEndAsync(); $taskErr = $taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(20000)) { $taskProcess.Kill(); throw 'Preset fake checks timed out' }
    $text = $taskOut.Result + $taskErr.Result
    [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'), $text)
    Write-Output $text
    if ($taskProcess.ExitCode -ne 0) { throw 'Preset fake checks failed' }
} finally { $taskProcess.Dispose() }
