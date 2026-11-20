param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-PTBR-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
$null = New-Item -ItemType Directory -Path $output
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src/Localization') -File -Filter 'Lang*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $workspace 'src/Game/Lol/LolLang.cs'
$sources += Join-Path $workspace 'tests/PortugueseLanguageChecks.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $output 'checks.exe'
& $compiler -nologo -codepage:65001 -target:exe ('-out:' + $exe) $sources
if ($LASTEXITCODE -ne 0) { throw 'Failed to compile PT-BR checks.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'PT-BR checks failed.' }
