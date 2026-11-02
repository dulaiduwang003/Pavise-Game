param([string]$OutputDirectory,[switch]$Mutations)
$ErrorActionPreference='Stop'
$hardStage=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-HardThreadChecks-'+[Guid]::NewGuid().ToString('N'))}
$hardOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $hardOutput -Force
$hardInputNames=@(
 'src/Core/CriticalThreadGuardian.cs','src/Core/CriticalThreadGuardianCleanup.cs',
 'src/Core/CriticalThreadGuardianClose.cs',
 'src/Core/CriticalThreadHardAffinity.cs','src/Core/CriticalThreadHardAffinityStore.cs',
 'src/Core/CriticalThreadHistoricalPolicy.cs','src/Core/CriticalThreadOrigin.cs',
 'src/Core/CriticalThreadStorageDomain.cs','src/Platform/CriticalThreadGuardianCleanupNative.cs','src/Platform/CriticalThreadGuardianSweepNative.cs',
 'src/Platform/CriticalThreadGuardianNative.cs','src/Platform/CriticalThreadGuardianOriginNative.cs',
 'src/Platform/CriticalThreadHardAffinityNative.cs','src/Platform/CriticalThreadHistoricalGoneNative.cs')
$hardInputs=@($hardInputNames|ForEach-Object{Join-Path $hardStage $_})
$hardTest=Join-Path $hardStage 'tests\CriticalThreadHardAffinityChecks.cs'
$hardInputs+=@($hardTest,(Join-Path $hardStage 'tests\CriticalThreadContractStubs.cs'),$PSCommandPath)
$hardRecords=@($hardInputs|ForEach-Object{
 $hardRelative=$_.Substring($hardStage.Length+1);$hardCopy=Join-Path (Join-Path $hardOutput 'frozen') $hardRelative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($hardCopy)) -Force
 [IO.File]::WriteAllBytes($hardCopy,[IO.File]::ReadAllBytes($_))
 [pscustomobject]@{Source=$_;Frozen=$hardCopy;Hash=(Get-FileHash -LiteralPath $hardCopy).Hash}
})
$hardRecords|Export-Csv -LiteralPath (Join-Path $hardOutput 'source-sha256.csv') -NoTypeInformation
$hardCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Invoke-HardHidden([string]$file,[string]$arguments){
 $hardInfo=New-Object Diagnostics.ProcessStartInfo
 $hardInfo.FileName=$file;$hardInfo.Arguments=$arguments;$hardInfo.UseShellExecute=$false;$hardInfo.CreateNoWindow=$true
 $hardInfo.RedirectStandardOutput=$true;$hardInfo.RedirectStandardError=$true
 $hardChild=[Diagnostics.Process]::Start($hardInfo)
 try{$hardStdout=$hardChild.StandardOutput.ReadToEndAsync();$hardStderr=$hardChild.StandardError.ReadToEndAsync()
  if(-not $hardChild.WaitForExit(45000)){$hardChild.Kill();throw 'Hard affinity isolated check timed out'}
  $hardChild.WaitForExit();return [pscustomobject]@{Exit=$hardChild.ExitCode;Text=$hardStdout.Result+$hardStderr.Result}
 }finally{$hardChild.Dispose()}
}
function Compile-Hard([string[]]$files,[string]$folder,[bool]$production){
 $null=New-Item -ItemType Directory -Path $folder -Force
 $hardRsp=Join-Path $folder 'sources.rsp';[IO.File]::WriteAllLines($hardRsp,[string[]]@($files|ForEach-Object{'"'+$_+'"'}),(New-Object Text.UTF8Encoding($true)))
 $hardBinary=Join-Path $folder $(if($production){'HardThread.Contract.dll'}else{'HardThreadChecks.exe'})
 $hardFlags=if($production){'-target:library -define:PAVISE_HARD_THREAD_CONTRACT_STUB'}else{'-target:exe -define:PAVISE_SELFTEST,PAVISE_HARD_THREAD_CONTRACT_STUB -main:PaviseApp.CriticalThreadHardAffinityChecks'}
 $hardBuild=Invoke-HardHidden $hardCompiler ('-nologo -nowarn:0649,0169 -optimize+ -platform:x64 -codepage:65001 '+$hardFlags+' -reference:System.dll -reference:System.Core.dll -reference:System.Xml.dll -reference:System.Security.dll "-out:'+$hardBinary+'" "@'+$hardRsp+'"')
 [IO.File]::WriteAllText((Join-Path $folder 'compile.log'),$hardBuild.Text)
 if($hardBuild.Exit -ne 0){throw ('Hard affinity compile failed '+$hardBuild.Text)}
 return $hardBinary
}
$hardSources=@($hardRecords|Where-Object{$_.Frozen.EndsWith('.cs')}|ForEach-Object{$_.Frozen})
$hardExe=Compile-Hard $hardSources $hardOutput $false
$hardData=Join-Path $hardOutput 'own-temp-ledger'
$hardRun=Invoke-HardHidden $hardExe ('"'+$hardData+'"')
[IO.File]::WriteAllText((Join-Path $hardOutput 'result.txt'),$hardRun.Text);Write-Output $hardRun.Text
if($hardRun.Exit -ne 0){throw 'Hard affinity isolated checks failed'}
$null=Compile-Hard (@($hardSources|Where-Object{$_ -notlike '*CriticalThreadHardAffinityChecks.cs'})) (Join-Path $hardOutput 'contract-library') $true
if($Mutations){
 $hardCore=@($hardRecords|Where-Object{$_.Source -eq (Join-Path $hardStage 'src/Core/CriticalThreadHardAffinity.cs')})[0].Frozen
 $hardBase=[IO.File]::ReadAllText($hardCore)
 $hardMutants=@(
  @{Name='missing-write-ahead';Find="Save(); row = journal.Rows[journal.Rows.Count - 1];`n                RequireForwardAuthority();";Replace="row = journal.Rows[journal.Rows.Count - 1];`n                RequireForwardAuthority();";Failure='happy apply requires durable intent before every native write'},
  @{Name='guess-unknown-previous';Find='|| !row.PreviousKnown) continue;';Replace=') continue;';Failure='unknown previous must never issue restore'},
  @{Name='truncate-groups';Find='if (machine.GroupCount != 1 || machine.Group != 0)';Replace='if (machine.GroupCount < 1 || machine.Group != 0)';Failure='multi-group plan must refuse rather than truncate'},
  @{Name='same-physical-keys';Find=' || (occupied & physical) != 0';Replace='';Failure='SMT siblings must not become two key cores'},
  @{Name='restored-complete-candidate-core';Find='if (physical == 0 || (occupied & physical) != 0)';Replace='if (physical == 0 || (physical & target.AllowedMask) != physical || (occupied & physical) != 0)';Failure='candidates must use distinct physical cores'},
  @{Name='restored-complete-worker-cores';Find='if ((workers & core) != 0) capacity++;';Replace='if ((workers & core) == core) capacity++;';Failure='logical processors on at least two other physical cores required'},
  @{Name='foreign-plan';Find='if (!requested.Target.Same(gate.Target))';Replace='if (false)';Failure='foreign session plan must not grant writes'},
  @{Name='ignore-readback';Find='throw new InvalidOperationException("native apply readback is unconfirmed");';Replace=';';Failure='unverified readback must not report applied'},
  @{Name='new-worker-rolls-back';Find='incomplete = true; lastFailureKind = HardThreadFailureKind.ObservationUnavailable; continue;';Replace='throw new IOException("new worker observation unavailable");';Failure='new unknown worker rolled back the active plan'},
  @{Name='invalid-input-rolls-back';Find='return new HardThreadOperationException(HardThreadFailureKind.InvalidRequest, message);';Replace='return new HardThreadOperationException(HardThreadFailureKind.InvalidRequest, message, true);';Failure='invalid worker budget restored existing owned threads'},
  @{Name='guardian-drops-pending-proof';Module='Core\CriticalThreadGuardian.cs';Find='if (settled) return;';Replace='return;';Failure='guardian released recovery before original ownership settled'},
  @{Name='historical-pending-bypass';Module='Core\CriticalThreadHardAffinityStore.cs';Find='foreach (var row in historical.Rows) if (!row.Terminal)';Replace='foreach (var row in historical.Rows) if (false)';Failure='pending previous session permitted new forward authority'},
  @{Name='shared-root-writer';Module='Core\CriticalThreadHardAffinityStore.cs';Find='rootWriter = new FileStream(rootLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);';Replace='rootWriter = new FileStream(rootLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);';Failure='another session bypassed the original root writer admission'},
  @{Name='dispose-abandons-close-retry';Find='if (resourcesClosed) return; disposed = true;';Replace='if (disposed) return; disposed = true;';Failure='uncompleted resource closure could not be retried'},
  @{Name='held-image-blocks-settlement';Module='Core\CriticalThreadGuardianCleanup.cs';Find='if (part < HardThreadCleanupPart.Configuration || !original.HeldElsewhere(part)) return false;';Replace='return false;';Failure='a guardian image held by another process kept the settled session open'},
  @{Name='held-settling-part-deferred';Module='Core\CriticalThreadGuardianCleanup.cs';Find='if (part < HardThreadCleanupPart.Configuration || !original.HeldElsewhere(part)) return false;';Replace='if (!original.HeldElsewhere(part)) return false;';Failure='a held settling part was deferred'},
  @{Name='held-image-directory-deleted';Module='Core\CriticalThreadGuardianCleanup.cs';Find='if (part == HardThreadCleanupPart.Directory && Array.IndexOf(deferred, true) >= 0) { deferred[index] = true; continue; }';Replace='';Failure='the directory holding a deferred image was deleted or reported removed'},
  @{Name='cleanup-forgets-source-gate';Module='Core\CriticalThreadGuardianCleanup.cs';Find='if (original == null || !original.OriginalSourceReady) return false;';Replace='if (original == null) return false;';Failure='unverified original cleanup was accepted'},
  @{Name='scope-seal-not-bound';Module='Platform\CriticalThreadGuardianNative.cs';Find='Hash(platform.Unseal(value.OriginSeal)) != Hash(value.Binding())';Replace='Hash(platform.Unseal(value.OriginSeal)) != Hash(platform.Unseal(value.OriginSeal))';Failure='changed or legacy source bypassed the original machine sealed binding'},
  @{Name='historical-alive-is-gone';Module='Core\CriticalThreadHistoricalPolicy.cs';Find='if (fact.Birth != target.ProcessCreation)';Replace='if (fact.Birth == target.ProcessCreation)';Failure='live original process was marked historical gone'},
  @{Name='historical-access-denied-is-missing';Module='Core\CriticalThreadHistoricalPolicy.cs';Find='first.Error == 87 && second.Error == 87';Replace='(first.Error == 87 || first.Error == 5) && (second.Error == 87 || second.Error == 5)';Failure='unknown denied wrong-owner or wait failure became a historical gone fact'},
  @{Name='scope-header-not-checked';Module='Core\CriticalThreadHardAffinityStore.cs';Find='else if (journal.SourceNonce != sourceNonce || journal.SourceScopeHash != sourceScopeHash)';Replace='else if (false)';Failure='historical or recovery source used a foreign scope hash'},
  @{Name='origin-machine-not-checked';Module='Core\CriticalThreadOrigin.cs';Find='other != null && Machine == other.Machine && Name == other.Name';Replace='other != null';Failure='a different Windows machine identity accepted a copied original scope'},
  @{Name='setup-failure-left-orphaned';Module='Core\CriticalThreadGuardianCleanup.cs';Find='return !spawnSucceeded || originalChild == HardThreadRead.Gone;';Replace='return originalChild == HardThreadRead.Gone;';Failure='original setup files remained orphaned after synchronous spawn failure'},
  @{Name='setup-controller-debt-omitted';Module='Core\CriticalThreadHardAffinityStore.cs';Find='if (CriticalThreadGuardian.HasControllerDebt(canonicalRoot)) return true;';Replace=';';Failure='an in memory setup cleanup controller was omitted from canonical debt'},
  @{Name='controller-final-admission-bypass';Module='Platform\CriticalThreadGuardianCleanupNative.cs';Find='if (!existing.controllerReleased && string.Equals(existing.ControlledRoot, controller.ControlledRoot, StringComparison.OrdinalIgnoreCase))';Replace='if (false)';Failure='a pending setup controller without a WAL permitted another original factory admission'},
  @{Name='storage-network-accepted';Module='Core\CriticalThreadStorageDomain.cs';Find='driveType != DriveType.Fixed && driveType != DriveType.Removable && driveType != DriveType.Ram';Replace='driveType != DriveType.Fixed && driveType != DriveType.Removable && driveType != DriveType.Ram && driveType != DriveType.Network';Failure='a mapped network or unknown data directory permitted new forward execution'},
  @{Name='storage-path-shape-bypass';Module='Core\CriticalThreadStorageDomain.cs';Find="if (string.IsNullOrEmpty(canonicalRoot) || canonicalRoot.Length < 3`n                || !((canonicalRoot[0] >= 'A' && canonicalRoot[0] <= 'Z') || (canonicalRoot[0] >= 'a' && canonicalRoot[0] <= 'z'))`n                || canonicalRoot[1] != ':' || canonicalRoot[2] != '\\')";Replace='if (false)';Failure='a UNC or unsupported source path reached the forward storage probe'},
  @{Name='storage-probe-failure-accepted';Module='Core\CriticalThreadStorageDomain.cs';Find='throw new HardThreadStorageDomainException("The local Pavise data directory could not be confirmed", error);';Replace='return;';Failure='a failed storage domain probe permitted new forward execution'},
  @{Name='storage-failure-type-erased';Module='Core\CriticalThreadStorageDomain.cs';Find='throw new HardThreadStorageDomainException(';Replace='throw new NotSupportedException(';Failure='a storage refusal lost its dedicated failure type'},
  @{Name='historical-session-budget-bypass';Module='Core\CriticalThreadHistoricalPolicy.cs';Find='sessions >= 8';Replace='sessions > 8';Failure='historical batch exceeded eight unsettled sessions'},
  @{Name='historical-image-budget-bypass';Module='Core\CriticalThreadHistoricalPolicy.cs';Find='imageBytes > 67108864 - bytes';Replace='false';Failure='historical image reads exceeded the total bounded budget'}
 )
 $hardMutationResults=@()
 foreach($hardMutant in $hardMutants){
  $hardModule=$hardCore
  if($hardMutant.Module){$hardModule=@($hardRecords|Where-Object{$_.Source -eq (Join-Path (Join-Path $hardStage 'src') $hardMutant.Module)})[0].Frozen}
  $hardNormalized=[IO.File]::ReadAllText($hardModule).Replace("`r`n","`n")
  if(-not $hardNormalized.Contains($hardMutant.Find)){throw ('Missing exact mutation anchor '+$hardMutant.Name)}
  $hardFolder=Join-Path $hardOutput $hardMutant.Name;$null=New-Item -ItemType Directory -Path $hardFolder
  $hardShadow=Join-Path $hardFolder ([IO.Path]::GetFileName($hardModule))
  [IO.File]::WriteAllText($hardShadow,$hardNormalized.Replace($hardMutant.Find,$hardMutant.Replace),(New-Object Text.UTF8Encoding($false)))
  $hardMutantExe=Compile-Hard (@($hardSources|Where-Object{$_ -ne $hardModule})+@($hardShadow)) $hardFolder $false
  $hardMutantRun=Invoke-HardHidden $hardMutantExe ('"'+(Join-Path $hardFolder 'own-temp-ledger')+'"')
  [IO.File]::WriteAllText((Join-Path $hardFolder 'result.txt'),$hardMutantRun.Text)
  $hardRejected=$hardMutantRun.Exit -eq 1 -and $hardMutantRun.Text.Contains($hardMutant.Failure)
  $hardMutationResults+=[pscustomobject]@{Name=$hardMutant.Name;CompileExit=0;RunExit=$hardMutantRun.Exit;NamedFailure=$hardRejected}
  if(-not $hardRejected){throw ('Hard affinity mutation not rejected '+$hardMutant.Name+' '+$hardFolder)}
 }
 $hardMutationResults|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $hardOutput 'mutations.json') -Encoding UTF8
 Write-Output ('PASS hard-thread semantic mutants='+$hardMutationResults.Count)
}
$hardChanged=@($hardRecords|Where-Object{(Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$hardWorkspaceChanged=@($hardRecords|Where-Object{(Get-FileHash -LiteralPath $_.Source).Hash -ne $_.Hash})
[pscustomobject]@{Compile=0;Run=$hardRun.Exit;Pure=$true;Native=$false;BridgeActive=$false;UserThreads=$false;Windows=$false;ProductExecuted=$false;InputsUnchanged=$hardChanged.Count -eq 0;StageUnchanged=$hardWorkspaceChanged.Count -eq 0}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $hardOutput 'verification.json') -Encoding UTF8
Write-Output ('OUTPUT '+$hardOutput)
if($hardChanged.Count -ne 0 -or $hardWorkspaceChanged.Count -ne 0){throw 'Hard affinity tested source changed'}
