$ErrorActionPreference = 'Stop'
$yieldRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$yieldOutput = Join-Path $yieldRepo ('build\self-yield-checks-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $yieldOutput
$yieldCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$yieldSources = @(Get-ChildItem -LiteralPath (Join-Path $yieldRepo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$yieldSources += Join-Path $PSScriptRoot 'Check-SelfYieldHelpers.cs'
$yieldExe = Join-Path $yieldOutput 'SelfYieldHelperChecks.exe'
& $yieldCompiler -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_HELPER_CHECKS' -main:PaviseApp.SelfYieldHelperChecks "-out:$yieldExe" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll $yieldSources
if ($LASTEXITCODE -ne 0) { throw 'Own-helper regression compilation failed.' }
$yieldLines = & $yieldExe 2>&1
$yieldExit = $LASTEXITCODE
$yieldLines | Set-Content -LiteralPath (Join-Path $yieldOutput 'result.txt') -Encoding UTF8
$yieldLines
"RESULT_DIRECTORY=$yieldOutput"
if ($yieldExit -ne 0) { throw 'Own-helper regression failed.' }
