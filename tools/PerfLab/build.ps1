param(
    [string]$OutputDirectory = "$env:TEMP\Aegis-PerfLab-bin"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw "csc.exe was not found"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$references = @(
    "-reference:System.dll",
    "-reference:System.Core.dll",
    "-reference:System.Drawing.dll",
    "-reference:System.Windows.Forms.dll",
    "-reference:System.Management.dll",
    "-reference:System.Xml.dll"
)

& $compiler -nologo -target:winexe -optimize+ -codepage:65001 `
    "-win32manifest:$PSScriptRoot\PerfLab.manifest" `
    "-out:$OutputDirectory\Aegis.PerfLab.exe" `
    $references `
    "$PSScriptRoot\PerfLab.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$sources = Get-ChildItem -LiteralPath (Join-Path $root "src") -Recurse -Filter *.cs |
    ForEach-Object { $_.FullName }
& $compiler -nologo -target:exe -optimize+ -codepage:65001 `
    -define:AEGIS_PERFLAB `
    -main:AegisApp.PerfEngineProgram `
    "-win32manifest:$PSScriptRoot\PerfLab.manifest" `
    "-out:$OutputDirectory\Aegis.PerfEngine.exe" `
    $references `
    $sources `
    "$PSScriptRoot\PerfEngine.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output "$OutputDirectory\Aegis.PerfLab.exe"
Write-Output "$OutputDirectory\Aegis.PerfEngine.exe"
