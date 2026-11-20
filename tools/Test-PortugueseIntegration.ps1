param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-PTBRIntegration-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
$null = New-Item -ItemType Directory -Path $output
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$sources += Join-Path $workspace 'tests/PortugueseIntegrationChecks.cs'
$rsp = Join-Path $output 'sources.rsp'
[IO.File]::WriteAllLines($rsp, [string[]]@($sources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($false)))
$exe = Join-Path $output 'checks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler -nologo -unsafe+ -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 -define:PAVISE_SELFTEST -main:PaviseApp.PortugueseIntegrationChecks ('-out:'+$exe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@'+$rsp)
if ($LASTEXITCODE -ne 0) { throw 'Failed to compile integration checks.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
