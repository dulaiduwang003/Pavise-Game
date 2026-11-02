param(
    [string]$BenchPath = "$env:TEMP\Pavise-PresentLaneLab\PresentWakeLaneLab.exe",
    [int]$Rounds = 3,
    [int]$Seconds = 6,
    [string]$OutputDirectory = "",
    [switch]$IncludeWakeGroup,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
if ($Rounds -lt 1 -or $Rounds -gt 5 -or $Seconds -lt 2 -or $Seconds -gt 12) { throw 'Invalid bounded experiment duration.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-PresentLane-Results-' + [guid]::NewGuid().ToString('N')) }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { throw 'ETW elevation failed.' }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -BenchPath "{1}" -Rounds {2} -Seconds {3} -OutputDirectory "{4}" -Elevated' -f $PSCommandPath,$BenchPath,$Rounds,$Seconds,$OutputDirectory
    if ($IncludeWakeGroup) { $arguments += ' -IncludeWakeGroup' }
    $child = Start-Process -FilePath powershell.exe -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    Write-Output "RUN_DIRECTORY=$OutputDirectory"
    Write-Output "CONTROLLER_PID=$($child.Id)"
    return
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PresentLaneLab.cpp'),$PSCommandPath,(Join-Path $PSScriptRoot 'Analyze.py') -Destination $OutputDirectory
Copy-Item -LiteralPath $BenchPath -Destination (Join-Path $OutputDirectory 'Benchmark.exe')
$beforePlan = (& powercfg /getactivescheme | Out-String).Trim()
$manifest = [ordered]@{
    StartedUtc=[DateTime]::UtcNow.ToString('o');Rounds=$Rounds;Seconds=$Seconds
    CasesPerRound=$(if ($IncludeWakeGroup) { 18 } else { 17 });IncludeWakeGroup=[bool]$IncludeWakeGroup
    ProcessPriority='HIGH_PRIORITY_CLASS';BaselineDriftLimitPercent=10
    BenchSha256=(Get-FileHash -LiteralPath $BenchPath -Algorithm SHA256).Hash
    SourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PresentLaneLab.cpp') -Algorithm SHA256).Hash
    Cpu=(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    Os=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,BuildNumber)
    ActivePlanBefore=$beforePlan
    Kind='synthetic controlled contention; not game FPS'
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding UTF8
$rows = [Collections.Generic.List[object]]::new()
$cases = @(
    @{Name='free';Layout='free';Dependent='false'},
    @{Name='same';Layout='same';Dependent='false'},
    @{Name='dependency';Layout='same';Dependent='true'},
    @{Name='smt';Layout='smt';Dependent='false'}
)
try {
    for ($round=0; $round -lt $Rounds; $round++) {
        $caseOrder = if ($round % 2 -eq 0) { $cases } else { @($cases[3],$cases[2],$cases[1],$cases[0]) }
        foreach ($case in $caseOrder) {
            $arms = if ($round % 2 -eq 0) { @('baseline','cpu-hot','present','baseline') } else { @('baseline','present','cpu-hot','baseline') }
            if ($case.Dependent -eq 'true') { $arms = @($arms[0],$arms[1],'group',$arms[2],$arms[3]) }
            if ($case.Dependent -eq 'true' -and $IncludeWakeGroup) { $arms = @($arms[0],$arms[1],$arms[2],'wake-group',$arms[3],$arms[4]) }
            for ($index=0; $index -lt $arms.Count; $index++) {
                $arm=$arms[$index]
                $label = '{0:D2}-{1}-{2:D2}-{3}' -f $round,$case.Name,$index,$arm
                $raw = Join-Path $OutputDirectory ($label+'.csv')
                $err = Join-Path $OutputDirectory ($label+'.stderr.txt')
                $args = '{0} {1} {2} {3} "{4}"' -f $arm,$case.Layout,$case.Dependent,$Seconds,$raw
                $startInfo=[Diagnostics.ProcessStartInfo]::new($BenchPath,$args)
                $startInfo.UseShellExecute=$false;$startInfo.CreateNoWindow=$true
                $startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
                $startInfo.RedirectStandardError=$true
                $owned=[Diagnostics.Process]::new();$owned.StartInfo=$startInfo
                $null=$owned.Start();$errorRead=$owned.StandardError.ReadToEndAsync()
                try {
                    if (-not $owned.WaitForExit(32000)) { $owned.Kill();$owned.WaitForExit();throw "Owned bench timed out: $label" }
                    $errorText=$errorRead.Result
                    $errorText | Set-Content -LiteralPath $err -Encoding UTF8
                    if ($owned.ExitCode -ne 0) { throw "Bench failed: $label ($($owned.ExitCode)); $errorText" }
                } finally { $owned.Dispose() }
                $row=Import-Csv -LiteralPath $raw
                if (@($row).Count -ne 1 -or $row.trace_rc -ne '0' -or $row.events_lost -ne '0' -or $row.restored -ne '1' -or $row.failed -ne '0') { throw "Invalid capture or restore: $label" }
                if ($row.process_priority -ne '128') { throw "Invalid process priority: $label" }
                if ($arm -eq 'wake-group' -and ($row.wake_rc -ne '0' -or $row.wake_lost -ne '0' -or $row.wake_winner -ne $row.worker_tid)) { throw "Invalid owned wake attribution: $label" }
                $row | Add-Member -NotePropertyName round -NotePropertyValue $round
                $row | Add-Member -NotePropertyName case -NotePropertyValue $case.Name
                $row | Add-Member -NotePropertyName order -NotePropertyValue $index
                $rows.Add($row)
                $rows | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'samples.csv') -NoTypeInformation -Encoding UTF8
                $label | Set-Content -LiteralPath (Join-Path $OutputDirectory 'progress.txt') -Encoding UTF8
                Start-Sleep -Milliseconds 500
            }
        }
    }
    'PASS' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'completed.txt') -Encoding UTF8
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $OutputDirectory 'failed.txt') -Encoding UTF8
    throw
} finally {
    $afterPlan=(& powercfg /getactivescheme | Out-String).Trim()
    [ordered]@{EndedUtc=[DateTime]::UtcNow.ToString('o');Samples=$rows.Count;ActivePlanUnchanged=($beforePlan -eq $afterPlan);ActivePlanAfter=$afterPlan} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'verification.json') -Encoding UTF8
}
