param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$strengthRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $strengthRoot ('build/SuppressionStrength-' + [Guid]::NewGuid().ToString('N')) }
$strengthOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force -Path $strengthOutput
$strengthSources = @(Get-ChildItem -LiteralPath (Join-Path $strengthRoot 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$strengthSources += Join-Path $strengthRoot 'tests/SelfTests.AntiCheatThrottle.cs'
$strengthSources += Join-Path $strengthRoot 'tests/SuppressionStrengthRunner.cs'
$strengthManifest = @($strengthSources | ForEach-Object { [pscustomobject]@{Path=$_; SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$strengthManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $strengthOutput 'source-manifest.json') -Encoding UTF8
$strengthResponse = Join-Path $strengthOutput 'sources.rsp'
[IO.File]::WriteAllLines($strengthResponse, [string[]]@($strengthSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$strengthExe = Join-Path $strengthOutput 'Pavise.SuppressionStrength.exe'
$strengthCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $strengthCompiler -nologo -target:exe -platform:x64 -unsafe+ -optimize+ -codepage:65001 -nowarn:0649 `
    '-define:PAVISE_SELFTEST;PAVISE_SUPPRESSION_STRENGTH_CHECKS' '-main:PaviseApp.SelfTests' ('-out:' + $strengthExe) `
    -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll `
    -reference:System.Management.dll -reference:System.Xml.dll ('@' + $strengthResponse) *> (Join-Path $strengthOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $strengthOutput 'compile.log') -Tail 20; throw 'Suppression strength compilation failed.' }
$strengthStart = New-Object Diagnostics.ProcessStartInfo
$strengthStart.FileName = $strengthExe
$strengthStart.UseShellExecute = $false
$strengthStart.CreateNoWindow = $true
$strengthStart.WindowStyle = 'Hidden'
$strengthStart.RedirectStandardOutput = $true
$strengthStart.RedirectStandardError = $true
$strengthProcess = [Diagnostics.Process]::Start($strengthStart)
try {
    $strengthStdout = $strengthProcess.StandardOutput.ReadToEndAsync()
    $strengthStderr = $strengthProcess.StandardError.ReadToEndAsync()
    if (-not $strengthProcess.WaitForExit(15000)) { $strengthProcess.Kill(); throw 'Suppression strength checks timed out.' }
    $strengthProcess.WaitForExit()
    $strengthResult = $strengthStdout.Result + $strengthStderr.Result
    [IO.File]::WriteAllText((Join-Path $strengthOutput 'result.log'), $strengthResult)
    Write-Output $strengthResult
    if ($strengthProcess.ExitCode -ne 0) { throw 'Suppression strength checks failed.' }
} finally { $strengthProcess.Dispose() }
foreach ($strengthItem in $strengthManifest) {
    if ((Get-FileHash -LiteralPath $strengthItem.Path -Algorithm SHA256).Hash -ne $strengthItem.SHA256) { throw 'Sources changed during checks.' }
}
Write-Output ('OUTPUT ' + $strengthOutput)
