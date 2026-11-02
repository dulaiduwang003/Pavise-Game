$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$output = Join-Path ([IO.Path]::GetTempPath()) ('PaviseLayoutTests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $output
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $workspace 'tests\SelfTests.AutomaticLayout.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $output 'AutomaticLayoutTests.exe'
& $compiler -unsafe+ /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 '/define:PAVISE_SELFTEST;PAVISE_LAYOUT_TEST_RUNNER' /main:PaviseApp.SelfTests ('/out:' + $exe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll @sources *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'compile.log'); throw 'Automatic layout compile failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Automatic layout regression tests failed.' }
Write-Output ('OUTPUT ' + $output)
