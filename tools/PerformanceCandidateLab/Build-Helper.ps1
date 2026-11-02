$ErrorActionPreference = 'Stop'
$helperRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$helperOutput = Join-Path $helperRepo 'build\candidate-lab-20261005'
$helperCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$helperSources = @(Get-ChildItem -LiteralPath (Join-Path $helperRepo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$helperSources += Join-Path $PSScriptRoot 'HelperContention.cs'
$helperExe = Join-Path $helperOutput 'HelperContention.exe'
& $helperCompiler -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_HELPER_CONTENTION' -main:PaviseApp.HelperContention "-out:$helperExe" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Web.Extensions.dll $helperSources
if ($LASTEXITCODE -ne 0) { throw 'Helper contention compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'HelperLoop.ps1') -Destination (Join-Path $helperOutput 'HelperLoop.ps1') -Force
"HELPER_EXE=$helperExe"
