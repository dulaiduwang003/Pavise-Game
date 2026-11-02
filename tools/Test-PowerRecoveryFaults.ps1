param([string]$OutputDirectory,[switch]$SkipNegativeControls)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$powerFaultWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-PowerRecoveryFaults-'+[Guid]::NewGuid().ToString('N'))}
$powerFaultOutput=[IO.Path]::GetFullPath($OutputDirectory);$powerFaultFrozen=Join-Path $powerFaultOutput 'frozen'
if(Test-Path -LiteralPath $powerFaultFrozen){throw 'Choose a fresh output directory; frozen evidence is retained.'}
$null=New-Item -ItemType Directory -Path $powerFaultFrozen -Force
$powerFaultManifest=New-Object 'Collections.Generic.List[object]'
foreach($powerFaultOriginal in @(& rg --files (Join-Path $powerFaultWorkspace 'src') -g '*.cs')){
    $powerFaultRelative=$powerFaultOriginal.Substring($powerFaultWorkspace.Length+1);$powerFaultCopy=Join-Path $powerFaultFrozen $powerFaultRelative
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($powerFaultCopy)) -Force
    [IO.File]::WriteAllBytes($powerFaultCopy,[IO.File]::ReadAllBytes($powerFaultOriginal))
    $powerFaultManifest.Add([pscustomobject]@{Path=$powerFaultRelative;Original=$powerFaultOriginal;SHA256=(Get-FileHash -LiteralPath $powerFaultCopy -Algorithm SHA256).Hash})
}
function Write-PowerFaultUtf8([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text,(New-Object Text.UTF8Encoding($false)))}
function Find-PowerFaultBrace([string]$Text,[int]$Start){
    $depth=0;$state='code'
    for($i=$Start;$i -lt $Text.Length;$i++){
        $c=$Text[$i];$next=if($i+1 -lt $Text.Length){$Text[$i+1]}else{[char]0}
        if($state -eq 'line'){if($c -eq "`n"){$state='code'};continue}
        if($state -eq 'block'){if($c -eq '*' -and $next -eq '/'){$state='code';$i++};continue}
        if($state -eq 'verbatim'){if($c -eq '"'){if($next -eq '"'){$i++}else{$state='code'}};continue}
        if($state -eq 'string' -or $state -eq 'char'){if($c -eq '\'){$i++}elseif(($state -eq 'string' -and $c -eq '"') -or ($state -eq 'char' -and $c -eq "'")){$state='code'};continue}
        if($c -eq '/' -and $next -eq '/'){$state='line';$i++;continue}
        if($c -eq '/' -and $next -eq '*'){$state='block';$i++;continue}
        if($c -eq '@' -and $next -eq '"'){$state='verbatim';$i++;continue}
        if($c -eq '"'){$state='string';continue};if($c -eq "'"){$state='char';continue}
        if($c -eq '{'){$depth++}elseif($c -eq '}'){$depth--;if($depth -eq 0){return $i}}
    }
    throw 'Unbalanced fixture source braces'
}
function Get-PowerFaultBodyRange([string]$Text,[string]$Signature){
    if(([regex]::Matches($Text,[regex]::Escape($Signature))).Count -ne 1){throw "Missing or ambiguous endpoint: $Signature"}
    $start=$Text.IndexOf($Signature,[StringComparison]::Ordinal);$open=$Text.IndexOf('{',$start);$close=Find-PowerFaultBrace $Text $open
    return [pscustomobject]@{Open=$open;Close=$close}
}
function Set-PowerFaultBody([string]$Text,[string]$Signature,[string]$Body){
    $range=Get-PowerFaultBodyRange $Text $Signature
    return $Text.Substring(0,$range.Open+1)+"`n"+$Body+"`n"+$Text.Substring($range.Close)
}
$powerFaultPowerPath=Join-Path $powerFaultFrozen 'src/PaviseApp/PowerPlan.cs'
$powerFaultGamePath=Join-Path $powerFaultFrozen 'src/PaviseApp/GameMode.cs'
$powerFaultPower=[IO.File]::ReadAllText($powerFaultPowerPath)
# Route only native environment boundaries. Keep WriteAc/WriteDc/Set, the CPU
# ledger protocol, parking restoration, ActionFailureStore and session guards real.
$powerFaultPrimitive='\[DllImport\("powrprof\.dll"[^\]]*\)\]\s*private static extern uint (\w+)\(([^;]+)\);'
$powerFaultPrimitiveCount=([regex]::Matches($powerFaultPower,$powerFaultPrimitive)).Count
if($powerFaultPrimitiveCount -lt 12){throw 'Unexpected native power endpoint inventory.'}
$powerFaultPower=[regex]::Replace($powerFaultPower,$powerFaultPrimitive,[Text.RegularExpressions.MatchEvaluator]{param($match)
    $name=$match.Groups[1].Value;$signature=$match.Groups[2].Value
    $body=switch($name){
        'PowerReadACValueIndex' {'return PowerRecoveryFixture.Read(scheme,sub,setting,true,out value);'}
        'PowerReadDCValueIndex' {'return PowerRecoveryFixture.Read(scheme,sub,setting,false,out value);'}
        'PowerWriteACValueIndex' {'return PowerRecoveryFixture.Write(scheme,sub,setting,true,value);'}
        'PowerWriteDCValueIndex' {'return PowerRecoveryFixture.Write(scheme,sub,setting,false,value);'}
        'PowerSetActiveScheme' {'return PowerRecoveryFixture.Set(guid);'}
        default {'throw PowerRecoveryFixture.Forbidden();'}
    }
    return 'private static uint '+$name+'('+$signature+') { '+$body+' }'
})
$powerFaultPower=Set-PowerFaultBody $powerFaultPower 'private static Guid? Current()' 'return PowerRecoveryFixture.Current();'
$powerFaultPower=Set-PowerFaultBody $powerFaultPower 'private static bool SchemeUsable(Guid g)' 'return g==PowerRecoveryFixture.Plan || g==PowerRecoveryFixture.Other;'
$powerFaultPower=Set-PowerFaultBody $powerFaultPower 'private static string ReadName(Guid g)' 'return "Memory plan "+g;'
$powerFaultPower=Set-PowerFaultBody $powerFaultPower 'private static bool? RestorePlanIsUsable(Guid scheme)' 'return scheme==PowerRecoveryFixture.Plan || scheme==PowerRecoveryFixture.Other;'
Write-PowerFaultUtf8 $powerFaultPowerPath $powerFaultPower
$powerFaultGame=[IO.File]::ReadAllText($powerFaultGamePath)
$powerFaultGame=Set-PowerFaultBody $powerFaultGame 'private static bool RestorePowerPlan()' 'return PowerRecoveryFixture.Restore();'
Write-PowerFaultUtf8 $powerFaultGamePath $powerFaultGame
$powerFaultFixtureOriginal=Join-Path $powerFaultWorkspace 'tests/Official3106/PowerRecoveryFaultChecks.cs'
$powerFaultFixture=Join-Path $powerFaultOutput 'PowerRecoveryFaultChecks.cs'
[IO.File]::WriteAllBytes($powerFaultFixture,[IO.File]::ReadAllBytes($powerFaultFixtureOriginal))
$powerFaultManifest.Add([pscustomobject]@{Path='tests/Official3106/PowerRecoveryFaultChecks.cs';Original=$powerFaultFixtureOriginal;SHA256=(Get-FileHash -LiteralPath $powerFaultFixture -Algorithm SHA256).Hash})
$powerFaultManifest|Export-Csv -LiteralPath (Join-Path $powerFaultOutput 'source-manifest.tsv') -Delimiter ([char]9) -NoTypeInformation
Write-PowerFaultUtf8 (Join-Path $powerFaultOutput 'fixture-endpoints.txt') 'Frozen sources only: native powrprof read/write/activate primitives route to a dictionary with faults; every other powrprof endpoint throws; Current reads a controlled nullable Guid; Settings is a test-only memory API with write/readback faults; GameMode.RestorePowerPlan calls an observable environment restore callback. CPU idle receipt, parking restore, WriteAc/WriteDc/Set verification, ActionFailureStore, RunEnvironmentAction, RunPowerPlanApply, RollbackPowerPlan and session admission/invalidation methods are original. GameMode/profile store objects are allocated without constructors; only required memory state is initialized. No product runtime, real registry, powercfg, device, process mutation or visible UI. Negative controls change only output copies.'
$powerFaultSources=@(Get-ChildItem -LiteralPath $powerFaultFrozen -Recurse -File -Filter '*.cs'|Where-Object{$_.FullName -ne (Join-Path $powerFaultFrozen 'src/PaviseApp/Settings.cs')}|ForEach-Object{$_.FullName})
$powerFaultSources+=$powerFaultFixture
$powerFaultCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
function Invoke-PowerFaultBuild([string[]]$Inputs,[string]$Name){
    $exe=Join-Path $powerFaultOutput ($Name+'.exe');$rsp=Join-Path $powerFaultOutput ($Name+'.rsp')
    [IO.File]::WriteAllLines($rsp,[string[]]@($Inputs|ForEach-Object{'"'+$_+'"'}),(New-Object Text.UTF8Encoding($false)))
    & $powerFaultCompiler /nologo /unsafe+ /target:exe /platform:x64 /optimize+ /codepage:65001 /nowarn:0649,0414 '/define:PAVISE_POWER_RECOVERY_FAULTS' /main:PaviseApp.PowerRecoveryFaultChecks ('/out:'+$exe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll /reference:System.Security.dll ('@'+$rsp) *> (Join-Path $powerFaultOutput ($Name+'.compile.log'))
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $powerFaultOutput ($Name+'.compile.log'));throw ('Power fault fixture failed to compile: '+$Name)}
    return $exe
}
function Invoke-PowerFaultRun([string]$Exe,[string]$Name){
    $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$Exe;$info.Arguments='"'+$powerFaultOutput+'"';$info.WorkingDirectory=$powerFaultOutput
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($info)
    try{$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(20000)){$process.Kill();throw 'Owned power fault fixture exceeded20s'}
        $process.WaitForExit();$result=$stdout.Result+$stderr.Result;Write-PowerFaultUtf8 (Join-Path $powerFaultOutput ($Name+'.result.txt')) $result
        return [pscustomobject]@{Exit=$process.ExitCode;Text=$result}
    }finally{$process.Dispose()}
}
$powerFaultExe=Invoke-PowerFaultBuild $powerFaultSources 'PowerRecoveryFaultChecks'
$powerFaultRun=Invoke-PowerFaultRun $powerFaultExe 'PowerRecoveryFaultChecks';Write-Output $powerFaultRun.Text
if($powerFaultRun.Exit -ne 0 -or $powerFaultRun.Text -notmatch 'PASS power recovery faults'){throw 'Power recovery fault checks failed.'}
$powerFaultNegatives=New-Object 'Collections.Generic.List[object]'
if(-not $SkipNegativeControls){
    # Reintroduce aggregate settlement of a mixed0/1 pair on retry.
    $range=Get-PowerFaultBodyRange $powerFaultPower 'internal static bool RestoreLegacyCpuIdle()'
    $body=$powerFaultPower.Substring($range.Open+1,$range.Close-$range.Open-1)
    $prefix='if(PowerPlan.cpuIdleReceipt!=null && PowerPlan.cpuIdleReceipt.PendingMask==2){uint mutantAc,mutantDc;if(CpuIdleReadValues(PowerPlan.cpuIdleReceipt.Scheme,out mutantAc,out mutantDc)&&mutantAc==0&&mutantDc==1){PowerPlan.cpuIdleReceipt.PendingMask=0;PowerPlan.cpuIdleReceipt.Phase=CpuIdlePhase.Settled;return ClearCpuIdleReceipt();}}'
    $cpu=Set-PowerFaultBody $powerFaultPower 'internal static bool RestoreLegacyCpuIdle()' ($prefix+$body)
    $cpuPath=Join-Path $powerFaultOutput 'PowerPlan.aggregate-negative.cs';Write-PowerFaultUtf8 $cpuPath $cpu
    $run=Invoke-PowerFaultRun (Invoke-PowerFaultBuild (@($powerFaultSources|Where-Object{$_ -ne $powerFaultPowerPath})+$cpuPath) 'CpuAggregateNegative') 'CpuAggregateNegative'
    if($run.Exit -ne 1 -or $run.Text -notmatch 'Retry rewrote AC or skipped pending DC'){throw 'Aggregate CPU idle negative control did not fail at the expected assertion.'}
    $powerFaultNegatives.Add([pscustomobject]@{Mutation='aggregate mixed-pair settlement';Exit=$run.Exit;Rejected=$true})
    $admission=Set-PowerFaultBody $powerFaultGame 'private bool PowerPlanApplySessionCurrent(int genShot)' 'return !stopping && !PowerPlan.NoSwitchSelected && Volatile.Read(ref powerSessionGen)==genShot;'
    $admissionPath=Join-Path $powerFaultOutput 'GameMode.admission-negative.cs';Write-PowerFaultUtf8 $admissionPath $admission
    $run=Invoke-PowerFaultRun (Invoke-PowerFaultBuild (@($powerFaultSources|Where-Object{$_ -ne $powerFaultGamePath})+$admissionPath) 'AdmissionNegative') 'AdmissionNegative'
    if($run.Exit -ne 1 -or $run.Text -notmatch 'Stale power session was admitted'){throw 'Stale apply negative control did not fail at the expected assertion.'}
    $powerFaultNegatives.Add([pscustomobject]@{Mutation='missing live-session admission';Exit=$run.Exit;Rejected=$true})
    $old='(!Set(guid) || Current() != guid)'
    if(([regex]::Matches($powerFaultPower,[regex]::Escape($old))).Count -ne 1){throw 'Update the parking activation negative control after changing that branch.'}
    $park=$powerFaultPower.Replace($old,'(Set(guid) && false)');$parkPath=Join-Path $powerFaultOutput 'PowerPlan.park-negative.cs';Write-PowerFaultUtf8 $parkPath $park
    $run=Invoke-PowerFaultRun (Invoke-PowerFaultBuild (@($powerFaultSources|Where-Object{$_ -ne $powerFaultPowerPath})+$parkPath) 'ParkActivationNegative') 'ParkActivationNegative'
    if($run.Exit -ne 1 -or $run.Text -notmatch 'Failed activation cleared parking snapshot'){throw 'Ignored parking activation failure negative control did not fail at the expected assertion.'}
    $powerFaultNegatives.Add([pscustomobject]@{Mutation='ignored parking activation failure';Exit=$run.Exit;Rejected=$true})
    Write-Output 'PASS aggregate CPU, stale apply and ignored parking activation negative controls rejected'
}
$powerFaultChanged=@($powerFaultManifest|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256})
[pscustomobject]@{RunExit=$powerFaultRun.Exit;FrozenInputs=$powerFaultManifest.Count;WorkspaceMatchesSnapshot=($powerFaultChanged.Count -eq 0);ChangedSources=@($powerFaultChanged|ForEach-Object{$_.Path});NativePowerEndpointsIsolated=$powerFaultPrimitiveCount;ActualPowerWrites=0;ActualRegistryWrites=0;ProductStartup=0;UiWindows=0;NegativeControls=$powerFaultNegatives.ToArray()}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $powerFaultOutput 'verification.json') -Encoding UTF8
Write-Output ('OUTPUT '+$powerFaultOutput)
if($powerFaultChanged.Count -ne 0){throw 'Sources changed during frozen validation; rerun against the final source.'}
