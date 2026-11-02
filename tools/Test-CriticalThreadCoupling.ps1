param([string]$WorkspaceRoot,[string]$OverlayDirectory,[switch]$Mutations)
# Thread-boost / hard-affinity coupling disclosure: pure classifier and GameMode wiring, plus the
# hard-affinity admission checks (C-1 protected roster, C-2 published reasons).
# Freezes the whole src (optionally with an overlay of src-relative files) plus one test entry,
# compiles PAVISE_SELFTEST with an explicit Main, and never runs the application. The only native
# calls are a query-limited OpenProcess and GetProcessAffinityMask on the test process itself.
$ErrorActionPreference='Stop'
if(-not $WorkspaceRoot){$WorkspaceRoot=Split-Path $PSScriptRoot -Parent}
$WorkspaceRoot=[IO.Path]::GetFullPath($WorkspaceRoot)
$test=[IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/CriticalThreadCouplingChecks.cs'))
$output=Join-Path $env:TEMP ('Pavise-CriticalCoupling-'+[Guid]::NewGuid().ToString('N'))
$frozen=Join-Path $output 'frozen'
$null=New-Item -ItemType Directory -Path $frozen
$src=Join-Path $WorkspaceRoot 'src'
$inputs=[ordered]@{}
foreach($file in @(Get-ChildItem -LiteralPath $src -Recurse -File -Filter '*.cs'|Sort-Object FullName)){$inputs[('src\'+$file.FullName.Substring($src.Length+1))]=$file.FullName}
if($OverlayDirectory){
 $overlay=[IO.Path]::GetFullPath($OverlayDirectory)
 foreach($file in @(Get-ChildItem -LiteralPath $overlay -Recurse -File -Filter '*.cs'|Sort-Object FullName)){
  $relative=$file.FullName.Substring($overlay.Length+1)
  if($relative.StartsWith('tests\') -or $relative.StartsWith('tools\') -or $relative.StartsWith('baseline\') -or $relative.StartsWith('superseded-v1\') -or $relative.Split('\')[0] -like '*-frozen'){continue}
  $inputs[('src\'+$relative)]=$file.FullName
 }
}
$inputs['tests\CriticalThreadCouplingChecks.cs']=$test
$pins=@()
foreach($key in $inputs.Keys){
 $target=Join-Path $frozen $key
 $null=New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
 $hash=(Get-FileHash -LiteralPath $inputs[$key]).Hash
 [IO.File]::Copy($inputs[$key],$target)
 if((Get-FileHash -LiteralPath $target).Hash -ne $hash){throw ('An input changed while freezing: '+$key)}
 $pins+=[pscustomobject]@{Relative=$key;Original=$inputs[$key];Frozen=$target;Hash=$hash}
}
$pins|Export-Csv -LiteralPath (Join-Path $output 'inputs.csv') -NoTypeInformation
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$options=@('/nologo','/target:exe','/platform:x64','/unsafe+','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0169,0649,0414',
 '/define:PAVISE_SELFTEST;CB_T2B_COUPLING_CHECKS','/main:PaviseApp.CriticalThreadCouplingChecks',
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
function Invoke-Hidden([string]$program,[string]$arguments,[int]$limit){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$program;$info.Arguments=$arguments;$info.UseShellExecute=$false
 $info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{
  $null=$child.Start();$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit($limit)){$child.Kill();$child.WaitForExit();throw 'A timeout is not a semantic test result'}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}
 }finally{$child.Dispose()}
}
function Invoke-Entry([string]$name,[string]$replaceRelative,[string]$replacement){
 $exe=Join-Path $output ($name+'.exe');$rsp=Join-Path $output ($name+'.rsp')
 $files=@($pins|ForEach-Object{if($_.Relative -eq $replaceRelative){'"'+$replacement+'"'}else{'"'+$_.Frozen+'"'}})
 [IO.File]::WriteAllLines($rsp,($options+@('/out:"'+$exe+'"')+$files),(New-Object Text.UTF8Encoding($false)))
 $compile=Invoke-Hidden $csc ('@"'+$rsp+'"') 180000
 [IO.File]::WriteAllText((Join-Path $output ($name+'.compile.log')),$compile.Text)
 if($compile.Exit -ne 0){throw ('Compile failure is not a semantic result: '+$name)}
 $run=Invoke-Hidden $exe '' 60000
 [IO.File]::WriteAllText((Join-Path $output ($name+'.log')),$run.Text)
 return [pscustomobject]@{Name=$name;Compile=0;Exit=$run.Exit;Text=$run.Text;Exe=$exe}
}
$baseline=Invoke-Entry 'baseline' '' ''
Write-Output $baseline.Text
if($baseline.Exit -ne 0 -or -not $baseline.Text.Contains('PASS critical thread coupling assertions=')){Write-Output ('OUTPUT '+$output);throw 'Coupling checks failed'}
$negative=Invoke-Hidden $baseline.Exe '--negative-control' 15000
if($negative.Exit -ne 3){throw 'The negative control did not return its distinct exit code'}
$rows=@([pscustomobject]@{Name='baseline';File='';Exit=0;Expected=''},[pscustomobject]@{Name='negative-control';File='';Exit=$negative.Exit;Expected='exit 3'})
if($Mutations){
 $coupling='src\Core\CriticalThreadBoostCoupling.cs';$runtime='src\Core\GameMode.CriticalThreadRuntime.cs'
 $lane='src\Core\Suppression\RenderLane.cs';$state='src\Core\CriticalThreadUiState.cs'
 $mutants=@(
  [pscustomobject]@{Name='boost-switch-ignored';File=$coupling;From='if (!boostEnabled) return CriticalThreadBoostCoupling.BoostDisabled;';To='';Expect='the game resource priority switch was ignored'},
  [pscustomobject]@{Name='eligibility-ignored';File=$coupling;From='if (!laneEligible) return CriticalThreadBoostCoupling.CandidateUnsupported;';To='';Expect='an unavailable busy-thread boost was reported as selected'},
  [pscustomobject]@{Name='thread-birth-ignored';File=$coupling;From='candidate.Tid == lane.Tid && candidate.Creation == lane.ThreadBirth';To='candidate.Tid == lane.Tid';Expect='a reused TID was reported as the same thread'},
  [pscustomobject]@{Name='process-birth-ignored';File=$coupling;From='|| lane.ProcessBirth != birth)';To=')';Expect='a boost in another process instance was reported for this game'},
  [pscustomobject]@{Name='first-candidate-only';File=$coupling;From='foreach (HardThreadIdentity candidate in candidates)';To='foreach (HardThreadIdentity candidate in new[] { candidates[0] })';Expect='the second candidate''s overlap was missed'},
  [pscustomobject]@{Name='unknown-birth-classified';File=$coupling;From='if (lane.Tid <= 0 || lane.ThreadBirth <= 0) return CriticalThreadBoostCoupling.Unknown;';To='';Expect='a busy-thread boost without creation time was classified'},
  [pscustomobject]@{Name='frame-association-ignored';File=$coupling;From=': candidateFrameAssociated ? CriticalThreadBoostCoupling.FrameAssociated : CriticalThreadBoostCoupling.FrameOther;';To=': CriticalThreadBoostCoupling.FrameAssociated;';Expect='an unassociated candidate was reported as possibly boosted'},
  [pscustomobject]@{Name='overlap-tid-hidden';File=$coupling;From='string thread = tid.ToString(CultureInfo.InvariantCulture);';To='string thread = "";';Expect='the overlapping candidate TID was not shown'},
  [pscustomobject]@{Name='benefit-wording';File=$coupling;From='from busy-thread boost; the combined effect is untested';To='from busy-thread boost; this improves low frames';Expect='coupling text claims a benefit for SameThread'},
  [pscustomobject]@{Name='wiring-choice-read-twice';File=$runtime;From='string mode = !ThreadBoostSelection.IsOn(choice) ? ThreadBoostSelection.Off';To='string mode = !policy.ThreadBoostOn ? ThreadBoostSelection.Off';Expect='the wiring read the thread boost choice more than once'},
  [pscustomobject]@{Name='wiring-boost-switch-ignored';File=$runtime;From='Classify(policy.EffBoost, mode, LaneSupported(policy.Preset)';To='Classify(true, mode, LaneSupported(policy.Preset)';Expect='the wiring ignored the game resource priority switch'},
  [pscustomobject]@{Name='wiring-eligibility-ignored';File=$runtime;From='Classify(policy.EffBoost, mode, LaneSupported(policy.Preset)';To='Classify(policy.EffBoost, mode, true';Expect='the wiring ignored busy-thread boost eligibility'},
  [pscustomobject]@{Name='frame-association-any-role';File=$coupling;From='if (role != null && role.Tid == choice.Thread.Tid && role.Creation == choice.Thread.Creation) return true;';To='if (role != null) return true;';Expect='a role fact of an unbound thread marked the candidates as associated'},
  [pscustomobject]@{Name='frame-association-tid-only';File=$coupling;From='if (role != null && role.Tid == choice.Thread.Tid && role.Creation == choice.Thread.Creation) return true;';To='if (role != null && role.Tid == choice.Thread.Tid) return true;';Expect='a reused TID with another creation time was treated as associated'},
  [pscustomobject]@{Name='frame-association-first-choice';File=$coupling;From='foreach (HardThreadChoice choice in choices)';To='foreach (HardThreadChoice choice in new[] { choices[0] })';Expect='the second candidate''s role fact was missed'},
  [pscustomobject]@{Name='wiring-no-policy-falls-back';File=$runtime;From='if (policy == null) { sameTid = 0; return CriticalThreadBoostCoupling.Unknown; }';To='if (policy == null) { int t; CriticalThreadBoostCoupling v = CriticalThreadBoostCouplingPolicy.Classify(EffBoost, null, LaneEligible, pid, birth, null, null, false, out t); sameTid = t; return v; }';Expect='the wiring took the GameMode lock without a session policy'},
  [pscustomobject]@{Name='wiring-no-policy-classified';File=$runtime;From='if (policy == null) { sameTid = 0; return CriticalThreadBoostCoupling.Unknown; }';To='if (policy == null) { sameTid = 0; return CriticalThreadBoostCoupling.BoostOff; }';Expect='the wiring classified a relation without a session policy'},
  [pscustomobject]@{Name='wiring-applied-ignored';File=$runtime;From='applied && plan != null ? plan.Choices.Select(x => x.Thread).ToArray() : null';To='plan != null ? plan.Choices.Select(x => x.Thread).ToArray() : null';Expect='the wiring reported a thread overlap before candidates were applied'},
  [pscustomobject]@{Name='wiring-frame-flag-ignored';File=$runtime;From='criticalChoiceFrameAssociated, out sameTid';To='false, out sameTid';Expect='the wiring ignored the frame association'},
  [pscustomobject]@{Name='publish-drops-coupling';File=$runtime;From='source.RuleText, coupling, couplingTid);';To='source.RuleText);';Expect='the published state lost the coupling'},
  [pscustomobject]@{Name='lane-view-requires-birth';File=$lane;From='return laneApplied;';To='return laneApplied && laneThreadCreation > 0;';Expect='the wiring trusted a busy-thread boost without creation time'},
  [pscustomobject]@{Name='lane-view-drops-birth';File=$lane;From='threadBirth = laneThreadCreation;';To='threadBirth = 0;';Expect='the wiring missed the busy-thread boost on a bound candidate'},
  [pscustomobject]@{Name='lane-view-ignores-applied';File=$lane;From='return laneApplied;';To='return true;';Expect='the wiring reported an inactive busy-thread boost'},
  [pscustomobject]@{Name='state-drops-coupling';File=$state;From='Coupling = coupling; CouplingTid = couplingTid;';To='CouplingTid = couplingTid;';Expect='the published state lost the coupling'},
  [pscustomobject]@{Name='c1-roster-ignored';File=$runtime;From='if (ProtectedGameRoster.Contains(source.Name)) {';To='if (false) {';Expect='a game that denied or stripped writes was not refused before the foreground and sampling steps'},
  [pscustomobject]@{Name='binding-follows-foreground';File=$runtime;From='if (criticalSession == null && (smartSwitchedOut || RendererForegroundPid() != source.Pid)) {';To='if (smartSwitchedOut || RendererForegroundPid() != source.Pid) {';Expect='a binding in place was released when the game left the foreground'},
  [pscustomobject]@{Name='new-binding-while-switched-out';File=$runtime;From='if (criticalSession == null && (smartSwitchedOut || RendererForegroundPid() != source.Pid)) {';To='if (criticalSession == null && RendererForegroundPid() != source.Pid) {';Expect='a new binding started while the session was switched out'},
  [pscustomobject]@{Name='c1-roster-silent';File=$runtime;From=('criticalObservation.Reset();'+"`n"+'                    PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text(');To=('criticalObservation.Reset();'+"`n"+'                    string.Concat(source, "unsupported", PerformancePolicy.Text(');Expect='a game that denied or stripped writes was not refused before the foreground and sampling steps'},
  [pscustomobject]@{Name='c1-roster-after-foreground';File=$runtime;From='if (ProtectedGameRoster.Contains(source.Name)) {';To='if (!smartSwitchedOut && RendererForegroundPid() == source.Pid && ProtectedGameRoster.Contains(source.Name)) {';Expect='a game that denied or stripped writes was not refused before the foreground and sampling steps'},
  [pscustomobject]@{Name='c1-roster-start-only';File=$runtime;From='if (ProtectedGameRoster.Contains(source.Name)) {';To='if (criticalSession == null && ProtectedGameRoster.Contains(source.Name)) {';Expect='a session in progress kept its bindings after its game joined the protected roster'},
  [pscustomobject]@{Name='c1-roster-no-restore';File=$runtime;From=('if (ProtectedGameRoster.Contains(source.Name)) {'+"`n"+'                    if (!RestoreCriticalThreadsLocked()) return;');To='if (ProtectedGameRoster.Contains(source.Name)) {';Expect='a session in progress kept its bindings after its game joined the protected roster'},
  [pscustomobject]@{Name='c1-roster-restore-ignored';File=$runtime;From=('if (ProtectedGameRoster.Contains(source.Name)) {'+"`n"+'                    if (!RestoreCriticalThreadsLocked()) return;');To=('if (ProtectedGameRoster.Contains(source.Name)) {'+"`n"+'                    RestoreCriticalThreadsLocked();');Expect='a failed restore of a protected game''s session was hidden behind the roster reason'},
  [pscustomobject]@{Name='c1-roster-keeps-hotspots';File=$runtime;From=('if (!RestoreCriticalThreadsLocked()) return;'+"`n"+'                    criticalObservation.Reset();'+"`n"+'                    PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text(');To=('if (!RestoreCriticalThreadsLocked()) return;'+"`n"+'                    PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text(');Expect='a refused game kept its hotspot observations or reached candidate selection'},
  [pscustomobject]@{Name='c2-affinity-silent';File=$runtime;From=('if (!readable) {'+"`n"+'                    PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text(');To=('if (!readable) {'+"`n"+'                    string.Concat(source, "unsupported", PerformancePolicy.Text(');Expect='an unreadable process affinity returned without a reason'},
  [pscustomobject]@{Name='c2-target-silent';File=$runtime;From=('if (original == null) {'+"`n"+'                    PublishCriticalThreadState(source, "observing", PerformancePolicy.Text(');To=('if (original == null) {'+"`n"+'                    string.Concat(source, "observing", PerformancePolicy.Text(');Expect='a refused binding target returned without a reason'}
 )
 foreach($mutation in $mutants){
  $pin=@($pins|Where-Object{$_.Relative -eq $mutation.File})
  if($pin.Count -ne 1){throw ('Mutation target is not a frozen input: '+$mutation.File)}
  $text=[IO.File]::ReadAllText($pin[0].Frozen).Replace("`r`n","`n")
  if($text.Split(@([string]$mutation.From),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation anchor must be unique: '+$mutation.Name)}
  $file=Join-Path $output ($mutation.Name+'.cs');[IO.File]::WriteAllText($file,$text.Replace($mutation.From,$mutation.To),(New-Object Text.UTF8Encoding($false)))
  $result=Invoke-Entry $mutation.Name $mutation.File $file
  if($result.Exit -ne 1 -or -not $result.Text.Contains('FAIL '+$mutation.Expect)){throw ('Mutation did not fail at its intended check: '+$mutation.Name+' | '+$result.Text.Trim())}
  $rows+=[pscustomobject]@{Name=$mutation.Name;File=$mutation.File;Exit=$result.Exit;Expected=$mutation.Expect}
 }
}
$changed=@($pins|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.Hash -or (Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$rows|Export-Csv -LiteralPath (Join-Path $output 'results.csv') -NoTypeInformation
[pscustomobject]@{Baseline=0;NegativeControl=$negative.Exit;MutationKills=@($rows|Where-Object{$_.Exit -eq 1}).Count;Inputs=$pins.Count;Overlay=[bool]$OverlayDirectory;SourceChanges=$changed.Count;Native='query-limited handle to this test process only';GUI=$false;ProductMain=$false;Output=$output}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($changed.Count){throw 'Coupling test inputs changed'}
Write-Output ('PASS critical thread coupling mutations='+@($rows|Where-Object{$_.Exit -eq 1}).Count+'; OUTPUT '+$output)
