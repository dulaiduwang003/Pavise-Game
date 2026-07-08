# @author bdth 2074055628@qq.com
# 文件用途 启动测试程序并检查游戏会话识别结果

param([string]$AegisPath)
$defaultAegis = Join-Path $PSScriptRoot '..\Aegis.exe'
if ([string]::IsNullOrWhiteSpace($AegisPath)) { $AegisPath = $defaultAegis }

$ErrorActionPreference = 'Stop'
$detectorReport = Join-Path $PSScriptRoot '..\.app-detector-test.txt'
Remove-Item -LiteralPath $detectorReport -Force -ErrorAction SilentlyContinue

$spawnedApp = $null
$testApp = $null
try {
    $existingIds = @(Get-Process mspaint -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    $paintPath = (Get-Command mspaint.exe).Source
    $spawnedApp = Start-Process -FilePath $paintPath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 150
        $spawnedApp.Refresh()
        $testApp = Get-Process mspaint -ErrorAction SilentlyContinue | Where-Object { $existingIds -notcontains $_.Id } | Select-Object -First 1
    } while ($null -eq $testApp -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $testApp) { throw 'Paint test instance did not remain alive' }
    do {
        Start-Sleep -Milliseconds 150
        $testApp.Refresh()
    } while (-not $testApp.HasExited -and $testApp.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)

    $before = Get-Process -Id $testApp.Id
    $beforePriority = $before.PriorityClass
    $beforeAffinity = $before.ProcessorAffinity
    $aegis = $AegisPath

    $appRoot = Split-Path -Parent $before.Path
    $probeArgs = @(
        '--detector-probe',
        $testApp.Id,
        ('"' + $appRoot.Replace('"', '""') + '"'),
        ('"' + $detectorReport.Replace('"', '""') + '"')
    )
    $detectorProcess = Start-Process -FilePath $aegis -ArgumentList $probeArgs -Wait -PassThru
    $detectorExit = $detectorProcess.ExitCode
    $reportDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $detectorReport) -and [DateTime]::UtcNow -lt $reportDeadline) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $detectorReport)) { throw 'Probe report timed out' }

    $after = Get-Process -Id $testApp.Id
    [pscustomobject]@{
        App = 'mspaint.exe'
        Pid = $testApp.Id
        VisibleWindow = [bool]($testApp.MainWindowHandle -ne 0)
        Detector = Get-Content -Encoding UTF8 $detectorReport
        DetectorExit = $detectorExit
        PriorityBefore = $beforePriority
        PriorityAfter = $after.PriorityClass
        AffinityUnchanged = ([int64]$beforeAffinity -eq [int64]$after.ProcessorAffinity)
    } | Format-List
}
finally {
    if ($null -ne $testApp) {
        try {
            $testApp.Refresh()
            if (-not $testApp.HasExited) {
                Stop-Process -Id $testApp.Id -Force
                $testApp.WaitForExit(3000)
            }
        }
        catch { }
    }
    if ($null -ne $spawnedApp -and ($null -eq $testApp -or $spawnedApp.Id -ne $testApp.Id)) {
        try {
            $spawnedApp.Refresh()
            if (-not $spawnedApp.HasExited) {
                Stop-Process -Id $spawnedApp.Id -Force
                $spawnedApp.WaitForExit(3000)
            }
        }
        catch { }
    }
}
