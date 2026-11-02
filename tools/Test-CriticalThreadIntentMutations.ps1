param([Parameter(Mandatory=$true)][string]$SnapshotDirectory)
$ErrorActionPreference='Stop'
$snapshot=[IO.Path]::GetFullPath($SnapshotDirectory)
$manifest=Import-Csv -LiteralPath (Join-Path $snapshot 'inputs.csv')
$baseline=[IO.File]::ReadAllText((Join-Path $snapshot 'checks.rsp'))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output=Join-Path $snapshot ('mutations-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $output
$mutants=@(
 @('enabled-malformed-fallback','CriticalThreadAllowedRange.cs','policy.ValueOf(PolicyCatalog.KeyAutoCores) == "1"','policy.AutoCoresAllowed'),
 @('worker-default-on','CriticalThreadRule.cs','this(topology, false, processors)','this(topology, true, processors)'),
 @('same-physical-candidates','CriticalThreadRule.cs','(whole & (exclusive | reserved)) != 0','(whole & reserved) != 0'),
 @('worker-capacity-omitted','CriticalThreadRule.cs','physical.Count(x => (x & workers) != 0) < 2','physical.Count(x => (x & workers) != 0) < 0'),
 @('cache-replay-loses-confidence','CriticalThreadObservation.cs','if (copy.Ticks == previous.Ticks) { Detail = "shared snapshot has not advanced"; return true; }',''),
 @('cpu-counter-reversal-accepted','CriticalThreadObservation.cs',' || copy.ProcessCpu < previous.ProcessCpu',''),
 @('stale-sample-accepted','CriticalThreadObservation.cs','now - sample.Ticks > MaximumSampleAge','false'),
 @('foreground-gate-restored','GameMode.CriticalThreadAuthority.cs',': owner.ProfileStoreSaveFailed ? "profile store unavailable" : null;',': owner.smartSwitchedOut || owner.RendererForegroundPid() != ProcessId ? "foreground" : owner.ProfileStoreSaveFailed ? "profile store unavailable" : null;'),
 @('foreground-revoke-restored','GameMode.RendererHandoff.cs','rendererObservedForeground = foreground;','if (criticalOriginalTarget != null && foreground != criticalOriginalTarget.ProcessId) RevokeCriticalThreadIntent(); rendererObservedForeground = foreground;'),
 @('mint-single-use-replay','GameMode.CriticalThreadAuthority.cs','originalOwner.criticalMintInFlight = null;',''),
 @('pending-early-never-cleared','GameMode.CriticalThreadRules.cs','current.Pending && (criticalThreadResidue || criticalHistoricalDebt)','current.Pending'),
 @('pending-cache-never-cleared','GameMode.CriticalThreadRules.cs','!current.Pending && CriticalThreadSnapshotCurrent(current, profileId)','CriticalThreadSnapshotCurrent(current, profileId)'),
 @('parent-mask-reverse-lock-wait','GameMode.Boost.HardPin.cs','System.Threading.Monitor.TryEnter(criticalProcessGate, 0)','System.Threading.Monitor.TryEnter(criticalProcessGate, 1000)')
)
$rows=@()
foreach($mutation in $mutants){
 $source=@($manifest|Where-Object{[IO.Path]::GetFileName($_.Path) -eq $mutation[1]})
 if($source.Count -ne 1){throw ('Ambiguous source: '+$mutation[1])}
 $source=$source[0]
 if((Get-FileHash -LiteralPath $source.Frozen -Algorithm SHA256).Hash -ne $source.Hash){throw 'Frozen source changed'}
 $text=[IO.File]::ReadAllText($source.Frozen)
 if($text.Split(@([string]$mutation[2]),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation needle not unique: '+$mutation[0])}
 $directory=Join-Path $output $mutation[0];$null=New-Item -ItemType Directory -Path $directory
 $file=Join-Path $directory $mutation[1]
 [IO.File]::WriteAllText($file,$text.Replace($mutation[2],$mutation[3]),(New-Object Text.UTF8Encoding($false)))
 $exe=Join-Path $directory 'Intent.exe'
 $response=$baseline.Replace('"'+$source.Frozen+'"','"'+$file+'"') -replace '(?m)^/out:"[^"\r\n]+"',('/out:"'+$exe+'"')
 $rsp=Join-Path $directory 'checks.rsp';[IO.File]::WriteAllText($rsp,$response,(New-Object Text.UTF8Encoding($false)))
 & $compiler ('@'+$rsp) *> (Join-Path $directory 'compile.log')
 if($LASTEXITCODE -ne 0){throw ('Compile error is not a semantic kill: '+$mutation[0])}
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$exe;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $process=New-Object Diagnostics.Process;$process.StartInfo=$info
 try{
  $null=$process.Start();$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit(20000)){$process.Kill();$process.WaitForExit();throw 'Timeout is not a semantic kill'}
  $process.WaitForExit();$text=$out.Result+$err.Result
  [IO.File]::WriteAllText((Join-Path $directory 'run.log'),$text)
  $killed=$process.ExitCode -eq 1 -and $text.Contains('FAIL ')
  $rows+=[pscustomobject]@{Name=$mutation[0];Compile=0;Exit=$process.ExitCode;SemanticKill=$killed}
  $rows|Export-Csv -LiteralPath (Join-Path $output 'results.tsv') -Delimiter ([char]9) -NoTypeInformation
  if(-not $killed){throw ('Mutation did not fail semantically: '+$mutation[0])}
 }finally{$process.Dispose()}
}
Write-Output ('PASS joint intent semantic mutants='+$rows.Count+'; frozen selected pure entry only; OUTPUT '+$output)
