param([string]$WorkspaceRoot,[string]$SourceFile,[string]$SamplingFile,[switch]$Mutations)
$ErrorActionPreference='Stop'
if(-not $WorkspaceRoot){$WorkspaceRoot=Split-Path $PSScriptRoot -Parent}
if(-not $SourceFile){$SourceFile=Join-Path $WorkspaceRoot 'src/Core/Suppression/RenderLane.cs'}
if(-not $SamplingFile){$SamplingFile=Join-Path $WorkspaceRoot 'src/Core/Suppression/RenderLane.Sampling.cs'}
$test=Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/RenderLaneIdentityChecks.cs'
$output=Join-Path $env:TEMP ('Pavise-RenderLaneIdentity-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $output
$sourcePaths=@([IO.Path]::GetFullPath($SourceFile),[IO.Path]::GetFullPath($SamplingFile),[IO.Path]::GetFullPath($test))
$pins=@($sourcePaths|ForEach-Object{[pscustomobject]@{Original=$_;Frozen=(Join-Path $output ([IO.Path]::GetFileName($_)));Hash=(Get-FileHash -LiteralPath $_).Hash}})
foreach($pin in $pins){[IO.File]::Copy($pin.Original,$pin.Frozen);if((Get-FileHash -LiteralPath $pin.Frozen).Hash -ne $pin.Hash){throw 'Render lane source changed while freezing'}}
$pins|Export-Csv -LiteralPath (Join-Path $output 'inputs.csv') -NoTypeInformation
function Invoke-Entry([string]$name,[string]$source,[string]$sampling){
 if(-not $sampling){$sampling=$pins[1].Frozen}
 $exe=Join-Path $output ($name+'.exe');$rsp=Join-Path $output ($name+'.rsp')
 [IO.File]::WriteAllLines($rsp,@('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/define:PAVISE_RENDERLANE_IDENTITY_CHECKS','/main:PaviseApp.RenderLaneIdentityChecks',('/out:"'+$exe+'"'),'/reference:System.dll',('"'+$source+'"'),('"'+$sampling+'"'),('"'+$pins[2].Frozen+'"')),(New-Object Text.UTF8Encoding($false)))
 & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') ('@'+$rsp) *> (Join-Path $output ($name+'.compile.log'))
 if($LASTEXITCODE -ne 0){throw ('Compile failure is not a semantic result: '+$name)}
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$exe;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{
  $null=$child.Start();$a=$child.StandardOutput.ReadToEndAsync();$b=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(60000)){$child.Kill();$child.WaitForExit();throw 'Timeout is not a semantic result'}
  $child.WaitForExit();$text=$a.Result+$b.Result;[IO.File]::WriteAllText((Join-Path $output ($name+'.log')),$text)
  return [pscustomobject]@{Name=$name;Compile=0;Exit=$child.ExitCode;Text=$text;Expected=''}
 }finally{$child.Dispose()}
}
$baseline=Invoke-Entry 'baseline' $pins[0].Frozen
Write-Output $baseline.Text
if($baseline.Exit -ne 0){Write-Output ('OUTPUT '+$output);throw 'Render lane identity restoration failed its fixture'}
$rows=@($baseline|Select-Object Name,Compile,Exit,Expected)
if($Mutations){
 $text=[IO.File]::ReadAllText($pins[0].Frozen).Replace("`r`n","`n")
 $ind='            '
 $healCall='RestoreResult restored = RestoreThread(pid, creation, tid, threadCreation, original);'+"`n"+$ind+'if (restored == RestoreResult.Failed) return false;'
 $healZero='RestoreResult restored = RestoreThread(pid, creation, tid, 0, original);'+"`n"+$ind+'if (restored == RestoreResult.Failed) return false;'
 $saveHead='if (!Settings.TryLoadStr("RenderLane", out stored) || stored.Length != 0) return false;'+"`n"+$ind+'string line = JournalLine(pid, creation, tid, original);'+"`n"+$ind+'return SaveThreadBirth(line, threadCreation) && Settings.SaveStr("RenderLane", line)'
 $saveLate='string line = JournalLine(pid, creation, tid, original);'+"`n"+$ind+'if (!SaveThreadBirth(line, threadCreation) || !Settings.TryLoadStr("RenderLane", out stored) || stored.Length != 0) return false;'+"`n"+$ind+'return Settings.SaveStr("RenderLane", line)'
 $restoredLane='Lang.T("log.renderlane.19") + tid + Lang.T("log.renderlane.20") + original'
 $restoredHeal='Lang.T("log.renderlane.23") + tid + Lang.T("log.renderlane.24") + original'
 $mutants=@(
  [pscustomobject]@{Name='pin-birth-missing';From='string value = threadCreation > 0 ? line + "|" + threadCreation : "", stored;';To='string value = "", stored;';Expect='a new pin did not record the original thread creation time'},
  [pscustomobject]@{Name='main-five-fields';From='string line = JournalLine(pid, creation, tid, original);';To='string line = JournalLine(pid, creation, tid, original) + "|" + threadCreation;';Expect='the primary journal is no longer the legacy four-field receipt'},
  [pscustomobject]@{Name='reused-tid-written';From='if (born != threadCreation || exited != 0)';To='if (exited != 0)';Expect='release wrote the old priority onto a new thread that reused the TID'},
  [pscustomobject]@{Name='exited-thread-written';From='|| exited != 0) return RestoreResult.Released;';To=') return RestoreResult.Released;';Expect='release wrote to an exited original thread or kept its debt'},
  [pscustomobject]@{Name='identity-failure-acknowledged';From='if (!Native.GetThreadTimes(h, out born, out exited, out kernel, out user)) return RestoreResult.Failed;';To='if (!Native.GetThreadTimes(h, out born, out exited, out kernel, out user)) return RestoreResult.Released;';Expect='an unreadable thread identity was acknowledged as restored'},
  [pscustomobject]@{Name='expected-value-dropped';From='if (current != Native.THREAD_PRIORITY_HIGHEST) return RestoreResult.Released;';To='';Expect='release overwrote a priority the game changed after the boost'},
  [pscustomobject]@{Name='priority-failure-acknowledged';From='if (current == Native.THREAD_PRIORITY_ERROR_RETURN) return RestoreResult.Failed;';To='';Expect='an unreadable current priority was acknowledged'},
  [pscustomobject]@{Name='legacy-blind-restore';From='if (threadCreation <= 0) return RestoreResult.Failed;';To='';Expect='a legacy receipt without thread identity was written while its owner was unprovable'},
  [pscustomobject]@{Name='heal-birth-dropped';From=$healCall;To=$healZero;Expect='crash recovery kept a gone thread''s debt or logged it as restored'},
  [pscustomobject]@{Name='birth-unbound-accepted';From='string prefix = line + "|";';To='string prefix = stored.Substring(0, stored.LastIndexOf(''|'') + 1);';Expect='a creation record bound to another receipt was trusted'},
  [pscustomobject]@{Name='birth-unreadable-acknowledged';From='if (!Settings.TryLoadStr(ThreadBirthKey, out stored)) return false;';To='if (!Settings.TryLoadStr(ThreadBirthKey, out stored)) return true;';Expect='an unreadable creation record was acknowledged'},
  [pscustomobject]@{Name='birth-before-empty-check';From=$saveHead;To=$saveLate;Expect='a refused pin replaced the creation record of an existing debt'},
  [pscustomobject]@{Name='birth-save-ignored';From='return SaveThreadBirth(line, threadCreation) && Settings.SaveStr("RenderLane", line)';To='SaveThreadBirth(line, threadCreation); return Settings.SaveStr("RenderLane", line)';Expect='a pin continued after its creation record could not be saved'},
  [pscustomobject]@{Name='main-before-birth';From='return SaveThreadBirth(line, threadCreation) && Settings.SaveStr("RenderLane", line)';To='return Settings.SaveStr("RenderLane", line) && SaveThreadBirth(line, threadCreation)';Expect='a failed creation record left a primary journal behind'},
  [pscustomobject]@{Name='verify-birth-dropped';From='if (RestoreThread(pid, creation, tid, threadCreation, original) == RestoreResult.Failed)';To='if (RestoreThread(pid, creation, tid, 0, original) == RestoreResult.Failed)';Expect='re-verification kept a gone thread''s debt'},
  [pscustomobject]@{Name='lane-birth-not-stored';From='laneTid = best.Tid; laneThreadCreation = best.ThreadCreation;';To='laneTid = best.Tid;';Expect='the original thread was not restored on release'},
  [pscustomobject]@{Name='release-logged-as-restored';From=($restoredLane+' : ReleasedLine(tid));');To=($restoredLane+' : '+$restoredLane+');');Expect='an unwritten release was logged as a restoration'},
  [pscustomobject]@{Name='heal-logged-as-restored';From=($restoredHeal+' : ReleasedLine(tid));');To=($restoredHeal+' : '+$restoredHeal+');');Expect='crash recovery kept a gone thread''s debt or logged it as restored'},
  [pscustomobject]@{Name='birth-not-cleared';From='Settings.SaveStr(ThreadBirthKey, "");';To='';Expect='a settled receipt kept its thread creation record'}
 )
 foreach($mutation in $mutants){
  if($text.Split(@([string]$mutation.From),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation anchor must be unique: '+$mutation.Name)}
  $file=Join-Path $output ($mutation.Name+'.cs');[IO.File]::WriteAllText($file,$text.Replace($mutation.From,$mutation.To),(New-Object Text.UTF8Encoding($false)))
  $result=Invoke-Entry $mutation.Name $file
  $result.Expected=$mutation.Expect
  if($result.Exit -ne 1 -or -not $result.Text.Contains('FAIL '+$mutation.Expect)){throw ('Mutation did not fail at its intended check: '+$mutation.Name+' | '+$result.Text.Trim())}
  $rows+=$result|Select-Object Name,Compile,Exit,Expected
 }
 # Sampling must read thread times from the snapshot instead of opening every game thread.
 $samplingText=[IO.File]::ReadAllText($pins[1].Frozen).Replace("`r`n","`n")
 $samplingFrom='into[(int)native.Ids[i]] = new ThreadSample { Creation = native.Created[i], CpuTicks = native.Cpu[i] };'
 if($samplingText.Split(@($samplingFrom),[StringSplitOptions]::None).Length -ne 2){throw 'Mutation anchor must be unique: sampling-opens-threads'}
 $samplingFile=Join-Path $output 'sampling-opens-threads.cs'
 [IO.File]::WriteAllText($samplingFile,$samplingText.Replace($samplingFrom,'ReadAccessibleThread(pid, (int)native.Ids[i], native.Created[i], into, ref status);'),(New-Object Text.UTF8Encoding($false)))
 $result=Invoke-Entry 'sampling-opens-threads' $pins[0].Frozen $samplingFile
 $result.Expected='thread sampling opened game thread handles'
 if($result.Exit -ne 1 -or -not $result.Text.Contains('FAIL '+$result.Expected)){throw ('Mutation did not fail at its intended check: sampling-opens-threads | '+$result.Text.Trim())}
 $rows+=$result|Select-Object Name,Compile,Exit,Expected
}
$changed=@($pins|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.Hash -or (Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$rows|Export-Csv -LiteralPath (Join-Path $output 'results.csv') -NoTypeInformation
[pscustomobject]@{Baseline=0;MutationKills=$rows.Count-1;SourceChanges=$changed.Count;Native=$false;GUI=$false;ProductMain=$false;Preferences='in-memory';Output=$output}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($changed.Count){throw 'Render lane identity test inputs changed'}
Write-Output ('PASS render lane identity mutations='+($rows.Count-1)+'; OUTPUT '+$output)
