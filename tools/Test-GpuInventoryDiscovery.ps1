param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$inventoryRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $inventoryRoot ('build/GpuInventoryDiscovery-' + [Guid]::NewGuid().ToString('N')) }
$inventoryOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $inventoryOutput -Force
$inventoryInputs = @((Join-Path $inventoryRoot 'src/PaviseApp/GpuInventoryDiscovery.cs'),(Join-Path $inventoryRoot 'tests/Official3106/GpuInventoryDiscoveryChecks.cs'))
$inventoryManifest = @($inventoryInputs | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$inventoryManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inventoryOutput 'inputs.json') -Encoding UTF8
$inventoryResponse = Join-Path $inventoryOutput 'sources.rsp'
[IO.File]::WriteAllLines($inventoryResponse,[string[]]@($inventoryInputs | ForEach-Object { '"'+$_+'"' }),(New-Object Text.UTF8Encoding($true)))
$inventoryCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$inventoryExe = Join-Path $inventoryOutput 'GpuInventoryDiscoveryChecks.exe'
& $inventoryCompiler -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 ('-out:'+$inventoryExe) -reference:System.dll -reference:System.Core.dll ('@'+$inventoryResponse) *> (Join-Path $inventoryOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $inventoryOutput 'compile.log') -Tail 20; throw 'GPU discovery compilation failed.' }
& $inventoryExe *> (Join-Path $inventoryOutput 'result.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $inventoryOutput 'result.log'); throw 'GPU discovery checks failed.' }
Get-Content -LiteralPath (Join-Path $inventoryOutput 'result.log')
foreach($inventoryInput in $inventoryManifest) { if((Get-FileHash -LiteralPath $inventoryInput.Path -Algorithm SHA256).Hash -ne $inventoryInput.SHA256) { throw 'Inputs changed during GPU discovery checks.' } }
Write-Output ('OUTPUT '+$inventoryOutput)
