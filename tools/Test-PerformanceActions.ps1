param([switch]$EnvironmentQuantumOnly, [switch]$VisualEffectsOnly)
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = Join-Path ([IO.Path]::GetTempPath()) ('PavisePerformanceActions-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskOutput
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src') -Recurse -Filter '*.cs' | ForEach-Object { $_.FullName })
foreach ($taskSource in @('SelfTests.PerformanceActions.cs', 'SelfTests.MatchVisualEffects.cs', 'SelfTests.EnvironmentBundleLayout.cs', 'SelfTests.FullscreenOptimizations.cs', 'PerformanceActionRegressionRunner.cs')) {
    $taskSources += Join-Path $taskWorkspace ('tests\' + $taskSource)
}
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExe = Join-Path $taskOutput 'PerformanceActionsTests.exe'
$taskResponse = Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse, [string[]]@($taskSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
& $taskCompiler -unsafe+ /nologo /nowarn:0649 /target:exe /platform:x64 /optimize+ /codepage:65001 '/define:PAVISE_SELFTEST;PAVISE_PERFORMANCE_ACTION_RUNNER' /main:PaviseApp.SelfTests ('/out:' + $taskExe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll ('@' + $taskResponse) *> (Join-Path $taskOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskOutput 'compile.log'); throw 'Performance action test compilation failed' }
if ($VisualEffectsOnly) { & $taskExe --visual-effects } elseif ($EnvironmentQuantumOnly) { & $taskExe --environment-quantum } else { & $taskExe }
if ($LASTEXITCODE -ne 0) { throw 'Performance action tests failed' }
Write-Output ('OUTPUT ' + $taskOutput)
