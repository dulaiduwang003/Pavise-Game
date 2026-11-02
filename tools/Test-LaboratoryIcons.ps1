param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build\LaboratoryIcons-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
$source = Join-Path $workspace 'src\PaviseApp\PerformanceFeatureIcons.cs'
$checks = Join-Path $workspace 'tests\Official3106\LaboratoryIconChecks.cs'
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$exe = Join-Path $output 'LaboratoryIconChecks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler -nologo -target:exe -codepage:65001 -optimize+ "-out:$exe" -reference:System.dll -reference:System.Drawing.dll $source $checks
if ($LASTEXITCODE -ne 0) { throw 'Laboratory vector icon fixture did not compile' }
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $exe; $info.Arguments = '"' + $output + '"'
$info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'
$info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
$child = New-Object Diagnostics.Process
$child.StartInfo = $info
try {
    $null = $child.Start()
    $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(30000)) { $child.Kill(); throw 'Laboratory vector icon fixture timed out' }
    $child.WaitForExit()
    $result = $stdout.Result + $stderr.Result
    [IO.File]::WriteAllText((Join-Path $output 'result.txt'), $result)
    Write-Output $result
    if ($child.ExitCode -ne 0) { throw 'Laboratory vector icon fixture failed' }
} finally { $child.Dispose() }
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before) { throw 'Icon source changed during the validation run' }
Write-Output ('OUTPUT ' + $output)
