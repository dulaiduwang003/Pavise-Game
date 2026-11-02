$ErrorActionPreference = 'Stop'
$gpuRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$gpuOutput = Join-Path $gpuRepo 'build\candidate-lab-20261005'
$gpuCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$gpuSources = @(Get-ChildItem -LiteralPath (Join-Path $gpuRepo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$gpuSources += Join-Path $PSScriptRoot 'GpuPriorityProbe.cs'
$gpuExe = Join-Path $gpuOutput 'GpuPriorityProbe.exe'
& $gpuCompiler -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_GPU_PRIORITY_PROBE' -main:PaviseApp.GpuPriorityProbe "-out:$gpuExe" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Web.Extensions.dll $gpuSources
if ($LASTEXITCODE -ne 0) { throw 'GPU priority probe compilation failed.' }
$gpuLines = & $gpuExe 2>&1
$gpuExit = $LASTEXITCODE
$gpuLines | Set-Content -LiteralPath (Join-Path $gpuOutput 'gpu-priority-probe.json') -Encoding UTF8
$gpuLines
if ($gpuExit -ne 0) { throw 'GPU priority probe failed to restore.' }
