param([string]$WorkspaceRoot,[string]$TestSourceRoot,[string]$OutputDirectory,[hashtable]$SourceOverrides=@{},[switch]$Mutations)
$ErrorActionPreference='Stop'
if(-not $WorkspaceRoot){$WorkspaceRoot=Join-Path $PSScriptRoot '..'}
$closeWorkspace=[IO.Path]::GetFullPath($WorkspaceRoot)
if(-not $TestSourceRoot){$TestSourceRoot=Join-Path $closeWorkspace 'tests'}
$closeTest=[IO.Path]::GetFullPath((Join-Path $TestSourceRoot 'GuardianCloseChecks.cs'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $closeWorkspace ('build\GuardianCloseChecks\'+[Guid]::NewGuid().ToString('N'))}
$closeOutput=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath $closeOutput) -and @(Get-ChildItem -LiteralPath $closeOutput -Force).Count -ne 0){throw 'Guardian close output directory must be empty'}
$null=New-Item -ItemType Directory -Path $closeOutput -Force
function Get-CloseSourceMap {
 $map=@{}
 Get-ChildItem -LiteralPath (Join-Path $closeWorkspace 'src') -Recurse -File -Filter '*.cs'|ForEach-Object{$map[$_.FullName.Substring($closeWorkspace.Length+1)]=$_.FullName}
 foreach($key in $SourceOverrides.Keys){
  $target=([string]$key).Replace('/','\')
  if(-not $target.StartsWith('src\',[StringComparison]::OrdinalIgnoreCase) -or ($target.Split('\') -contains '..') -or [IO.Path]::GetExtension($target) -ne '.cs'){throw 'Guardian close override requires a relative src C sharp target'}
  $map[$target]=[IO.Path]::GetFullPath([string]$SourceOverrides[$key])
 }
 $map['tests\GuardianCloseChecks.cs']=$closeTest
 return $map
}
$closeMap=Get-CloseSourceMap
$closeRecords=@($closeMap.Keys|Sort-Object|ForEach-Object{
 $source=$closeMap[$_];$frozen=Join-Path (Join-Path $closeOutput 'frozen') $_
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
 $hash=(Get-FileHash -LiteralPath $source).Hash;[IO.File]::WriteAllBytes($frozen,[IO.File]::ReadAllBytes($source))
 if((Get-FileHash -LiteralPath $frozen).Hash -ne $hash){throw 'Guardian close input changed while freezing'}
 [pscustomobject]@{Target=$_;Source=$source;Frozen=$frozen;Hash=$hash}
})
$closeRecords|Export-Csv -LiteralPath (Join-Path $closeOutput 'source-sha256.csv') -NoTypeInformation
$closeToolHash=(Get-FileHash -LiteralPath $PSCommandPath).Hash
[IO.File]::WriteAllBytes((Join-Path $closeOutput 'Test-GuardianClose.ps1'),[IO.File]::ReadAllBytes($PSCommandPath))
if((Get-FileHash -LiteralPath (Join-Path $closeOutput 'Test-GuardianClose.ps1')).Hash -ne $closeToolHash){throw 'Guardian close tool changed while freezing'}
$closeSources=@($closeRecords|ForEach-Object{$_.Frozen})
$closeCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Invoke-CloseHidden([string]$file,[string]$arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$file;$info.Arguments=$arguments;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=[Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage);$info.StandardErrorEncoding=$info.StandardOutputEncoding
 $child=[Diagnostics.Process]::Start($info)
 try{$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(50000)){$child.Kill();$child.WaitForExit();throw 'Guardian close isolated check timed out'}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$stdout.Result+$stderr.Result}
 }finally{$child.Dispose()}
}
function Compile-Close([string[]]$sources,[string]$folder,[bool]$production){
 $null=New-Item -ItemType Directory -Path $folder -Force
 $rsp=Join-Path $folder 'sources.rsp'
 [IO.File]::WriteAllLines($rsp,[string[]]@($sources|ForEach-Object{'"'+$_+'"'}),(New-Object Text.UTF8Encoding($true)))
 $binary=Join-Path $folder $(if($production){'GuardianClose.Production.dll'}else{'GuardianCloseChecks.exe'})
 $mode=if($production){'-target:library'}else{'-target:exe -define:PAVISE_SELFTEST,PAVISE_GUARDIAN_CLOSE_CHECKS -main:PaviseApp.GuardianCloseChecks'}
 $built=Invoke-CloseHidden $closeCompiler ('-nologo -unsafe+ -nowarn:0162,0169,0414,0649 -optimize+ -platform:x64 -codepage:65001 '+$mode+' -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll "-out:'+$binary+'" "@'+$rsp+'"')
 [IO.File]::WriteAllText((Join-Path $folder 'compile.log'),$built.Text)
 if($built.Exit -ne 0){throw ('Guardian close compile failed '+$built.Text)}
 return $binary
}
$closeProduction=Compile-Close $closeSources (Join-Path $closeOutput 'production') $true
$closeExe=Compile-Close $closeSources (Join-Path $closeOutput 'pure') $false
$closeRun=Invoke-CloseHidden $closeExe ('"'+$closeProduction+'"')
[IO.File]::WriteAllText((Join-Path $closeOutput 'result.txt'),$closeRun.Text);Write-Output $closeRun.Text
if($closeRun.Exit -ne 0){throw 'Guardian close pure contract failed'}
if($Mutations){
 $mutants=@(
  @{Name='skip-later-originals';Module='src\Core\CriticalThreadGuardianClose.cs';Find='catch (Exception error) { failures.Add(error); }';Replace='catch (Exception error) { failures.Add(error); break; }';Failure='a failed closer prevented later original resources from being attempted'},
  @{Name='failed-slot-closed';Module='src\Core\CriticalThreadGuardianClose.cs';Find='catch (Exception error) { failures.Add(error); }';Replace='catch (Exception error) { failures.Add(error); closed[index] = true; }';Failure='a failed original resource was falsely reported closed'},
  @{Name='successful-slot-repeated';Module='src\Core\CriticalThreadGuardianClose.cs';Find='if (closed[index]) continue;';Replace=';';Failure='successful resource closure was repeated during retry'},
  @{Name='original-array-replaced';Module='src\Core\CriticalThreadGuardianClose.cs';Find='original = (IDisposable[])resources.Clone();';Replace='original = resources;';Failure='original close fault disappeared'},
  @{Name='raw-number-retained';Module='src\Core\CriticalThreadGuardianClose.cs';Find='IntPtr original = handle; handle = IntPtr.Zero;';Replace='IntPtr original = handle;';Failure='unconfirmed raw closure lost its fault or skipped another original resource'},
  @{Name='raw-latch-forgotten';Module='src\Core\CriticalThreadGuardianClose.cs';Find="if (uncertain) throw new HardThreadClosureFailure(`"Original handle closure is unconfirmed`");`n            if (handle == IntPtr.Zero) return;";Replace='if (handle == IntPtr.Zero) return;';Failure='original close fault disappeared'},
  @{Name='exit-failure-escapes';Module='src\Core\CriticalThreadGuardianClose.cs';Find='catch { exitCode = 14; }';Replace='catch { throw; }';Failure='cleanup failure escaped the guardian exit boundary'},
  @{Name='exit-failure-is-success';Module='src\Core\CriticalThreadGuardianClose.cs';Find='catch { exitCode = 14; }';Replace='catch { exitCode = 0; }';Failure='guardian cleanup failure did not preserve failure exit and remaining obligations'},
  @{Name='registration-root-omitted';Module='src\Platform\CriticalThreadGuardianNative.cs';Find='new HardThreadResourceClosure(Pipe, Source, Target, Parent, Root)';Replace='new HardThreadResourceClosure(Pipe, Source, Target, Parent, null)';Failure='actual registration did not retain every original close obligation'},
  @{Name='target-close-failure-ignored';Module='src\Platform\CriticalThreadGuardianNative.cs';Find='public void Dispose() { HardThreadRawHandleClosure.Close(ref Handle, ref closeFault, CloseHandle); }';Replace='public void Dispose() { Handle = IntPtr.Zero; }';Failure='actual registration did not retain every original close obligation'},
  @{Name='production-finally-bypassed';Module='src\Platform\CriticalThreadGuardianNative.cs';Find='HardThreadGuardianExit.CloseForExit(original, ref exitCode);';Replace='if (original != null) original.Dispose();';Failure='actual production RunNative finally bypassed its protected close boundary'},
  @{Name='spawn-first-error-skips-second';Module='src\Core\CriticalThreadGuardianClose.cs';Find='catch (Exception threadFailure) { errors.Add(threadFailure); }';Replace='catch (Exception threadFailure) { errors.Add(threadFailure); throw; }';Failure='spawn first close failure prevented the second original handle attempt'},
  @{Name='spawn-process-closed-too-early';Module='src\Core\CriticalThreadGuardianClose.cs';Find='if (releaseProcess) try';Replace='if (true) try';Failure='spawn process was closed before an original child pin existed'},
  @{Name='pin-image-error-skips-raw';Module='src\Core\CriticalThreadGuardianClose.cs';Find='catch (Exception image) { errors.Add(new HardThreadClosureFailure("Original image source closure is unconfirmed", image)); }';Replace='catch (Exception image) { errors.Add(new HardThreadClosureFailure("Original image source closure is unconfirmed", image)); throw; }';Failure='image close failure skipped the original raw handle attempt'},
  @{Name='constructor-cleanup-error-hidden';Module='src\Core\CriticalThreadGuardianClose.cs';Find="catch (Exception cleanup) { throw new AggregateException(`"Original pin construction failed with unconfirmed cleanup`", cause,`n                new HardThreadClosureFailure(`"Unassigned original pin closure is unconfirmed`", cleanup)); }";Replace='catch (Exception cleanup) { return; }';Failure='constructor cleanup failure was hidden or lost its original verification cause'},
  @{Name='temporary-close-uncertainty-untyped';Module='src\Core\CriticalThreadGuardianClose.cs';Find='throw new HardThreadClosureFailure("Original temporary source closure is unconfirmed", cleanup);';Replace='throw cleanup;';Failure='temporary source cleanup uncertainty was swallowed'},
  @{Name='ordinary-io-permanently-poisoned';Module='src\Platform\CriticalThreadGuardianCleanupNative.cs';Find='if (!HardThreadClosureFailure.Contains(error)) return;';Replace='if (error == null) return;';Failure='ordinary permission or verification IO permanently poisoned a parent controller'},
  @{Name='pin-constructor-cleanup-bypassed';Module='src\Platform\CriticalThreadGuardianNative.cs';Find='HardThreadConstructionCleanup.CloseFailed(this, error);';Replace=';';Failure='actual pin constructor bypassed protected construction cleanup'},
  @{Name='actual-spawn-pair-not-closed';Module='src\Platform\CriticalThreadGuardianCleanupNative.cs';Find='HardThreadSpawnHandleClosure.Close(ref SpawnThread, ref spawnThreadCloseFault, ref UnverifiedSpawn, ref spawnProcessCloseFault, releaseProcess, CloseHandle);';Replace='SpawnThread = IntPtr.Zero; UnverifiedSpawn = IntPtr.Zero;';Failure='actual parent spawn pair discarded an independent latched fault'}
 )
 $results=@()
 foreach($mutant in $mutants){
  $module=@($closeRecords|Where-Object{$_.Target -eq $mutant.Module})[0].Frozen
  $source=[IO.File]::ReadAllText($module).Replace("`r`n","`n")
  if(-not $source.Contains($mutant.Find)){throw ('Missing guardian close mutation anchor '+$mutant.Name)}
  $folder=Join-Path $closeOutput $mutant.Name;$null=New-Item -ItemType Directory -Path $folder
  $shadow=Join-Path $folder ([IO.Path]::GetFileName($module))
  [IO.File]::WriteAllText($shadow,$source.Replace($mutant.Find,$mutant.Replace),(New-Object Text.UTF8Encoding($false)))
  $variant=@($closeSources|Where-Object{$_ -ne $module})+@($shadow)
  $library=Compile-Close $variant (Join-Path $folder 'production') $true
  $runner=Compile-Close $variant (Join-Path $folder 'pure') $false
  $run=Invoke-CloseHidden $runner ('"'+$library+'"')
  [IO.File]::WriteAllText((Join-Path $folder 'result.txt'),$run.Text)
  $killed=$run.Exit -eq 1 -and $run.Text.Contains($mutant.Failure)
  $results+=[pscustomobject]@{Name=$mutant.Name;ProductionCompile=0;PureCompile=0;Run=$run.Exit;NamedFailure=$killed;ProductionExecuted=$false}
  if(-not $killed){throw ('Guardian close mutation did not hit its named failure '+$mutant.Name+' '+$run.Text)}
 }
 $results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $closeOutput 'mutations.json') -Encoding UTF8
 Write-Output ('PASS guardian close mutants='+$results.Count)
}
$frozenChanges=@($closeRecords|Where-Object{(Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$sourceChanges=@($closeRecords|Where-Object{(Get-FileHash -LiteralPath $_.Source).Hash -ne $_.Hash})
$current=Get-CloseSourceMap;$setChanged=$current.Count -ne $closeMap.Count
foreach($key in $closeMap.Keys){if($current[$key] -ne $closeMap[$key]){$setChanged=$true}}
$toolChanged=(Get-FileHash -LiteralPath $PSCommandPath).Hash -ne $closeToolHash
[pscustomobject]@{ProductionCompile=0;PureCompile=0;PureRun=$closeRun.Exit;Sources=$closeRecords.Count;FrozenChanges=$frozenChanges.Count;SourceChanges=$sourceChanges.Count;SourceSetChanged=$setChanged;ToolChanged=$toolChanged;ProductionReflectionOnly=$true;ProductionExecuted=$false;AppMainExecuted=$false;Native=$false;Dpapi=$false;UserSampling=$false;Restart=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $closeOutput 'verification.json') -Encoding UTF8
Write-Output ('OUTPUT '+$closeOutput)
if($frozenChanges.Count -ne 0 -or $sourceChanges.Count -ne 0 -or $setChanged -or $toolChanged){throw 'Guardian close tested input changed'}
