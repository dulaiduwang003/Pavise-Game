param([string]$OutputDirectory, [switch]$ExhaustionMutation)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/SmartGovernanceThreshold-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory.'}
$null=New-Item -ItemType Directory -Path $output
$manifest=@()
foreach($relative in @('src/Scheduling/Cpu/CpuSaturation.cs','src/Tuning/Tweaks/System/AdaptiveGuard.cs','src/Game/Mode/Background/GameMode.Background.cs','tests/SmartGovernanceThresholdChecks.cs')){
 $source=Join-Path $workspace $relative
 $copy=Join-Path $output ([IO.Path]::GetFileName($relative))
 Copy-Item -LiteralPath $source -Destination $copy
 $manifest+=[pscustomobject]@{Source=$source;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $copy).Hash}
}
$manifest|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'source-manifest.json') -Encoding UTF8
$cpuPath=Join-Path $output 'CpuSaturation.cs'
$cpuText=[IO.File]::ReadAllText($cpuPath)
$timePattern='\[DllImport\("kernel32\.dll", SetLastError = true\)\]\s*private static extern bool GetSystemTimes\(out long idle, out long kernel, out long user\);'
$groupPattern='\[DllImport\("kernel32\.dll"\)\]\s*private static extern ushort GetActiveProcessorGroupCount\(\);'
if([regex]::Matches($cpuText,$timePattern).Count -ne 1 -or [regex]::Matches($cpuText,$groupPattern).Count -ne 1){throw 'CPU native fixture boundaries must each match once.'}
$cpuText=[regex]::Replace($cpuText,$timePattern,'private static bool GetSystemTimes(out long idle, out long kernel, out long user) { return SmartGovernanceThresholdChecks.ReadNativeTimes(out idle, out kernel, out user); }')
$cpuText=[regex]::Replace($cpuText,$groupPattern,'private static ushort GetActiveProcessorGroupCount() { return SmartGovernanceThresholdChecks.ReadNativeGroups(); }')
[IO.File]::WriteAllText($cpuPath,$cpuText,(New-Object Text.UTF8Encoding($false)))
$modePath=Join-Path $output 'GameMode.Background.cs'
$modeText=[IO.File]::ReadAllText($modePath)
$fixturePath=Join-Path $output 'SmartGovernanceThresholdChecks.cs'
$fixtureText=[IO.File]::ReadAllText($fixturePath)
foreach($name in @('step','reset')){
 $method=if($name-eq 'step'){'StepAdaptiveGuard'}else{'ResetAdaptiveGuard'}
 $body=[regex]::Matches($modeText,('(?ms)^        private void '+$method+'\(\).*?^        \}'))
 if($body.Count-ne 1){throw ('Adaptive production method not found: '+$method)}
 $fixtureText=$fixtureText.Replace(('        // Insert production '+$name),$body[0].Value)
}
if($ExhaustionMutation){
 $anchor='if (!adaptiveGuard.Escalated && adaptiveGuard.Episodes >= AdaptiveGuard.MaxEpisodes) return;'
 if([regex]::Matches($fixtureText,[regex]::Escape($anchor)).Count-ne 1){throw 'Adaptive mutation boundary changed'}
 $fixtureText=$fixtureText.Replace($anchor,'')
}
$generated=Join-Path $output 'AdaptiveFixture.cs'
[IO.File]::WriteAllText($generated,$fixtureText,(New-Object Text.UTF8Encoding($false)))
$compiled=@($manifest.Frozen|Where-Object{$_ -ne $modePath -and $_ -ne $fixturePath})+@($generated)
$exe=Join-Path $output 'SmartGovernanceThresholdChecks.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /optimize+ /platform:x64 /codepage:65001 /main:PaviseApp.SmartGovernanceThresholdChecks ('/out:'+$exe) /reference:System.dll /reference:System.Core.dll $compiled *> (Join-Path $output 'compile.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $output 'compile.log');throw 'Threshold checks compile failed'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$exe;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$child=[Diagnostics.Process]::Start($start)
try{
 $stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
 if(-not $child.WaitForExit(10000)){$child.Kill();throw 'Owned threshold fixture timed out'}
 $result=$stdout.Result+$stderr.Result
 [IO.File]::WriteAllText((Join-Path $output 'result.txt'),$result)
 Write-Output $result
 if($ExhaustionMutation){
  if($child.ExitCode-ne 1-or -not $result.Contains('settled adaptive guard still queried native CPU')){throw 'Adaptive stability mutation missed its assertion'}
  Write-Output 'PASS mutation rejected: continue native sampling after de-escalation'
 }elseif($child.ExitCode -ne 0){throw 'Threshold checks failed'}
}finally{$child.Dispose()}
$drift=@($manifest|Where-Object{(Get-FileHash -LiteralPath $_.Source).Hash -ne $_.SHA256}|Select-Object -ExpandProperty Source)
[pscustomobject]@{Passed=$true;NativeSamples=$false;ProductStarted=$false;SourceDrift=$drift}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($drift.Count -ne 0){throw 'Source drifted during checks'}
Write-Output ('OUTPUT '+$output)
