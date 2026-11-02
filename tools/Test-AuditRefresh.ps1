$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 compiler not found.' }
$output = Join-Path $env:TEMP ('Pavise-AuditRefresh-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Force -Path $output)
$exe = Join-Path $output 'AuditRefreshChecks.exe'
$arguments = @(
    '-nologo', '-target:exe', '-platform:x64', '-optimize+', '-codepage:65001', '-nowarn:0649', '-unsafe+',
    '-define:PAVISE_SELFTEST;PAVISE_AUDIT_REFRESH_CHECKS', '-main:PaviseApp.AuditRefreshChecks',
    "-out:$exe", '-reference:System.dll', '-reference:System.Drawing.dll',
    '-reference:System.Windows.Forms.dll', '-reference:System.Core.dll',
    '-reference:System.Management.dll', '-reference:System.Xml.dll', '-reference:System.Security.dll',
    "-recurse:$root\src\*.cs", (Join-Path $root 'tests\AuditRefreshChecks.cs')
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Audit refresh checks failed to compile.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw "Audit refresh checks failed: $output" }
"OUTPUT $output"
