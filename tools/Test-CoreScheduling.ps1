param([switch]$LiveIsolation)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
Push-Location $workspace
try {
    if ($LiveIsolation) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Live isolation changes a temporary system CPU range. Run this script from an administrator PowerShell for -LiveIsolation.'
        }
    }
    & .\build.cmd -b dev build\Pavise.selftest.core.exe --selftest
    if ($LASTEXITCODE -ne 0) { throw 'Self-test build failed.' }
    & .\build\Pavise.selftest.core.exe --selftest build\core-scheduling-selftest.txt
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed; see build\core-scheduling-selftest.txt.' }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $common = @('-nologo', '-target:exe', '-platform:x64', '-optimize+', '-codepage:65001',
        '-define:PAVISE_SELFTEST;PAVISE_SELFTEST_RUNNER', '-reference:System.dll',
        '-reference:System.Drawing.dll', '-reference:System.Windows.Forms.dll', '-reference:System.Core.dll',
        '-reference:System.Management.dll', '-reference:System.Xml.dll', '-recurse:src\*.cs', '-recurse:tests\*.cs')
    & $compiler '-main:PaviseApp.CoreSchedulingIntegrationRunner' '-out:build\Pavise.core-integration.exe' @common
    if ($LASTEXITCODE -ne 0) { throw 'Placement integration build failed.' }
    & .\build\Pavise.core-integration.exe --integration *> build\core-scheduling-integration.txt
    if ($LASTEXITCODE -ne 0) { throw 'Placement integration failed; see build\core-scheduling-integration.txt.' }
    if ($LiveIsolation) {
        & $compiler '-main:PaviseApp.CoreIsolationIntegrationRunner' '-out:build\Pavise.isolation-integration.exe' @common
        if ($LASTEXITCODE -ne 0) { throw 'Isolation integration build failed.' }
        & .\build\Pavise.isolation-integration.exe --integration
        if ($LASTEXITCODE -ne 0) { throw 'Isolation integration failed; see build\core-isolation-integration.txt.' }
        Get-Content build\core-isolation-integration.txt
    }
    Get-Content build\core-scheduling-selftest.txt -Tail 1
    Get-Content build\core-scheduling-integration.txt
} finally { Pop-Location }
