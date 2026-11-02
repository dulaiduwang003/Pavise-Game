$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'build\Pavise.anticheat-checks.exe'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    throw '.NET Framework 4.x x64 compiler not found.'
}
[void](New-Item -ItemType Directory -Force -Path (Join-Path $taskRoot 'build'))
$taskArguments = @(
    '-nologo', '-target:exe', '-platform:x64', '-optimize+', '-codepage:65001',
    '-nowarn:0649',
    '-define:PAVISE_SELFTEST;PAVISE_ANTICHEAT_CHECKS', '-main:PaviseApp.AntiCheatChecks',
    "-out:$taskOutput", '-reference:System.dll', '-reference:System.Drawing.dll',
    '-reference:System.Windows.Forms.dll', '-reference:System.Core.dll',
    '-reference:System.Management.dll', '-reference:System.Xml.dll',
    "-recurse:$taskRoot\src\*.cs"
)
foreach ($taskSource in @('AntiCheatChecks.cs', 'SelfTests.AntiCheatCatalog.cs',
    'SelfTests.AntiCheatThrottle.cs', 'SelfTests.HeavySqueeze.cs', 'SelfTests.RendererRelease.cs')) {
    $taskArguments += Join-Path $PSScriptRoot $taskSource
}
& $taskCompiler -unsafe+ @taskArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $taskOutput
exit $LASTEXITCODE
