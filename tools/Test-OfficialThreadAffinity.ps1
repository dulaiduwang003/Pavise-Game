param([string]$SourceRoot,[string]$OutputDirectory,[switch]$Mutations)
$ErrorActionPreference='Stop'
$taskRepository=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(!$SourceRoot){$SourceRoot=Join-Path $taskRepository 'src\PaviseApp'}
$SourceRoot=[IO.Path]::GetFullPath($SourceRoot)
if(!(Test-Path -LiteralPath $SourceRoot -PathType Container)){throw ('SourceRoot does not exist: '+$SourceRoot)}
if(!$OutputDirectory){$OutputDirectory=Join-Path $taskRepository ('build\OfficialThreadAffinity-'+[Guid]::NewGuid().ToString('N'))}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
$taskSnapshot=Join-Path $taskOutput 'snapshot'
New-Item -ItemType Directory -Path $taskSnapshot -Force | Out-Null
$taskFixtureRoot=Join-Path $taskRepository 'tests\Official3106'
$taskSourceParent=Split-Path $SourceRoot -Parent
$taskSources=@(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -Filter '*.cs')
# 字符串分支辅助类和程序集信息位于 PaviseApp 目录旁。
$taskSources+=@(Get-ChildItem -LiteralPath $taskSourceParent -File -Filter '*.cs')
$taskProperties=Join-Path $taskSourceParent 'Properties'
if(Test-Path -LiteralPath $taskProperties){$taskSources+=@(Get-ChildItem -LiteralPath $taskProperties -File -Filter '*.cs')}
$taskSources=@($taskSources | Sort-Object FullName -Unique)
$taskRecords=@()
foreach($taskFile in $taskSources)
{
    $taskRelative='source\'+$taskFile.FullName.Substring($taskSourceParent.Length+1)
    $taskCopy=Join-Path $taskSnapshot $taskRelative
    New-Item -ItemType Directory -Path (Split-Path $taskCopy -Parent) -Force | Out-Null
    $taskHash=(Get-FileHash -LiteralPath $taskFile.FullName).Hash
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskCopy -Force
    if((Get-FileHash -LiteralPath $taskCopy).Hash -ne $taskHash){throw ('Source changed while freezing: '+$taskFile.FullName)}
    $taskRecords += [pscustomobject]@{Kind='Source';Relative=$taskRelative;Original=$taskFile.FullName;Frozen=$taskCopy;SHA256=$taskHash}
}
foreach($taskFile in @('AutoPureChecks.cs','HardAffinityChecks.cs','GuardianScopeBridge.cs' | ForEach-Object {Get-Item -LiteralPath (Join-Path $taskFixtureRoot $_)}))
{
    $taskRelative='tests\'+$taskFile.Name;$taskCopy=Join-Path $taskSnapshot $taskRelative
    New-Item -ItemType Directory -Path (Split-Path $taskCopy -Parent) -Force | Out-Null
    $taskHash=(Get-FileHash -LiteralPath $taskFile.FullName).Hash
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskCopy -Force
    if((Get-FileHash -LiteralPath $taskCopy).Hash -ne $taskHash){throw 'Fixture changed while freezing'}
    $taskRecords += [pscustomobject]@{Kind='Test';Relative=$taskRelative;Original=$taskFile.FullName;Frozen=$taskCopy;SHA256=$taskHash}
}
$taskRecords | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'inputs.json') -Encoding UTF8
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(!(Test-Path -LiteralPath $taskCompiler)){$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'}
$taskOptions=@('-nologo','-unsafe+','-target:exe','-platform:x64','-optimize+','-utf8output','-nowarn:0162,0169,0649,0414','-codepage:65001','-define:PAVISE_SELFTEST','-reference:System.dll','-reference:System.Core.dll','-reference:System.Drawing.dll','-reference:System.Windows.Forms.dll','-reference:System.Management.dll','-reference:System.Xml.dll','-reference:System.Security.dll')
function Invoke-OfficialAffinityHidden([string]$Program,[string]$Arguments)
{
    $taskInfo=New-Object Diagnostics.ProcessStartInfo
    $taskInfo.FileName=$Program;$taskInfo.Arguments=$Arguments;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
    $taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
    $taskChild=[Diagnostics.Process]::Start($taskInfo)
    try {$taskOut=$taskChild.StandardOutput.ReadToEndAsync();$taskErr=$taskChild.StandardError.ReadToEndAsync();if(!$taskChild.WaitForExit(60000)){$taskChild.Kill();throw 'Affinity timeout is not a passing result'};$taskChild.WaitForExit();return [pscustomobject]@{Exit=$taskChild.ExitCode;Text=$taskOut.Result+$taskErr.Result}}
    finally {$taskChild.Dispose()}
}
function Build-OfficialAffinityEntry([string]$Name,[string]$Suite,[string]$ReplaceOriginal,[string]$ReplaceWith)
{
    $taskFolder=Join-Path $taskOutput $Name
    New-Item -ItemType Directory -Path $taskFolder -Force | Out-Null
    $taskBinary=Join-Path $taskFolder 'Checks.exe';$taskRsp=Join-Path $taskFolder 'sources.rsp'
    $taskMain=if($Suite -eq 'Auto'){'PaviseApp.CriticalThreadAutoChecks'}else{'PaviseApp.CriticalThreadHardAffinityChecks'}
    $taskFiles=@($taskRecords | ForEach-Object {if($_.Original -eq $ReplaceOriginal){'"'+$ReplaceWith+'"'}else{'"'+$_.Frozen+'"'}})
    $taskArguments=$taskOptions+@('-main:'+$taskMain)+@('-out:"'+$taskBinary+'"')+$taskFiles
    [IO.File]::WriteAllLines($taskRsp,[string[]]$taskArguments,(New-Object Text.UTF8Encoding($false)))
    $taskCompile=Invoke-OfficialAffinityHidden $taskCompiler ('@"'+$taskRsp+'"')
    [IO.File]::WriteAllText((Join-Path $taskFolder 'compile.log'),$taskCompile.Text)
    if($taskCompile.Exit -ne 0){throw ('Compile failure is not a semantic result: '+$Name+' '+$taskCompile.Text)}
    $taskRun=Invoke-OfficialAffinityHidden $taskBinary ('"'+(Join-Path $taskFolder 'mock-ledger')+'"')
    [IO.File]::WriteAllText((Join-Path $taskFolder 'result.log'),$taskRun.Text)
    return [pscustomobject]@{Name=$Name;Exit=$taskRun.Exit;Text=$taskRun.Text;Exe=$taskBinary}
}
$taskAuto=Build-OfficialAffinityEntry 'auto' 'Auto' '' ''
Write-Output $taskAuto.Text
if($taskAuto.Exit -ne 0 -or !$taskAuto.Text.Contains('PASS migrated official auto pure assertions=69') -or !$taskAuto.Text.Contains('scenarios=130560;')){throw 'Official Auto/oracle checks failed'}
$taskHard=Build-OfficialAffinityEntry 'hard' 'Hard' '' ''
Write-Output $taskHard.Text
if($taskHard.Exit -ne 0 -or !$taskHard.Text.Contains('PASS hard-thread checks=1074 ')){throw 'Official HardAffinity mock checks failed'}
$taskNegative=Invoke-OfficialAffinityHidden $taskAuto.Exe '--negative-control'
if($taskNegative.Exit -ne 3){throw 'Official affinity negative control failed'}
$taskMutationRows=@()
if($Mutations)
{
    $taskMutants=@(
      @{Name='auto-full-sibling-required';Suite='Auto';File='CriticalThreadAutoPolicy.cs';From='x != 0 && (x & allowed) != 0 && (x & reserved) == 0';To='x != 0 && (x & allowed) == x && (x & reserved) == 0';Failure='three physical cores with one allowed logical processor each refused an automatic intent'},
      @{Name='auto-allowed-projection-lost';Suite='Auto';File='CriticalThreadAutoPolicy.cs';From='array3.Take(count).Select((ulong x) => x & allowed)';To='array3.Take(count)';Failure='an automatic target escaped the odd-only allowed range'},
      @{Name='auto-reserved-sibling-admitted';Suite='Auto';File='CriticalThreadAutoPolicy.cs';From='x != 0 && (x & allowed) != 0 && (x & reserved) == 0';To='x != 0 && (x & allowed) != 0';Failure='held cores counted as automatic capacity'},
      @{Name='auto-worker-capacity-lost';Suite='Auto';File='CriticalThreadAutoPolicy.cs';From='if (array.Length - count < 2)';To='if (array.Length - count < 0)';Failure='three logical processors on only two physical cores accepted an automatic intent'},
      @{Name='plan-full-sibling-required';Suite='Hard';File='HardThreadPlan.cs';From='if (num4 == 0 || (num & num4) != 0)';To='if (num4 == 0 || (num4 & target.AllowedMask) != num4 || (num & num4) != 0)';Failure='candidates must use distinct physical cores'},
      @{Name='plan-full-worker-core-required';Suite='Hard';File='HardThreadPlan.cs';From='if ((num6 & num8) != 0)';To='if ((num6 & num8) == num8)';Failure='logical processors on at least two other physical cores required'},
      @{Name='rule-half-reservation-not-expanded';Suite='Auto';File='CriticalThreadRule.cs';From='reserved = AutoCores.WholeCores(reserved, physical);';To='reserved &= allowed;';Failure='automatic rule capacity differs from logical-ID oracle'},
      @{Name='rule-full-worker-core-required';Suite='Auto';File='CriticalThreadRule.cs';From='physical.Count((ulong x) => (x & workers) != 0)';To='physical.Count((ulong x) => (x & workers) == x)';Failure='concrete rule refused oracle-valid targets'}
    )
    foreach($taskMutant in $taskMutants)
    {
        $taskPin=@($taskRecords | Where-Object {$_.Kind -eq 'Source' -and [IO.Path]::GetFileName($_.Original) -eq $taskMutant.File})
        if($taskPin.Count -ne 1){throw ('Ambiguous mutation source '+$taskMutant.File)}
        $taskText=[IO.File]::ReadAllText($taskPin[0].Frozen)
        if($taskText.Split(@($taskMutant.From),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation must match exactly once '+$taskMutant.Name)}
        $taskFolder=Join-Path $taskOutput ('mutants\'+$taskMutant.Name)
        New-Item -ItemType Directory -Path $taskFolder -Force | Out-Null
        $taskShadow=Join-Path $taskFolder $taskMutant.File
        [IO.File]::WriteAllText($taskShadow,$taskText.Replace($taskMutant.From,$taskMutant.To),(New-Object Text.UTF8Encoding($false)))
        $taskRun=Build-OfficialAffinityEntry ('mutants\'+$taskMutant.Name) $taskMutant.Suite $taskPin[0].Original $taskShadow
        if($taskRun.Exit -ne 1 -or !$taskRun.Text.Contains($taskMutant.Failure)){throw ('Mutation did not fail at intended assertion '+$taskMutant.Name+' '+$taskRun.Text)}
        $taskMutationRows += [pscustomobject]@{Name=$taskMutant.Name;Compile=0;Exit=1;Failure=$taskMutant.Failure;ExactMatches=1}
    }
}
$taskMutationRows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'mutations.json') -Encoding UTF8
$taskFrozenChanged=@($taskRecords | Where-Object {(Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.SHA256})
$taskSourceChanged=@($taskRecords | Where-Object {(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.SHA256})
$taskSummary=[ordered]@{AutoPureAssertions=69;OracleCombinations=130560;ValidPlanCombinations=1566;InsufficientManualCombinations=46020;HardAffinityAssertions=1074;SkippedSelftestOnlyAssertions=1;MutationKills=$taskMutationRows.Count;NegativeControlExit=3;FrozenSourceChanges=$taskFrozenChanged.Count;SourceChangesDuringTest=$taskSourceChanged.Count;ProductMainExecuted=$false;RealThreadBindings=$false;VisibleWindows=$false;Output=$taskOutput}
$taskSummary | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $taskOutput 'verification.json')
if($taskFrozenChanged.Count -or $taskSourceChanged.Count){throw 'Official affinity input changed during verification'}