$ErrorActionPreference = 'Stop'
$scopeWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$scopeOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseGameExtensionScope-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scopeOutput
$scopeSources = @(Get-ChildItem -LiteralPath (Join-Path $scopeWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$scopeSources += Join-Path $scopeWorkspace 'tests\SelfTests.GameExtensionScope.cs'
$scopeSources += Join-Path $scopeWorkspace 'tests\GameExtensionScopeRunner.cs'
$scopeResponse = Join-Path $scopeOutput 'sources.rsp'
[IO.File]::WriteAllLines($scopeResponse, [string[]]@($scopeSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$scopeExe = Join-Path $scopeOutput 'Pavise.game-extension-scope.exe'
$scopeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $scopeCompiler /nologo /nowarn:0649 /target:exe /platform:x64 /optimize+ /unsafe+ /codepage:65001 `
    '/define:PAVISE_SELFTEST;PAVISE_GAME_EXTENSION_SCOPE_RUNNER' /main:PaviseApp.SelfTests ('/out:' + $scopeExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll `
    ('@' + $scopeResponse) *> (Join-Path $scopeOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $scopeOutput 'compile.log'); throw 'Extension scope compilation failed.' }
$scopeStart = New-Object Diagnostics.ProcessStartInfo
$scopeStart.FileName = $scopeExe; $scopeStart.UseShellExecute = $false; $scopeStart.CreateNoWindow = $true
$scopeStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$scopeStart.RedirectStandardOutput = $true; $scopeStart.RedirectStandardError = $true
$scopeProcess = New-Object Diagnostics.Process
$scopeProcess.StartInfo = $scopeStart
try {
    $null = $scopeProcess.Start()
    $scopeStdout = $scopeProcess.StandardOutput.ReadToEndAsync(); $scopeStderr = $scopeProcess.StandardError.ReadToEndAsync()
    if (-not $scopeProcess.WaitForExit(10000)) { $scopeProcess.Kill(); throw 'Owned extension scope runner timed out.' }
    $scopeProcess.WaitForExit()
    $scopeResult = $scopeStdout.Result + $scopeStderr.Result
    [IO.File]::WriteAllText((Join-Path $scopeOutput 'result.txt'), $scopeResult)
    Write-Output $scopeResult
    if ($scopeProcess.ExitCode -ne 0) { throw 'Extension scope regressions failed.' }
} finally { $scopeProcess.Dispose(); Write-Output ('OUTPUT ' + $scopeOutput) }
