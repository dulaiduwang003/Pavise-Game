param([string]$WorkspaceRoot,[switch]$Mutations)
# Automatic candidate-thread affinity: pure rule, presenter and core choice, the observing frame worker
# with capture and wait doubles, the real target factory and rule resolution with this test process as
# the renderer, and the real Forms card on a temporary profile store inside a private desktop.
# Freezes the whole src plus the test entry and its layout helper, compiles PAVISE_SELFTEST with an
# explicit Main and never runs the application. No ETW session is started and no thread is written.
$ErrorActionPreference='Stop'
if(-not $WorkspaceRoot){$WorkspaceRoot=Split-Path $PSScriptRoot -Parent}
$WorkspaceRoot=[IO.Path]::GetFullPath($WorkspaceRoot)
$output=Join-Path $env:TEMP ('Pavise-CriticalAuto-'+[Guid]::NewGuid().ToString('N'))
$frozen=Join-Path $output 'frozen'
$null=New-Item -ItemType Directory -Path $frozen
$src=Join-Path $WorkspaceRoot 'src'
$inputs=[ordered]@{}
foreach($file in @(Get-ChildItem -LiteralPath $src -Recurse -File -Filter '*.cs'|Sort-Object FullName)){$inputs[('src\'+$file.FullName.Substring($src.Length+1))]=$file.FullName}
$inputs['tests\PerformanceWorkspaceChecks.cs']=Join-Path $WorkspaceRoot 'tests\PerformanceWorkspaceChecks.cs'
$inputs['tests\CriticalThreadAutoChecks.cs']=Join-Path $WorkspaceRoot 'tests\CriticalThreadAutoChecks.cs'
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
 '/define:PAVISE_SELFTEST;PAVISE_PERFORMANCE_PAGE_CHECKS;PAVISE_CRITICAL_THREAD_AUTO_CHECKS','/main:PaviseApp.CriticalThreadAutoChecks',
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
 if($compile.Exit -ne 0){throw ('Compile failure is not a semantic result: '+$name+' '+$compile.Text)}
 $data=Join-Path $output ($name+'-data')
 $run=Invoke-Hidden $exe ('"'+$data+'"') 120000
 [IO.File]::WriteAllText((Join-Path $output ($name+'.log')),$run.Text)
 return [pscustomobject]@{Name=$name;Compile=0;Exit=$run.Exit;Text=$run.Text;Exe=$exe}
}
$baseline=Invoke-Entry 'baseline' '' ''
Write-Output $baseline.Text
if($baseline.Exit -ne 0 -or -not $baseline.Text.Contains('PASS critical thread auto checks=')){Write-Output ('OUTPUT '+$output);throw 'Automatic candidate-thread checks failed'}
$negative=Invoke-Hidden $baseline.Exe '--negative-control' 15000
if($negative.Exit -ne 3){throw 'The negative control did not return its distinct exit code'}
$rows=@([pscustomobject]@{Name='baseline';File='';Exit=0;Expected=''},[pscustomobject]@{Name='negative-control';File='';Exit=$negative.Exit;Expected='exit 3'})
if($Mutations){
 $rule='src\Core\CriticalThreadRule.cs';$policy='src\Core\CriticalThreadAutoPolicy.cs';$observe='src\Core\Suppression\FrameCoordination.Observe.cs'
 $frame='src\Core\Suppression\FrameCoordination.cs';$authority='src\Core\GameMode.CriticalThreadAuthority.cs';$automatic='src\Core\GameMode.CriticalThreadAutomatic.cs'
 $observation='src\Core\CriticalThreadObservation.cs';$card='src\Ui\Pages\PanelForm.CoreThreads.cs'
 $mutants=@(
  [pscustomobject]@{Name='auto-admits-any-worker';File=$rule;From='return Automatic ? bound.WorkerAvoidance == WorkerAvoidance : bound.Encode() == Encode();';To='return Automatic ? true : bound.Encode() == Encode();';Expect='an automatic intent admitted something other than a concrete rule with its worker choice'},
  [pscustomobject]@{Name='auto-parse-loose';File=$rule;From='fields[1] == AutomaticTopology && (fields[2] == "0" || fields[2] == "1")';To='fields[1] == AutomaticTopology';Expect='a malformed automatic intent was accepted: 2|auto|2'},
  [pscustomobject]@{Name='auto-capacity-skipped';File=$rule;From='return CriticalThreadAutoPolicy.Targets(1, physical, allowed, reserved, 0, 0, null, 0, 0) != null ? null';To='return true ? null';Expect='three logical processors on only two physical cores accepted an automatic intent'},
  [pscustomobject]@{Name='crowded-ignored';File=$policy;From='if (crowded != 0 && pool.Count(x => (x & crowded) == 0) >= count) pool = pool.Where(x => (x & crowded) == 0).ToArray();';To='';Expect='the critical thread joined the throttle core of confined background work'},
  [pscustomobject]@{Name='crowded-hard-excluded';File=$policy;From='if (crowded != 0 && pool.Count(x => (x & crowded) == 0) >= count) pool = pool.Where(x => (x & crowded) == 0).ToArray();';To='pool = pool.Where(x => (x & crowded) == 0).ToArray();';Expect='throttle avoidance pushed the candidate onto the core of CPU 0'},
  [pscustomobject]@{Name='resolve-ignores-throttle';File=$automatic;From='CpuTopology.ThrottleMask);';To='0);';Expect='resolution bound the critical thread inside the background throttle cores'},
  [pscustomobject]@{Name='worker-capacity-ignored';File=$policy;From='if (free.Length - count < 2) return null;';To='if (free.Length - count < 0) return null;';Expect='three logical processors on only two physical cores accepted an automatic intent'},
  [pscustomobject]@{Name='restored-complete-core-requirement';File=$policy;From='physical.Where(x => x != 0 && (x & allowed) != 0 && (x & reserved) == 0)';To='physical.Where(x => x != 0 && (x & allowed) == x && (x & reserved) == 0)';Expect='three physical cores with one allowed logical processor each refused an automatic intent'},
  [pscustomobject]@{Name='target-ignores-allowed-sibling';File=$policy;From='.Select(x => x & allowed).Select(x => x & (~x + 1))';To='.Select(x => x & (~x + 1))';Expect='an automatic target escaped the odd-only allowed range'},
  [pscustomobject]@{Name='held-core-accepted';File=$policy;From='physical.Where(x => x != 0 && (x & allowed) != 0 && (x & reserved) == 0)';To='physical.Where(x => x != 0 && (x & allowed) != 0)';Expect='held cores counted as automatic capacity'},
  [pscustomobject]@{Name='cpu0-not-avoided';File=$policy;From='if (pool.Count(x => (x & 1UL) == 0) >= count) pool = pool.Where(x => (x & 1UL) == 0).ToArray();';To='';Expect='the core of CPU 0 was not avoided while it was favored'},
  [pscustomobject]@{Name='performance-ignored';File=$policy;From='if (performance != 0 && pool.Count(x => (x & performance) == x) >= count) pool = pool.Where(x => (x & performance) == x).ToArray();';To='';Expect='an efficiency core was chosen on a hybrid part'},
  [pscustomobject]@{Name='preferred-domain-ignored';File=$policy;From='if (preferred != 0 && pool.Count(x => (x & preferred) == x) >= count) return preferred;';To='';Expect='the large-cache domain was not preferred'},
  [pscustomobject]@{Name='favored-ignored';File=$policy;From='pool.OrderByDescending(x => (x & favored) != 0).ThenByDescending(x => x)';To='pool.OrderByDescending(x => x)';Expect='a favored core was not preferred'},
  [pscustomobject]@{Name='presenter-share-ignored';File=$policy;From=' && x.Windows >= MinimumWindows && x.CpuShare >= MinimumShare);';To=' && x.Windows >= MinimumWindows);';Expect='a presenter below the hotspot share was chosen'},
  [pscustomobject]@{Name='presenter-birth-ignored';File=$policy;From='x.Tid == facts[0].Tid && x.Creation == facts[0].Creation';To='x.Tid == facts[0].Tid';Expect='a recreated thread inherited the presenter role'},
  [pscustomobject]@{Name='presenter-ambiguity-guessed';File=$policy;From='if (facts.Length != 1) return new HardThreadIdentity[0];';To='if (facts.Length == 0) return new HardThreadIdentity[0];';Expect='an ambiguous presenter role was resolved by guessing'},
  [pscustomobject]@{Name='hotspots-trimmed';File=$observation;From='tracker.Observe(previous, copy, MaximumHotspots)';To='tracker.Observe(previous, copy)';Expect='sustained hotspots beyond the two busiest were lost'},
  [pscustomobject]@{Name='observer-full-capture';File=$observe;From='TakeCapture(target, created, alive, true);';To='TakeCapture(target, created, alive, session.PresenterOnly);';Expect='the observer captured without presenter-only mode'},
  [pscustomobject]@{Name='observer-applies';File=$observe;From='PublishCriticalRoles(sample, epoch);';To='PublishCriticalRoles(sample, epoch); Apply(sample, alive);';Expect='the observer wrote a thread priority'},
  [pscustomobject]@{Name='observer-no-roles';File=$observe;From='PublishCriticalRoles(sample, epoch);';To=';';Expect='the observer did not publish the presenter role'},
  [pscustomobject]@{Name='release-observer-noop';File=$observe;From='{ lock (sync) if (observing && worker != null && worker.IsAlive) Interlocked.Increment(ref generation); }';To='{ }';Expect='ReleaseObserver did not stop the observer'},
  [pscustomobject]@{Name='release-boost-kills-observer';File=$observe;From='keep = observing && worker != null && worker.IsAlive && observedPid > 0 && pid == observedPid && birth == observedBirth;';To='keep = false;';Expect='a boost release stopped an observer that is still wanted'},
  [pscustomobject]@{Name='release-boost-keeps-unwanted';File=$observe;From='keep = observing && worker != null && worker.IsAlive && observedPid > 0 && pid == observedPid && birth == observedBirth;';To='keep = observing && worker != null && worker.IsAlive;';Expect='a boost release kept an observer nobody wants'},
  [pscustomobject]@{Name='observer-replaces-boost';File=$observe;From='if (observing && (pid != target || birth != created)) Interlocked.Increment(ref generation);';To='Interlocked.Increment(ref generation);';Expect='an observer request disturbed a live boost worker'},
  [pscustomobject]@{Name='observer-ignores-block';File=$observe;From='if (ActionFailureStore.IsBlocked(ActionKey, name)) { state = 4; return; }
                FrameCaptureSession session = CaptureSession(target, created);
                // Game-managed';To='FrameCaptureSession session = CaptureSession(target, created);
                // Game-managed';Expect='a blocked capture started an observer'},
  [pscustomobject]@{Name='observer-stopped-by-managed';File=$observe;From='if (session == null || session.Stopped && !session.NoChangeNeeded || session.Failures >= MaxCaptureFailures) return;';To='if (session == null || session.Stopped || session.Failures >= MaxCaptureFailures) return;';Expect='game-managed priorities stopped an observation that writes none'},
  [pscustomobject]@{Name='boost-keeps-observer';File=$frame;From='if (pid != target || birth != created || observing && !GameManagedLocked(target, created, name))';To='if (pid != target || birth != created)';Expect='a boost Ensure did not retire the observer of the same renderer'},
  [pscustomobject]@{Name='factory-exact-only';File=$authority;From='!StoredRuleAdmits(expectedRawRule, expectedRule)';To='expectedRule.Encode() != expectedRawRule';Expect='an automatic intent did not admit its concrete rule'},
  [pscustomobject]@{Name='resolve-ignores-held';File=$automatic;From='reserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;';To='reserved = 0;';Expect='a held core was resolved as a target'},
  [pscustomobject]@{Name='resolve-ignores-scope';File=$automatic;From='if (scope.Error != null) { refusal = scope.Error; return false; }';To='';Expect='an enabled but invalid game processor rule was ignored by automatic resolution'},
  [pscustomobject]@{Name='roles-never-settled';File=$automatic;From='if (Volatile.Read(ref criticalRolesPid) == 0) return;';To='return;';Expect='roles nobody asked for in a pass were kept'},
  [pscustomobject]@{Name='card-saves-manual-intent';File=$card;From='CriticalThreadRule automatic = CriticalThreadRule.Auto(true);';To='CriticalThreadRule automatic = CriticalThreadRule.Auto(false);';Expect='the switch did not store the automatic intent'},
  [pscustomobject]@{Name='card-hides-old-manual';File=$card;From=': saved != null && !saved.Automatic ? ThreadUi(';To=': false ? ThreadUi(';Expect='an older manual rule was not disclosed'}
 )
 foreach($mutation in $mutants){
  $pin=@($pins|Where-Object{$_.Relative -eq $mutation.File})
  if($pin.Count -ne 1){throw ('Mutation target is not a frozen input: '+$mutation.File)}
  $text=[IO.File]::ReadAllText($pin[0].Frozen).Replace("`r`n","`n")
  $from=([string]$mutation.From).Replace("`r`n","`n");$to=([string]$mutation.To).Replace("`r`n","`n")
  if($text.Split(@($from),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation anchor must be unique: '+$mutation.Name)}
  $file=Join-Path $output ($mutation.Name+'.cs');[IO.File]::WriteAllText($file,$text.Replace($from,$to),(New-Object Text.UTF8Encoding($false)))
  $result=Invoke-Entry $mutation.Name $mutation.File $file
  if($result.Exit -ne 1 -or -not $result.Text.Contains('FAIL '+$mutation.Expect)){throw ('Mutation did not fail at its intended check: '+$mutation.Name+' | '+$result.Text.Trim())}
  $rows+=[pscustomobject]@{Name=$mutation.Name;File=$mutation.File;Exit=$result.Exit;Expected=$mutation.Expect}
 }
}
$changed=@($pins|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.Hash -or (Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$rows|Export-Csv -LiteralPath (Join-Path $output 'results.csv') -NoTypeInformation
[pscustomobject]@{Baseline=0;NegativeControl=$negative.Exit;MutationKills=@($rows|Where-Object{$_.Exit -eq 1}).Count;Inputs=$pins.Count;SourceChanges=$changed.Count;Native='query-limited handle to this test process only';ETW=$false;ProductMain=$false;Output=$output}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($changed.Count){throw 'Automatic candidate-thread test inputs changed'}
Write-Output ('PASS critical thread auto mutations='+@($rows|Where-Object{$_.Exit -eq 1}).Count+'; OUTPUT '+$output)
