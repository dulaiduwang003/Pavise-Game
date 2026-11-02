$ErrorActionPreference = 'Stop'
$crashRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$crashOutput = Join-Path $crashRepo ('build\memory-crash-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $crashOutput
$crashCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$crashSources = @(Get-ChildItem -LiteralPath (Join-Path $crashRepo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$crashSources += Join-Path $PSScriptRoot 'CrashMemoryRecovery.cs'
$crashExe = Join-Path $crashOutput 'MemoryCrashRecovery.exe'
& $crashCompiler -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_MEMORY_CRASH' -main:PaviseApp.CrashMemoryRecovery "-out:$crashExe" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll $crashSources
if ($LASTEXITCODE -ne 0) { throw 'Cross-process crash regression compilation failed.' }
$crashLines = & $crashExe 2>&1
$crashExit = $LASTEXITCODE
$crashLines | Set-Content -LiteralPath (Join-Path $crashOutput 'result.txt') -Encoding UTF8
$crashLines
"RESULT_DIRECTORY=$crashOutput"
if ($crashExit -ne 0) { throw 'Cross-process crash regression failed.' }
