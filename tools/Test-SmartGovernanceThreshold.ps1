param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/SmartGovernanceThreshold-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory.'}
$null=New-Item -ItemType Directory -Path $output
$manifest=@()
foreach($relative in @('src/PaviseApp/CpuSaturation.cs','src/PaviseApp/AdaptiveGuard.cs','tests/SmartGovernanceThresholdChecks.cs')){
 $source=Join-Path $workspace $relative
 $copy=Join-Path $output ([IO.Path]::GetFileName($relative))
 Copy-Item -LiteralPath $source -Destination $copy
 $manifest+=[pscustomobject]@{Source=$source;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $copy).Hash}
}
$manifest|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'source-manifest.json') -Encoding UTF8
$exe=Join-Path $output 'SmartGovernanceThresholdChecks.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /optimize+ /platform:x64 /codepage:65001 /main:PaviseApp.SmartGovernanceThresholdChecks ('/out:'+$exe) /reference:System.dll /reference:System.Core.dll @($manifest.Frozen) *> (Join-Path $output 'compile.log')
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
 if($child.ExitCode -ne 0){throw 'Threshold checks failed'}
}finally{$child.Dispose()}
$drift=@($manifest|Where-Object{(Get-FileHash -LiteralPath $_.Source).Hash -ne $_.SHA256}|Select-Object -ExpandProperty Source)
[pscustomobject]@{Passed=$true;NativeSamples=$false;ProductStarted=$false;SourceDrift=$drift}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($drift.Count -ne 0){throw 'Source drifted during checks'}
Write-Output ('OUTPUT '+$output)
