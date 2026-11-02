param([switch]$LiveIsolation, [switch]$IsolationSmoke, [switch]$ManualOnly)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent

function Invoke-HiddenTest([string]$File, [string]$Arguments, [string]$Log, [int]$TimeoutSeconds) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = [IO.Path]::GetFullPath($File)
    $info.Arguments = $Arguments
    $info.WorkingDirectory = $workspace
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        $null = $process.Start()
        try { $process.PriorityClass = 'BelowNormal' } catch { }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw ('Test exceeded its deadline: ' + $File)
        }
        $process.WaitForExit()
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($Log), $stdout.Result)
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($Log + '.stderr'), $stderr.Result)
        if ($process.ExitCode -ne 0) { throw ('Test failed: ' + $File + '; see ' + $Log) }
    } finally { $process.Dispose() }
}

Push-Location $workspace
try {
    if ($LiveIsolation -or $IsolationSmoke) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Kernel isolation requires an already elevated PowerShell. No elevation dialog was requested; no test changes were made.'
        }
    }
    $null = New-Item -ItemType Directory -Path 'build' -Force
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $common = @('-nologo', '-target:exe', '-platform:x64', '-optimize+', '-codepage:65001',
        '-define:PAVISE_SELFTEST;PAVISE_SELFTEST_RUNNER', '-reference:System.dll',
        '-reference:System.Drawing.dll', '-reference:System.Windows.Forms.dll', '-reference:System.Core.dll',
        '-reference:System.Management.dll', '-reference:System.Xml.dll', '-recurse:src\*.cs', '@tests\SelfTest.sources.rsp')
    & $compiler -unsafe+ '-main:PaviseApp.SelfTestRunner' '-out:build\Pavise.selftest.core.exe' @common *> build\core-scheduling-compile.txt
    if ($LASTEXITCODE -ne 0) { throw 'Self-test build failed; see build\core-scheduling-compile.txt.' }
    $taskSuiteArgs = if ($ManualOnly) { '--manual-cores build\core-scheduling-selftest.txt' } else { '--selftest build\core-scheduling-selftest.txt' }
    Invoke-HiddenTest 'build\Pavise.selftest.core.exe' $taskSuiteArgs 'build\core-scheduling-runner.txt' 180
    & $compiler -unsafe+ '-main:PaviseApp.CoreSchedulingIntegrationRunner' '-out:build\Pavise.core-integration.exe' @common *> build\core-scheduling-integration-compile.txt
    if ($LASTEXITCODE -ne 0) { throw 'Placement integration build failed.' }
    Invoke-HiddenTest 'build\Pavise.core-integration.exe' '--integration' 'build\core-scheduling-integration.txt' 45
    if ($LiveIsolation -or $IsolationSmoke) {
        & $compiler -unsafe+ '-main:PaviseApp.CoreIsolationIntegrationRunner' '-out:build\Pavise.isolation-integration.exe' @common *> build\core-isolation-compile.txt
        if ($LASTEXITCODE -ne 0) { throw 'Isolation integration build failed.' }
        $isolationArgs = if ($LiveIsolation) { '--integration' } else { '--smoke' }
        Invoke-HiddenTest 'build\Pavise.isolation-integration.exe' $isolationArgs 'build\core-isolation-runner.txt' 300
        Get-Content build\core-isolation-integration.txt
    }
    Get-Content build\core-scheduling-selftest.txt -Tail 1
    Get-Content build\core-scheduling-integration.txt
} finally { Pop-Location }
