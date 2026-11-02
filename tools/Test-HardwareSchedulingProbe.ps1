param([switch]$Mutations)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$probeWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$probeOutput = Join-Path $env:TEMP ('Pavise-HardwareSchedulingProbe-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $probeOutput
$probeSources = Join-Path $probeOutput 'sources'
$null = New-Item -ItemType Directory -Path $probeSources
$probeFiles = @('src\Platform\HardwareSchedulingProbe.cs', 'tests\HardwareSchedulingProbeChecks.cs', 'tools\Test-HardwareSchedulingProbe.ps1')
$probeManifest = @()
foreach ($probeRelative in $probeFiles) {
    $probeOriginal = Join-Path $probeWorkspace $probeRelative
    $probeFrozen = Join-Path $probeSources ([IO.Path]::GetFileName($probeRelative))
    [IO.File]::WriteAllBytes($probeFrozen, [IO.File]::ReadAllBytes($probeOriginal))
    $probeManifest += [pscustomobject]@{Original=$probeOriginal;Frozen=$probeFrozen;SHA256=(Get-FileHash -LiteralPath $probeFrozen -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$probeManifest | Export-Csv -LiteralPath (Join-Path $probeOutput 'source-sha256.tsv') -Delimiter "`t" -NoTypeInformation
$probeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$probeModule = Join-Path $probeSources 'HardwareSchedulingProbe.cs'
$probeChecks = Join-Path $probeSources 'HardwareSchedulingProbeChecks.cs'
$probeEntry = 'PaviseApp.HardwareSchedulingProbeChecks'
$probeResults = New-Object 'Collections.Generic.List[string]'
$probeResults.Add("step`tcompile_exit`trun_exit`tresult`tdetail")
$script:probeFailures = 0

function Invoke-ProbeHidden([string]$File, [string]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$File; $info.Arguments=$Arguments; $info.WorkingDirectory=$probeOutput
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process
    $process.StartInfo=$info
    try {
        if (-not $process.Start()) { throw 'Child did not start' }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Child timeout is a test failure, never a killed mutant' }
        $process.WaitForExit()
        return [pscustomobject]@{ExitCode=$process.ExitCode;Text=($stdout.Result + $stderr.Result)}
    } finally { $process.Dispose() }
}
function Record-Probe([string]$Step, [int]$CompileExit, [int]$RunExit, [bool]$Ok, [string]$Detail) {
    $verdict='PASS'; if (-not $Ok) { $verdict='FAIL'; $script:probeFailures++ }
    $detailClean=($Detail -replace '[\r\n\t]+',' ').Trim()
    $probeResults.Add($Step+"`t"+$CompileExit+"`t"+$RunExit+"`t"+$verdict+"`t"+$detailClean)
}
function Compile-Probe([string]$Name, [string]$Defines, [string]$Module, [switch]$Library) {
    $target='exe'; if ($Library) { $target='library' }
    $file=Join-Path $probeOutput ($Name+'.'+$(if($Library){'dll'}else{'exe'}))
    $arguments='/nologo /target:'+$target+' /platform:x64 /optimize+ /codepage:65001 /out:"'+$file+'" /warnaserror+'
    if ($Defines) { $arguments+=' "/define:'+$Defines+'"' }
    if (-not $Library) { $arguments+=' /main:'+$probeEntry+' "'+$probeChecks+'"' }
    $arguments+=' "'+$Module+'"'
    $result=Invoke-ProbeHidden $probeCompiler $arguments
    [IO.File]::WriteAllText((Join-Path $probeOutput ($Name+'.compile.log')), $result.Text)
    return [pscustomobject]@{ExitCode=$result.ExitCode;Text=$result.Text;File=$file}
}

# Native binding is compiled as a library, never executed. Each individual test symbol independently blocks defaults.
try {
    foreach ($symbol in @('PAVISE_SELFTEST','PAVISE_PERFLAB')) {
        $build=Compile-Probe $symbol ($symbol+';PAVISE_HWSCH_PROBE_CHECKS') $probeModule
        if ($build.ExitCode -ne 0) { Record-Probe $symbol $build.ExitCode -1 $false $build.Text; continue }
        $run=Invoke-ProbeHidden $build.File ''
        [IO.File]::WriteAllText((Join-Path $probeOutput ($symbol+'.run.log')), $run.Text)
        Record-Probe $symbol $build.ExitCode $run.ExitCode ($run.ExitCode -eq 0 -and $run.Text -match '^PASS \d+ hardware scheduling probe checks') $run.Text
        $negative=Invoke-ProbeHidden $build.File '--negative-control'
        Record-Probe ($symbol+' negative control') 0 $negative.ExitCode ($negative.ExitCode -eq 3 -and $negative.Text -match 'FAIL: intentional negative control') $negative.Text
    }
    $prod=Compile-Probe 'production-compile-only' '' $probeModule -Library
    Record-Probe 'production compile (not run)' $prod.ExitCode -1 ($prod.ExitCode -eq 0) $prod.Text

    if ($Mutations) {
        # Mutations are applied only to the frozen private source copy. Every death needs compile0 + run1 + FAIL assertion.
        $original=[IO.File]::ReadAllText($probeModule)
        $mutants=@(
            @('known-feature','if ((bits & KnownFeatureBit) == 0)','if (false)'),
            @('query-status','if (queryStatus != 0)','if (false)'),
            @('luid-binding','if (open.AdapterLuid.LowPart != parsed.LowPart || open.AdapterLuid.HighPart != parsed.HighPart)','if (false)'),
            @('query-identity','if (query.Adapter != ownedAdapter || query.FeatureId != HwsFeatureId)','if (false)'),
            @('feature-version','if (version > SupportedEnabledVersion || (enabled && version != SupportedEnabledVersion))','if (false)'),
            @('handle-close','if (ownedAdapter != 0)','if (false)'),
            @('reserved-result-bits','internal const ushort DocumentedResultBits = 0x3f;','internal const ushort DocumentedResultBits = 0xffff;')
        )
        foreach ($mutation in $mutants) {
            if (($original.Split(@([string]$mutation[1]),[StringSplitOptions]::None).Length-1) -ne 1) { throw ('Mutation anchor not unique: '+$mutation[0]) }
            $mutantSource=Join-Path $probeOutput ('mutant-'+$mutation[0]+'.cs')
            [IO.File]::WriteAllText($mutantSource,$original.Replace($mutation[1],$mutation[2]))
            # Removing guards creates unreachable code warnings in these intentionally broken private copies only.
            $build=Compile-Probe ('mutant-'+$mutation[0]) 'PAVISE_SELFTEST;PAVISE_HWSCH_PROBE_CHECKS' $mutantSource
            if ($build.ExitCode -ne 0 -and $build.Text -match 'CS0162') {
                $args='/nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /nowarn:0162 /main:'+$probeEntry+' /define:PAVISE_SELFTEST,PAVISE_HWSCH_PROBE_CHECKS /out:"'+$build.File+'" "'+$probeChecks+'" "'+$mutantSource+'"'
                $retry=Invoke-ProbeHidden $probeCompiler $args
                $build.ExitCode=$retry.ExitCode; $build.Text=$retry.Text
                [IO.File]::WriteAllText((Join-Path $probeOutput ('mutant-'+$mutation[0]+'.compile.log')),$retry.Text)
            }
            if ($build.ExitCode -ne 0) { Record-Probe ('mutant '+$mutation[0]) $build.ExitCode -1 $false $build.Text; continue }
            $run=Invoke-ProbeHidden $build.File ''
            [IO.File]::WriteAllText((Join-Path $probeOutput ('mutant-'+$mutation[0]+'.run.log')),$run.Text)
            Record-Probe ('mutant '+$mutation[0]) 0 $run.ExitCode ($run.ExitCode -eq 1 -and $run.Text -match 'FAIL:') $run.Text
        }
    }
} catch {
    Record-Probe 'harness failure' -1 -1 $false $_.Exception.Message
} finally {
    [IO.File]::WriteAllLines((Join-Path $probeOutput 'results.tsv'),$probeResults)
    foreach ($line in $probeResults) { Write-Output $line }
    Write-Output ('ARTIFACT='+$probeOutput)
}
if ($script:probeFailures -gt 0) { exit 1 }
exit 0
