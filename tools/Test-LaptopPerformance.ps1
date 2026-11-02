param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build/LaptopPerformance-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$sources += Join-Path $workspace 'tests/SelfTests.LaptopPerformance.cs'
$manifest = @($sources | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source-manifest.json') -Encoding UTF8
$response = Join-Path $output 'sources.rsp'
[IO.File]::WriteAllLines($response,[string[]]@($sources | ForEach-Object { '"' + $_ + '"' }),(New-Object Text.UTF8Encoding($true)))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $output 'LaptopPerformanceTests.exe'
& $compiler -unsafe+ /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /nowarn:0649,0414,0675,1718 '/define:PAVISE_SELFTEST;PAVISE_LAPTOP_TEST_RUNNER' /main:PaviseApp.SelfTests ('/out:' + $exe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll ('@' + $response) *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'compile.log') -Tail 25; throw 'Laptop regression compile failed.' }
& $exe *> (Join-Path $output 'result.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'result.log'); throw 'Laptop regression tests failed.' }
Get-Content -LiteralPath (Join-Path $output 'result.log')
$changed = @($manifest | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
if ($changed.Count -gt 0) { throw 'Sources changed during the check; rerun against final sources.' }
Write-Output ('OUTPUT ' + $output)