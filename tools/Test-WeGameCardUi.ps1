$ErrorActionPreference = 'Stop'
$weGameUiWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$weGameUiOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseWeGameCardUi-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $weGameUiOutput
$weGameUiSources = @(Get-ChildItem -LiteralPath (Join-Path $weGameUiWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$weGameUiSources += Join-Path $weGameUiWorkspace 'tests\WeGameCardUiChecks.cs'
$weGameUiResponse = Join-Path $weGameUiOutput 'sources.rsp'
[IO.File]::WriteAllLines($weGameUiResponse, [string[]]@($weGameUiSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$weGameUiExe = Join-Path $weGameUiOutput 'Pavise.wegame-card-ui.exe'
$weGameUiCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $weGameUiCompiler /nologo /nowarn:0649 /target:exe /platform:x64 /optimize+ /unsafe+ /codepage:65001 `
    '/define:PAVISE_SELFTEST;PAVISE_WEGAME_CARD_UI' /main:PaviseApp.WeGameCardUiChecks ('/out:' + $weGameUiExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll `
    ('@' + $weGameUiResponse) *> (Join-Path $weGameUiOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $weGameUiOutput 'compile.log'); throw 'WeGame card UI compilation failed.' }
$weGameUiStart = New-Object Diagnostics.ProcessStartInfo
$weGameUiStart.FileName = $weGameUiExe
$weGameUiStart.Arguments = '"' + $weGameUiOutput + '"'
$weGameUiStart.WorkingDirectory = $weGameUiOutput
$weGameUiStart.UseShellExecute = $false
$weGameUiStart.CreateNoWindow = $true
$weGameUiStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$weGameUiStart.RedirectStandardOutput = $true
$weGameUiStart.RedirectStandardError = $true
$weGameUiProcess = New-Object Diagnostics.Process
$weGameUiProcess.StartInfo = $weGameUiStart
try {
    $null = $weGameUiProcess.Start()
    $weGameUiStdout = $weGameUiProcess.StandardOutput.ReadToEndAsync()
    $weGameUiStderr = $weGameUiProcess.StandardError.ReadToEndAsync()
    if (-not $weGameUiProcess.WaitForExit(30000)) { $weGameUiProcess.Kill(); throw 'Owned WeGame card UI runner timed out.' }
    $weGameUiProcess.WaitForExit()
    $weGameUiResult = $weGameUiStdout.Result + $weGameUiStderr.Result
    [IO.File]::WriteAllText((Join-Path $weGameUiOutput 'result.txt'), $weGameUiResult)
    Write-Output $weGameUiResult
    if ($weGameUiProcess.ExitCode -ne 0 -or $weGameUiStderr.Result.Length -ne 0 -or $weGameUiStdout.Result -notmatch 'PASS WeGame card UI') { throw 'WeGame card UI regressions failed.' }
} finally { $weGameUiProcess.Dispose(); Write-Output ('OUTPUT ' + $weGameUiOutput) }
