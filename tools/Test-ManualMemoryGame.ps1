param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$memoryGameRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $memoryGameRoot ('build/ManualMemoryGame-' + [Guid]::NewGuid().ToString('N')) }
$memoryGameOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force -Path $memoryGameOutput
$memoryGameSources = @(Get-ChildItem -LiteralPath (Join-Path $memoryGameRoot 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$memoryGameSources += Join-Path $memoryGameRoot 'tests/Official3106/ManualMemoryGameChecks.cs'
$memoryGameManifest = @($memoryGameSources | ForEach-Object { [pscustomobject]@{Path=$_; SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$memoryGameManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $memoryGameOutput 'source-manifest.json') -Encoding UTF8
$memoryGameResponse = Join-Path $memoryGameOutput 'sources.rsp'
[IO.File]::WriteAllLines($memoryGameResponse, [string[]]@($memoryGameSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$memoryGameExe = Join-Path $memoryGameOutput 'Pavise.ManualMemoryGame.exe'
$memoryGameCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $memoryGameCompiler -nologo -target:exe -platform:x64 -unsafe+ -optimize+ -codepage:65001 -nowarn:0649 `
    '-define:PAVISE_SELFTEST;PAVISE_SUPPRESSION_STRENGTH_CHECKS' '-main:PaviseApp.ManualMemoryGameChecks' ('-out:' + $memoryGameExe) `
    -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll `
    -reference:System.Management.dll -reference:System.Xml.dll ('@' + $memoryGameResponse) *> (Join-Path $memoryGameOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $memoryGameOutput 'compile.log') -Tail 20; throw 'Suppression memoryGame compilation failed.' }
$memoryGameStart = New-Object Diagnostics.ProcessStartInfo
$memoryGameStart.FileName = $memoryGameExe
$memoryGameStart.UseShellExecute = $false
$memoryGameStart.CreateNoWindow = $true
$memoryGameStart.WindowStyle = 'Hidden'
$memoryGameStart.RedirectStandardOutput = $true
$memoryGameStart.RedirectStandardError = $true
$memoryGameProcess = [Diagnostics.Process]::Start($memoryGameStart)
try {
    $memoryGameStdout = $memoryGameProcess.StandardOutput.ReadToEndAsync()
    $memoryGameStderr = $memoryGameProcess.StandardError.ReadToEndAsync()
    if (-not $memoryGameProcess.WaitForExit(15000)) { $memoryGameProcess.Kill(); throw 'Suppression memoryGame checks timed out.' }
    $memoryGameProcess.WaitForExit()
    $memoryGameResult = $memoryGameStdout.Result + $memoryGameStderr.Result
    [IO.File]::WriteAllText((Join-Path $memoryGameOutput 'result.log'), $memoryGameResult)
    Write-Output $memoryGameResult
    if ($memoryGameProcess.ExitCode -ne 0) { throw 'Suppression memoryGame checks failed.' }
} finally { $memoryGameProcess.Dispose() }
foreach ($memoryGameItem in $memoryGameManifest) {
    if ((Get-FileHash -LiteralPath $memoryGameItem.Path -Algorithm SHA256).Hash -ne $memoryGameItem.SHA256) { throw 'Sources changed during checks.' }
}
Write-Output ('OUTPUT ' + $memoryGameOutput)
