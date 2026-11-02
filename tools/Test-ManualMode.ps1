param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Compile-Manual.ps1') -OutputDirectory $OutputDirectory -Defines 'PAVISE_SELFTEST;PAVISE_MANUAL_MODE_CHECKS;PAVISE_PERFORMANCE_PAGE_CHECKS' -Target exe -Main 'PaviseApp.ManualModeChecks' -Tests @('ManualModeChecks.cs','PerformanceWorkspaceChecks.cs')
$output=[IO.Path]::GetFullPath($OutputDirectory)
$data=Join-Path $output 'data'; $null=New-Item -ItemType Directory -Path $data
$info=New-Object Diagnostics.ProcessStartInfo
$info.FileName=Join-Path $output 'candidate.exe'; $info.Arguments='"'+$data+'"'
$info.WorkingDirectory=$output; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
$info.EnvironmentVariables['TEMP']=Join-Path $output 'temp'; $info.EnvironmentVariables['TMP']=Join-Path $output 'temp'
$process=[Diagnostics.Process]::Start($info)
try {
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(90000)) { $process.Kill(); $process.WaitForExit(); throw 'Isolated manual UI check timed out' }
    $process.WaitForExit(); $log=$stdout.Result+$stderr.Result; $exit=$process.ExitCode
} finally { $process.Dispose() }
[IO.File]::WriteAllText((Join-Path $output 'checks.log'),$log,(New-Object Text.UTF8Encoding($false)))
if($exit -ne 0 -or $log -notmatch 'PASS manual-only checks=') { throw ('Manual-mode checks failed; inspect '+(Join-Path $output 'checks.log')) }
Write-Output $log
