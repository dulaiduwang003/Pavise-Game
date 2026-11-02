$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path $workspace 'build\core-animation-preview'
$null = New-Item -ItemType Directory -Force -Path $output
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $workspace 'tests\CoreAnimationVisualChecks.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $output 'CoreAnimationVisualChecks.exe'
& $compiler -unsafe+ /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /define:PAVISE_PERFLAB /main:PaviseApp.CoreAnimationVisualChecks ('/out:' + $exe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll @sources *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'compile.log'); throw 'Animation bench compile failed' }
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.Arguments = '"' + $output + '"'
$start.WorkingDirectory = $output
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
$null = $process.Start()
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Animation bench timed out' }
    $process.WaitForExit()
    $stdout.Result | Set-Content -LiteralPath (Join-Path $output 'checks.log') -Encoding UTF8
    $stderr.Result | Set-Content -LiteralPath (Join-Path $output 'stderr.log') -Encoding UTF8
    Write-Output $stdout.Result
    Write-Output $stderr.Result
    if ($process.ExitCode -ne 0) { throw 'Animation bench failed' }
} finally { $process.Dispose() }
