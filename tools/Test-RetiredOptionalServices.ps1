param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$serviceWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('PaviseRetiredServices-' + [Guid]::NewGuid().ToString('N')) }
$serviceOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force -Path $serviceOutput
$serviceCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$serviceExe = Join-Path $serviceOutput 'RetiredOptionalServices.exe'
$serviceSources = @(Get-ChildItem -LiteralPath (Join-Path $serviceWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$serviceSources += Join-Path $serviceWorkspace 'tests/RetiredOptionalServiceChecks.cs'
$serviceResponse = Join-Path $serviceOutput 'sources.rsp'
[IO.File]::WriteAllLines($serviceResponse, [string[]]@($serviceSources | ForEach-Object { '"' + $_ + '"' }), [Text.UTF8Encoding]::new($true))
& $serviceCompiler -nologo -target:exe -platform:x64 -unsafe+ -optimize+ -codepage:65001 -warn:0 `
    '-define:PAVISE_PERFLAB;PAVISE_RETIRED_OPTIONAL_SERVICES_CHECKS' -main:PaviseApp.RetiredOptionalServiceChecks ('-out:' + $serviceExe) `
    -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll `
    -reference:System.Management.dll -reference:System.Xml.dll ('@' + $serviceResponse) *> (Join-Path $serviceOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $serviceOutput 'compile.log'); throw 'Retired service production-branch compilation failed' }
$serviceStart = [Diagnostics.ProcessStartInfo]::new()
$serviceStart.FileName = $serviceExe; $serviceStart.WorkingDirectory = $serviceOutput
$serviceStart.UseShellExecute = $false; $serviceStart.CreateNoWindow = $true
$serviceStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$serviceStart.RedirectStandardOutput = $true; $serviceStart.RedirectStandardError = $true
$serviceProcess = [Diagnostics.Process]::new(); $serviceProcess.StartInfo = $serviceStart
try {
    $null = $serviceProcess.Start()
    $serviceOut = $serviceProcess.StandardOutput.ReadToEndAsync(); $serviceErr = $serviceProcess.StandardError.ReadToEndAsync()
    if (-not $serviceProcess.WaitForExit(15000)) { $serviceProcess.Kill(); throw 'Owned retired service check timed out' }
    $serviceProcess.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $serviceOutput 'stdout.log'), $serviceOut.Result)
    [IO.File]::WriteAllText((Join-Path $serviceOutput 'stderr.log'), $serviceErr.Result)
    Write-Output $serviceOut.Result; Write-Output $serviceErr.Result
    if ($serviceProcess.ExitCode -ne 0) { throw 'Retired service activation check failed' }
} finally { $serviceProcess.Dispose(); Write-Output ('OUTPUT ' + $serviceOutput) }
