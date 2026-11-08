param([string]$OutputDirectory, [string]$BaselineSourcePath)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/TamerRetry-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory.'}
$null=New-Item -ItemType Directory -Path $output
$sourceFile=Join-Path $workspace 'src/PaviseApp/Tamer.cs'
$sourceHash=(Get-FileHash -LiteralPath $sourceFile).Hash
$encoding=New-Object Text.UTF8Encoding($false)
Copy-Item -LiteralPath (Join-Path $workspace 'tests/TamerRetryChecks.cs') -Destination (Join-Path $output 'TamerRetryChecks.cs')
function Extract-Method([string]$text,[string]$signature){
 if(([regex]::Matches($text,[regex]::Escape($signature))).Count -ne 1){throw ('Method must match once: '+$signature)}
 $start=$text.IndexOf($signature,[StringComparison]::Ordinal);$open=$text.IndexOf('{',$start)
 $depth=0;$state='code';$close=-1
 for($i=$open;$i -lt $text.Length;$i++){
  $c=$text[$i];$next=if($i+1 -lt $text.Length){$text[$i+1]}else{[char]0}
  if($state -eq 'line'){if($c -eq "`n"){$state='code'};continue}
  if($state -eq 'block'){if($c -eq '*' -and $next -eq '/'){$state='code';$i++};continue}
  if($state -eq 'verbatim'){if($c -eq '"'){if($next -eq '"'){$i++}else{$state='code'}};continue}
  if($state -eq 'string' -or $state -eq 'char'){if($c -eq '\'){$i++}elseif(($state -eq 'string' -and $c -eq '"') -or ($state -eq 'char' -and $c -eq "'")){$state='code'};continue}
  if($c -eq '/' -and $next -eq '/'){$state='line';$i++;continue}
  if($c -eq '/' -and $next -eq '*'){$state='block';$i++;continue}
  if($c -eq '@' -and $next -eq '"'){$state='verbatim';$i++;continue}
  if($c -eq '"'){$state='string';continue};if($c -eq "'"){$state='char';continue}
  if($c -eq '{'){$depth++}elseif($c -eq '}'){$depth--;if($depth -eq 0){$close=$i;break}}
 }
 if($close -le $open){throw 'Unbalanced method'}
 $text.Substring($start,$close-$start+1)
}
$signatures=@('private void Loop()','public void NotifyProcessChanges(ProcessChangeBatch batch)','public void Poke()','private List<ProcessChange> DrainProcessChanges()','private void RefreshGovernanceSession()','public bool PanicRestore()','public void Start()','public bool Stop()')
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
function Run-Variant([string]$label,[string]$path,[bool]$expectFailure){
 $sourceText=[IO.File]::ReadAllText($path)
 $constant=[regex]::Match($sourceText,'internal const int FailedSweepRetryMs = \d+;').Value
 if(-not $constant){throw 'Missing production failed sweep interval'}
 $methods=@($signatures | ForEach-Object {Extract-Method $sourceText $_})
 if($sourceText.Contains('private sealed class SweepRetryPhase')){
  $methods+=Extract-Method $sourceText 'private sealed class SweepRetryPhase'
  $methods+=Extract-Method $sourceText 'private SweepRetryPhase SelectSweepRetryPhase(List<ProcessChange> changes, out bool changed)'
  $methods+=Extract-Method $sourceText 'private ProcessSnapshot CaptureSweepSnapshot()'
 }else{
  $methods+='private sealed class SweepRetryPhase { internal int Failures; internal long NotBefore; }'
  $methods+='private ProcessSnapshot CaptureSweepSnapshot() { return ProcessSnapshotSource.Capture(); }'
 }
 $generated="using System;`nusing System.Collections.Generic;`nusing System.Threading;`nnamespace PaviseApp { internal sealed partial class TamerLoopFixture {`n"+$constant+"`n"+($methods -join "`n")+"`n}}"
 $generatedFile=Join-Path $output ($label+'-ProductionMethods.cs')
 [IO.File]::WriteAllText($generatedFile,$generated,$encoding)
 $exe=Join-Path $output ($label+'-TamerRetryChecks.exe')
 & $compiler -nologo -optimize+ -target:exe -platform:x64 -codepage:65001 -main:PaviseApp.TamerRetryChecks ('-out:'+$exe) -reference:System.dll -reference:System.Core.dll $generatedFile (Join-Path $output 'TamerRetryChecks.cs') *> (Join-Path $output ($label+'-compile.log'))
 if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $output ($label+'-compile.log'));throw 'Compile failed'}
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName=$exe;$start.WorkingDirectory=$output;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
 $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $process=[Diagnostics.Process]::Start($start)
 try{
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Owned fixture timed out'}
  $result=$stdout.Result+$stderr.Result
  [IO.File]::WriteAllText((Join-Path $output ($label+'-result.txt')),$result,$encoding)
  if($expectFailure){
   if($process.ExitCode -eq 0 -or -not $result.Contains('failure deadline bypassed by initial signal')){Write-Output $result;throw 'Baseline did not expose the intended retry failure'}
   Write-Output ('PASS baseline regression detected: '+$label)
  }else{Write-Output $result;if($process.ExitCode -ne 0){throw ('Fixture failed: '+$label)}}
 }finally{$process.Dispose()}
}
Run-Variant 'production' $sourceFile $false
if($BaselineSourcePath){Run-Variant 'baseline' ([IO.Path]::GetFullPath($BaselineSourcePath)) $true}
if((Get-FileHash -LiteralPath $sourceFile).Hash -ne $sourceHash){throw 'Production source changed while the fixture was running'}
[pscustomobject]@{Source=$sourceFile;SHA256=$sourceHash;MethodsCopiedVerbatim=$signatures;ProductionStarted=$false;NativeWrites=$false;Clock='real DateTime.UtcNow and AutoResetEvent';Boundary='Sweep outcomes, incremental processing and downstream collaborators are memory stubs';BaselineDetected=[bool]$BaselineSourcePath;Passed=$true}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
Write-Output ('OUTPUT '+$output)
