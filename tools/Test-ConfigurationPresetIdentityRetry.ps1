param([string]$OutputDirectory,[switch]$Mutations)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/ConfigurationPresetIdentityRetry-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Choose a fresh output directory'}
$null=New-Item -ItemType Directory -Path $output
$manifest=@('ConfigurationPresetCatalog.cs','ConfigurationPresetCoordinator.cs','ConfigurationPresetRuntime.cs')|ForEach-Object{
 $original=Join-Path $workspace ('src/PaviseApp/'+$_);$copy=Join-Path $output $_;Copy-Item -LiteralPath $original -Destination $copy
 [pscustomobject]@{Original=$original;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $copy).Hash}
}
$fixture=Join-Path $output 'ConfigurationPresetIdentityRetryChecks.cs'
Copy-Item -LiteralPath (Join-Path $workspace 'tests/ConfigurationPresetIdentityRetryChecks.cs') -Destination $fixture
$runtime=Join-Path $output 'ConfigurationPresetRuntime.cs';$source=[IO.File]::ReadAllText($runtime)
function Run-Checks([string]$name,[bool]$expectedPass){
 $exe=Join-Path $output ($name+'.exe');$rsp=Join-Path $output ($name+'.rsp')
 $inputs=@($manifest|ForEach-Object{'"'+$_.Frozen+'"'})+@('"'+$fixture+'"');[IO.File]::WriteAllLines($rsp,[string[]]$inputs)
 & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') -nologo -target:exe -optimize+ -warn:0 -codepage:65001 ('-out:'+$exe) -reference:System.dll -reference:System.Core.dll ('@'+$rsp) *> (Join-Path $output ($name+'-compile.txt'))
 if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $output ($name+'-compile.txt'));throw 'Compilation failed'}
 $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$exe;$start.WorkingDirectory=$output;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $process=[Diagnostics.Process]::Start($start)
 try{$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(15000)){$process.Kill();throw 'Owned fixture timed out'}
  $result=$stdout.Result+$stderr.Result;[IO.File]::WriteAllText((Join-Path $output ($name+'-result.txt')),$result)
  if(($process.ExitCode -eq 0) -ne $expectedPass){Write-Output $result;throw ('Unexpected result: '+$name)}
  if($expectedPass){Write-Output $result}else{Write-Output ('KILLED '+$name)}
 }finally{$process.Dispose()}
}
Run-Checks 'production' $true
$kills=0
if($Mutations){
 $cases=@(@{Name='no-failure-cap';Find='if (phase.Failures >= 3)';Replace='if (false)'},@{Name='no-success-reset';Find='phase.Failures = 0; // A complete negative observation is healthy too.';Replace='// mutation: preserve obsolete failures after complete success'},@{Name='mutation-refills-phase';Find='if (session > identitySession)';Replace='if (session >= identitySession)'},@{Name='ignore-material-config';Find='string configuration = readPhaseSnapshot() ?? "?";';Replace='string configuration = "ignored";'},@{Name='no-snapshot-cap';Find='if (phaseReadFailures >= 3 || generationReadFailures >= 3)';Replace='if (false)'},@{Name='no-apply-epoch';Find='Interlocked.Increment(ref identityEpoch);';Replace='// mutation: apply leaves the old observer epoch current'},@{Name='late-failure-clears-current';Find='if (object.ReferenceEquals(phase, identityPhase) && IdentityObservationCurrent(phase.Session, generation, observationEpoch)) current = null;';Replace='if (object.ReferenceEquals(phase, identityPhase)) current = null;'},@{Name='keep-other-material-label';Find='if (currentConfiguration != savedConfiguration) { current = null; currentConfiguration = null; }';Replace='// mutation: retain another configuration label'},@{Name='forget-verified-apply-material';Find='currentConfiguration = configuration;';Replace='currentConfiguration = null;'})
 foreach($case in $cases){if(([regex]::Matches($source,[regex]::Escape($case.Find))).Count -ne 1){throw 'Mutation anchor must match once'}
  [IO.File]::WriteAllText($runtime,$source.Replace($case.Find,$case.Replace));Run-Checks $case.Name $false;$kills++
 }
 [IO.File]::WriteAllText($runtime,$source)
}
$drift=@($manifest|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.SHA256}|Select-Object -ExpandProperty Original)
[pscustomobject]@{Passed=$true;MutationKills=$kills;ProductionSource=$manifest;SourceDrift=$drift;ProductionStarted=$false;NativeWrites=$false;RegistryWrites=$false;Fixture='Complete runtime/catalog/coordinator; memory backend; real ThreadPool plus rejected/throwing queue seam'}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
