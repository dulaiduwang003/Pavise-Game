param([switch]$Mutations, [switch]$PolicyOnly, [string]$WorkspaceRoot, [string]$OverlayRoot, [string]$HardThreadStageRoot, [string]$MutationFilter, [string]$JointIntegrationStageRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($MutationFilter -and -not $Mutations){throw 'MutationFilter requires explicit Mutations mode'}
$gameNvWorkspace=if($WorkspaceRoot){[IO.Path]::GetFullPath($WorkspaceRoot)}else{[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))}
$gameNvOutput=Join-Path $env:TEMP ('Pavise-GameNvNativeStage-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $gameNvOutput
$gameNvSources=@(& rg --files (Join-Path $gameNvWorkspace 'src') -g '*.cs')
$gameNvDependencies=@{}
if($HardThreadStageRoot){
 foreach($area in @('Core','Platform')){
  foreach($dependency in @(& rg --files (Join-Path $HardThreadStageRoot $area) -g 'CriticalThread*.cs')){
   $relative=Join-Path (Join-Path 'src' $area) ([IO.Path]::GetFileName($dependency))
   $gameNvDependencies[$relative]=[IO.Path]::GetFullPath($dependency)
   $gameNvSources+=Join-Path $gameNvWorkspace $relative
  }
 }
}
if($JointIntegrationStageRoot){
 $jointRoot=[IO.Path]::GetFullPath($JointIntegrationStageRoot)
 foreach($area in @('Core','Platform')){
  $areaRoot=Join-Path $jointRoot $area
  foreach($dependency in @(& rg --files $areaRoot -g '*.cs')){
   $relative=Join-Path (Join-Path 'src' $area) ([IO.Path]::GetFullPath($dependency)).Substring($areaRoot.Length+1)
   $gameNvDependencies[$relative]=[IO.Path]::GetFullPath($dependency);$gameNvSources+=Join-Path $gameNvWorkspace $relative
  }
 }
 $existingRoot=Join-Path $jointRoot 'Existing'
 foreach($dependency in @(& rg --files $existingRoot -g '*.cs')){
  $part=([IO.Path]::GetFullPath($dependency)).Substring($existingRoot.Length+1)
  $relative=if($part -eq 'FrameCoordination.cs'){Join-Path 'src\Core\Suppression' $part}else{Join-Path 'src' $part}
  $gameNvDependencies[$relative]=[IO.Path]::GetFullPath($dependency);$gameNvSources+=Join-Path $gameNvWorkspace $relative
 }
}
if($OverlayRoot){
 foreach($stageSource in @(& rg --files (Join-Path $OverlayRoot 'src') -g '*.cs')){
  $stageRelative=([IO.Path]::GetFullPath($stageSource)).Substring(([IO.Path]::GetFullPath($OverlayRoot)).Length+1)
  $gameNvSources+=Join-Path $gameNvWorkspace $stageRelative
 }
}
foreach($relative in @('tests/GameNvProfileChecks.cs','tests/GameNvFreshWriterChecks.cs','tests/GameNvDeliveryChecks.cs')){$gameNvSources+=Join-Path $gameNvWorkspace $relative}
$gameNvSources+=Join-Path $gameNvWorkspace 'src/Platform/GameNvProfileNativeWriter.cs'
$gameNvSources=@($gameNvSources|Select-Object -Unique)
$gameNvManifest=@($gameNvSources|ForEach-Object{
 $original=[IO.Path]::GetFullPath($_);$relative=$original.Substring($gameNvWorkspace.Length+1);if($gameNvDependencies.ContainsKey($relative)){$original=$gameNvDependencies[$relative]};if($OverlayRoot -and (Test-Path -LiteralPath (Join-Path $OverlayRoot $relative))){$original=Join-Path $OverlayRoot $relative};$frozen=Join-Path (Join-Path $gameNvOutput 'sources') $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
 [IO.File]::WriteAllBytes($frozen,[IO.File]::ReadAllBytes($original))
 [pscustomobject]@{Original=$original;Frozen=$frozen;SHA256=(Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$gameNvStageContract=$OverlayRoot -and -not (Test-Path -LiteralPath (Join-Path $OverlayRoot 'src/Core/GameNvNativeAuthority.cs'))
if($gameNvStageContract){
 $contract=Join-Path $OverlayRoot 'contract/GameNvNativeAuthority.cs';$contractFrozen=Join-Path $gameNvOutput 'stage-authority-contract.cs'
 [IO.File]::WriteAllBytes($contractFrozen,[IO.File]::ReadAllBytes($contract))
 $gameNvManifest+= [pscustomobject]@{Original=$contract;Frozen=$contractFrozen;SHA256=(Get-FileHash -LiteralPath $contractFrozen -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$gameNvManifest|Export-Csv -LiteralPath (Join-Path $gameNvOutput 'source-sha256.tsv') -Delimiter "`t" -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $gameNvOutput 'Test-GameNvProfile.ps1'),[IO.File]::ReadAllBytes($PSCommandPath))
$gameNvFrozen=@($gameNvManifest|ForEach-Object{$_.Frozen})
$gameNvCsc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$gameNvResults=New-Object 'Collections.Generic.List[string]';$gameNvResults.Add("step`tcompile_exit`trun_exit`tresult`tdetail")
$script:gameNvFailures=0
function Invoke-GameNvProfileHidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{$null=$child.Start();$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync();if(-not $child.WaitForExit(60000)){$child.Kill();throw 'Timeout is not a semantic mutant kill'};$child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$stdout.Result+$stderr.Result}}finally{$child.Dispose()}
}
function Build-GameNvProfile([string]$Name,[string[]]$Sources){
 $exe=Join-Path $gameNvOutput ($Name+'.exe');$rsp=Join-Path $gameNvOutput ($Name+'.rsp')
 $args=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/nowarn:0162,0649','/define:PAVISE_SELFTEST;PAVISE_GUARD_SELFTEST;PAVISE_GAME_NV_PROFILE_CHECKS','/main:PaviseApp.GameNvProfileChecks',('/out:"'+$exe+'"'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll')
 if($gameNvStageContract){$args+='/define:PAVISE_GAME_NV_STAGE_CONTRACT'}
 $args+=@($Sources|ForEach-Object{'"'+$_+'"'});[IO.File]::WriteAllLines($rsp,$args,(New-Object Text.UTF8Encoding($false)))
 $result=Invoke-GameNvProfileHidden $gameNvCsc ('@"'+$rsp+'"');[IO.File]::WriteAllText((Join-Path $gameNvOutput ($Name+'.compile.log')),$result.Text);return [pscustomobject]@{Exit=$result.Exit;Text=$result.Text;File=$exe}
}
function Record-GameNvProfile([string]$Step,[int]$Compile,[int]$Run,[bool]$Ok,[string]$Detail){
 $verdict=if($Ok){'PASS'}else{$script:gameNvFailures++;'FAIL'};$gameNvResults.Add($Step+"`t"+$Compile+"`t"+$Run+"`t"+$verdict+"`t"+($Detail-replace '[\r\n\t]+',' ').Trim())
}
function Run-GameNvProfile([string]$Name,[string]$File){
 $data=Join-Path $env:TEMP ('PNB-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $data
 [IO.File]::WriteAllText((Join-Path $gameNvOutput ($Name+'.data-root.txt')),$data)
 $mode=if($PolicyOnly){' --policy-only'}else{''}
 $result=Invoke-GameNvProfileHidden $File ('"'+$data+'"'+$mode);[IO.File]::WriteAllText((Join-Path $gameNvOutput ($Name+'.run.log')),$result.Text);return $result
}
try{
 $build=Build-GameNvProfile 'checks' $gameNvFrozen
 if($build.Exit -ne 0){Record-GameNvProfile 'compile' $build.Exit -1 $false $build.Text;throw 'game NVIDIA checks did not compile'}
 $run=Run-GameNvProfile 'checks' $build.File;Record-GameNvProfile 'checks' 0 $run.Exit ($run.Exit -eq 0 -and $run.Text -match '(?m)^PASS \d+ game NVIDIA checks') $run.Text
 $negative=Invoke-GameNvProfileHidden $build.File '--negative-control';Record-GameNvProfile 'negative control' 0 $negative.Exit ($negative.Exit -eq 3 -and $negative.Text -match 'FAIL: intentional game NVIDIA negative control') $negative.Text
 if($Mutations){
  $mutants=if($PolicyOnly){@(
   @('GameNvProfilePolicy.cs','policy-local-gl-rule','if (id == GameNvPrelaunchPlan.OpenGlId && source == GameNvValueSource.ProfilePredefined)','if (false)','FAIL: GL source-specific decision: ProfilePredefined/0'),
   @('GameNvProfilePolicy.cs','policy-local-user-choice','if (source == GameNvValueSource.ProfileUser)','if (false)','FAIL: GL source-specific decision: ProfileUser/0'),
   @('GameNvProfilePolicy.cs','policy-already-on','if (value.Value == requested)','if (false)','FAIL: already-on preserves every current source: ProfileUser'),
   @('GameNvProfilePolicy.cs','policy-union-not-source','bool predefined = value.CurrentPredefined != 0;','bool predefined = value.PredefinedValid != 0;','FAIL: current value source ignores predefined union validity: ProfileUser/1'),
   @('GameNvProfilePolicy.cs','policy-distinct-base-origin','case 2: return predefined ? GameNvValueSource.BasePredefined : GameNvValueSource.BaseUser;','case 2: return predefined ? GameNvValueSource.GlobalPredefined : GameNvValueSource.GlobalUser;','FAIL: current value source ignores predefined union validity: BaseUser/0'),
   @('GameNvProfilePolicy.cs','policy-unresolved-not-zero','if (value == null || !value.Resolved)','if (value == null)','FAIL: missing or unresolved setting remains observation pending'),
   @('GameNvProfilePolicy.cs','policy-pstate-independent','if (id == GameNvPrelaunchPlan.OpenGlId && source == GameNvValueSource.ProfilePredefined)','if (source == GameNvValueSource.ProfilePredefined)','FAIL: PState independent of local vendor GL policy: ProfilePredefined/0'),
   @('GameNvProfileLease.cs','policy-readonly-entry','return InspectCore(plan, explicitlySuppliedReader, false);','return InspectCore(plan, explicitlySuppliedReader, true);','FAIL: product inspection explicitly records pure policy without native authority'),
   @('GameNvProfileLease.cs','policy-mechanism-not-authority','!ProductPolicyEvaluated || originalPlan == null','originalPlan == null','FAIL: mechanism-only observation cannot serve as product policy evaluation'),
   @('GameNvProfileLease.cs','policy-two-key-vector','if (Originals.Count != 2 || Decisions.Count != 2 || EligibleTargets.Keys.Any(id => id != GameNvPrelaunchPlan.PStateId && id != GameNvPrelaunchPlan.OpenGlId))','if (false)','FAIL: semantic guard rejects any extra mutation address'),
   @('GameNvProfileLease.cs','policy-preparation-entry','return PrepareIsolated(originalPlan, ownStore, originalAdmission, fakeBackend, false);','return PrepareIsolated(originalPlan, ownStore, originalAdmission, fakeBackend, true);','FAIL: product fake lease changes only eligible PState, without redundant GL override')
  )}else{@(
   @('GameNvPrelaunchPlan.cs','partial-gl-pending','member == GameNvMember.OpenGl && !gl','!gl'),
   @('GameNvPrelaunchPlan.cs','pstate-ac-only','member == GameNvMember.PState && !approval.OnAc','false'),
   @('GameNvProfileBackend.cs','errors-not-absence','if (status == exactNotFound)','if (status != 0)'),
   @('GameNvProfileLease.cs','exact-application-scope','!profile.Applications[0].IsPlainExactPath(plan.Executable)','false'),
   @('GameNvProfileLease.cs','resource-write-ahead','Mark("issued-create"); BeforeWrite();','BeforeWrite();'),
   @('GameNvProfileLease.cs','setting-write-ahead','Mark("issued-set-" + id.ToString("X8")); BeforeWrite();','BeforeWrite();'),
   @('GameNvProfileLease.cs','immutable-original','var pin = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);','var pin = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);'),
   @('GameNvProfileLease.cs','restore-inheritance','original.UserOverride ? (uint?)original.Value : null','(uint?)original.Value'),
   @('GameNvProfileLease.cs','respect-external-override','value.Value != expected','false'),
   @('GameNvProfileLease.cs','fresh-persisted-readback','backend.SetUserOverride(name, id, value); BeforeWrite(); backend.Save(); Current(false);','backend.SetUserOverride(name, id, value); backend.Save();'),
   @('GameNvProfileLease.cs','original-issued-binding','if (!ReadFile(file, 4096).SequenceEqual(Event(op)))','if (false)'),
   @('GameNvProfileLease.cs','outstanding-original-debt','RequireStoreNoDebt(Path.GetDirectoryName(profileRoot));',''),
   @('GameNvProfileLease.cs','real-terminal-proof','!ReadFile(terminal, 4096).SequenceEqual(expected)','false'),
   @('GameNvProfileLease.cs','independent-safe-restoration','CalibrationCleanupSequence.Complete(null, ids.Select(id => (Action)delegate { RestoreMember(id); }).ToArray());','RestoreMember(ids[0]);'),
   @('GameNvProfileLease.cs','running-target-race','if (admission.Recheck(plan) != GameNvTargetPhase.Closed)','if (false)'),
   @('GameNvProfileLease.cs','owned-creation-authority','if (own && !Has("issued-create"))','if (false)'),
   @('GameNvPrelaunchPlan.cs','gl-physical-applicability','api.PhysicalToken == approval.PhysicalToken','true')
  )}
  if(-not $PolicyOnly){$mutants+=@(
   @('GameNvProfileNativeWriter.cs','native-last-admission','stage(api); Require(); RequireIssued(pendingOperation); api.Save();','stage(api); RequireIssued(pendingOperation); api.Save();'),
   @('GameNvProfileNativeWriter.cs','native-commit-issued-phase','stage(api); Require(); RequireIssued(pendingOperation); api.Save();','stage(api); Require(); api.Save();'),
   @('GameNvProfileNativeWriter.cs','native-new-session-readback','Require(); using (var verifyApi = open()) { Require(); verifyApi.Reload(); Require(); readback(verifyApi); Require(); }','Require();'),
   @('GameNvProfileNativeWriter.cs','native-owned-local-empty','if (p.LocalSettingCount != 0 || p.Applications.Count != 1 || !p.Applications[0].IsPlainExactPath(path))','if (p.Applications.Count != 1 || !p.Applications[0].IsPlainExactPath(path))'),
   @('GameNvProfileNativeWriter.cs','native-no-borrowed-delete','if (!own || !recovery) throw new InvalidOperationException("No original profile deletion authority");','if (false) throw new InvalidOperationException("No original profile deletion authority");'),
   @('GameNvWorkerQueue.cs','worker-queue-false-release','if (Interlocked.CompareExchange(ref entry, 2, 0) == 0) finish(); // False before callback admission.','// Removed false-admission cleanup.'),
   @('GameNvWorkerQueue.cs','worker-queue-throw-release','if (Interlocked.CompareExchange(ref entry, 2, 0) == 0) finish(); // Throw before callback admission.','// Removed thrown-admission cleanup.'),
   @('GameNvWorkerQueue.cs','worker-single-callback','if (Interlocked.CompareExchange(ref entry, 1, 0) != 0) return;','if (false) return;'),
   @('LegacyPurge.cs','hard-thread-debt-boundary','return HardThreadFileStore.CanonicalRootHasRecoveryDebt(Path.Combine(Path.GetFullPath(dataDir), CriticalThreadGuardian.StoreDirectory));','return false;'),
   @('CalibrationFeatureNvProfile.cs','native-read-boundary-unsaved','throw new InvalidOperationException("Read boundary discarded an uncommitted DRS operation; never saved implicitly");','return;'),
   @('CalibrationRuntime.cs','original-runtime-grant','if (IsGameNvKey(key)) return GameNvApprovedValue(key);','if (false) return GameNvApprovedValue(key);'),
   @('GameNvCanonicalStore.cs','root-io-not-absence','catch (DirectoryNotFoundException) { continue; }','catch (DirectoryNotFoundException) { continue; } catch (IOException) { continue; }'),
   @('GameMode.GameNvDelivery.cs','owner-calibration-lease','!enabled || calibrationLeaseActive || ProfileStoreSaveFailed','!enabled || ProfileStoreSaveFailed'),
   @('GameMode.GameNvDelivery.cs','pending-worker','gameNvWorker || gameNvLease != null || gameNvAuthority != null || !gameNvRecoveryChecked || !gameNvRootClear ? 1 : 0','gameNvLease != null || gameNvAuthority != null || !gameNvRecoveryChecked || !gameNvRootClear ? 1 : 0'),
   @('GameMode.GameNvDelivery.cs','pending-held-lease','gameNvWorker || gameNvLease != null || gameNvAuthority != null || !gameNvRecoveryChecked || !gameNvRootClear ? 1 : 0','gameNvWorker || gameNvAuthority != null || !gameNvRecoveryChecked || !gameNvRootClear ? 1 : 0'),
   @('CalibrationRuntime.GameNvGrant.cs','pinned-utf8-bom','int start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;','int start = 0;'),
   @('CalibrationRuntime.GameNvGrant.cs','source-close-retain','item.Value.Dispose(); files.Remove(item.Key);','files.Remove(item.Key); item.Value.Dispose();'),
   @('CalibrationRuntime.GameNvGrant.cs','grant-close-retry','public void Dispose() { lock (sourceGate) { disposed = true; revoked = true; pins.Dispose(); } }','public void Dispose() { lock (sourceGate) { if (disposed) return; disposed = true; revoked = true; pins.Dispose(); } }'),
   @('GameNvProfileLease.cs','lease-close-retry','if (disposed) { Release(); return; } Exception failure = null;','if (disposed) return; Exception failure = null;'),
   @('GameNvNativeAuthority.cs','authority-close-retry','disposed = true; forwardSealed = true;','if (disposed) return; disposed = true; forwardSealed = true;')
  )}
  if($MutationFilter){$mutants=@($mutants|Where-Object{$_[1] -match $MutationFilter});if($mutants.Count -eq 0){throw 'MutationFilter matched no declared mutation'}}
  foreach($mutant in $mutants){
   $module=@($gameNvFrozen|Where-Object{[IO.Path]::GetFileName($_) -eq $mutant[0]})[0];$original=[IO.File]::ReadAllText($module)
   if(($original.Split(@([string]$mutant[2]),[StringSplitOptions]::None).Length-1) -ne 1){throw ('Mutant anchor must be unique: '+$mutant[1])}
   $changed=Join-Path $gameNvOutput ('mutant-'+$mutant[1]+'.cs');[IO.File]::WriteAllText($changed,$original.Replace($mutant[2],$mutant[3]))
   $sources=@($gameNvFrozen|ForEach-Object{if($_ -eq $module){$changed}else{$_}});$build=Build-GameNvProfile ('mutant-'+$mutant[1]) $sources
   if($build.Exit -ne 0){Record-GameNvProfile ('mutant '+$mutant[1]) $build.Exit -1 $false $build.Text;continue}
   $run=Run-GameNvProfile ('mutant-'+$mutant[1]) $build.File
   $namedFailure=if($PolicyOnly){$run.Text.Contains($mutant[4])}else{$run.Text -match 'FAIL:'}
   Record-GameNvProfile ('mutant '+$mutant[1]) 0 $run.Exit ($run.Exit -eq 1 -and $namedFailure) $run.Text
  }
 }
}catch{Record-GameNvProfile 'harness failure' -1 -1 $false $_.Exception.Message}
finally{
 $changed=@($gameNvManifest|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256})
 [pscustomobject]@{FrozenSourceCount=$gameNvManifest.Count;WorkspaceMatchesSnapshot=($changed.Count -eq 0);ChangedSources=@($changed|ForEach-Object{$_.Original});OverlayRoot=$OverlayRoot;HardThreadStageRoot=$HardThreadStageRoot;JointIntegrationStageRoot=$JointIntegrationStageRoot;PolicyOnly=[bool]$PolicyOnly;Mutations=[bool]$Mutations;MutationFilter=$MutationFilter}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $gameNvOutput 'verification.json') -Encoding UTF8
 if($changed.Count -ne 0){Record-GameNvProfile 'source stability' 0 -1 $false 'Sources changed; results apply only to retained frozen input'}
 [IO.File]::WriteAllLines((Join-Path $gameNvOutput 'results.tsv'),$gameNvResults)
 foreach($row in $gameNvResults){Write-Output $row};Write-Output ('ARTIFACT='+$gameNvOutput)
}
if($script:gameNvFailures -gt 0){exit 1};exit 0
