param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$scaleRepo = Split-Path -Parent $PSScriptRoot
$scaleOutput = Join-Path $scaleRepo 'build\scaling'
& (Join-Path $PSScriptRoot 'Build-ScalingHost.ps1') | Out-Host

function Run-ScaleCheck([string]$Executable, [string]$Arguments, [string]$Name) {
    $outputPath = Join-Path $scaleOutput ($Name + '-test.txt')
    $errorPath = Join-Path $scaleOutput ($Name + '-error.txt')
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -PassThru
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); throw "$Name timed out" }
        $result = Get-Content -LiteralPath $outputPath -Raw
        if ($process.ExitCode -ne 0) { throw "$Name failed: $result $(Get-Content -LiteralPath $errorPath -Raw)" }
        Write-Host $result
        return $result
    } finally { $process.Dispose() }
}

Push-Location $scaleRepo
try {
    $scaleHostPath = Join-Path $scaleOutput 'Pavise.ScaleHost.exe'
    $null = Run-ScaleCheck $scaleHostPath '--self-test' 'hardware'
    $null = Run-ScaleCheck $scaleHostPath '--self-test-warp' 'warp'
    if ($Integration) {
        $scaleCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        $scaleRunner = Join-Path $scaleOutput 'Pavise.ScalingIntegration.exe'
        & $scaleCsc -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_SELFTEST_RUNNER;PAVISE_SCALING_INTEGRATION' -main:PaviseApp.ScalingIntegrationRunner "-out:$scaleRunner" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll "-resource:$scaleHostPath,Pavise.ScaleHost.exe" '-recurse:src\*.cs' '-recurse:tests\*.cs' *> (Join-Path $scaleOutput 'integration-compile.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Integration runner compilation failed; see build/scaling/integration-compile.txt' }
        $null = Run-ScaleCheck $scaleRunner '--integration' 'integration'
        $orphanResult = Run-ScaleCheck $scaleRunner '--orphan' 'orphan'
        if ($orphanResult -notmatch 'ORPHAN_CHILD (\d+)') { throw 'Missing orphan child identity' }
        $orphanPid = [int]$Matches[1]
        $orphanProcess = Get-Process -Id $orphanPid -ErrorAction SilentlyContinue
        if ($orphanProcess) {
            try { if (-not $orphanProcess.WaitForExit(5000)) { throw 'Native helper remained after its parent exited' } }
            finally { $orphanProcess.Dispose() }
        }
        'PASS native-parent-death-cleanup' | Tee-Object -FilePath (Join-Path $scaleOutput 'orphan-test.txt') -Append | Out-Host
    }
} finally { Pop-Location }
