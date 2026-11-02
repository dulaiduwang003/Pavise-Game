$ErrorActionPreference = 'Stop'
$candidateRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$candidateOutput = Join-Path $candidateRepo ('build\memory-native-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $candidateOutput
$candidateCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$candidateSources = @(Get-ChildItem -LiteralPath (Join-Path $candidateRepo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$candidateSources += Join-Path $candidateRepo 'tests\FrameMemoryNativeRunner.cs'
$candidateSources += Join-Path $candidateRepo 'tests\SelfTests.ReleaseNotes.cs'
$candidateExe = Join-Path $candidateOutput 'FrameMemoryNative.exe'
& $candidateCompiler -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_MEMORY_NATIVE' -main:PaviseApp.FrameMemoryNativeRunner "-out:$candidateExe" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll $candidateSources
if ($LASTEXITCODE -ne 0) { throw 'Native memory regression compilation failed.' }
$candidateLines = & $candidateExe 2>&1
$candidateExit = $LASTEXITCODE
$candidateLines | Set-Content -LiteralPath (Join-Path $candidateOutput 'result.txt') -Encoding UTF8
$candidateLines
"RESULT_DIRECTORY=$candidateOutput"
if ($candidateExit -ne 0) { throw 'Native memory regression failed.' }
