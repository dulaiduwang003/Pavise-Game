param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/AutomaticRetryOwner-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory.'}
$null=New-Item -ItemType Directory -Path $output
$source=Join-Path $workspace 'src/PaviseApp/GameMode.AutomaticRetries.cs'
$frozen=Join-Path $output 'GameMode.AutomaticRetries.cs'
Copy-Item -LiteralPath $source -Destination $frozen
Copy-Item -LiteralPath (Join-Path $workspace 'src/PaviseApp/RecoveryRouteSet.cs') -Destination (Join-Path $output 'RecoveryRouteSet.cs')
$hash=(Get-FileHash -LiteralPath $frozen).Hash
$exe=Join-Path $output 'AutomaticRetryOwnerChecks.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler -nologo -optimize+ -target:exe -platform:x64 -codepage:65001 -reference:System.dll -reference:System.Core.dll ('-out:'+$exe) $frozen (Join-Path $output 'RecoveryRouteSet.cs') (Join-Path $workspace 'tests/AutomaticRetryOwnerChecks.cs') *> (Join-Path $output 'compile.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $output 'compile.log');throw 'Compilation failed'}
$start=New-Object Diagnostics.ProcessStartInfo($exe)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start)
try {
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
 if(-not $process.WaitForExit(15000)){$process.Kill();throw 'Owned fixture timed out'}
 $result=$stdout.Result+$stderr.Result
 [IO.File]::WriteAllText((Join-Path $output 'result.txt'),$result)
 $result
 if($process.ExitCode -ne 0){throw 'Automatic retry owner checks failed'}
}finally{$process.Dispose()}
if((Get-FileHash -LiteralPath $source).Hash -ne $hash){throw 'Source drift during test'}
[pscustomobject]@{Source=$source;SHA256=$hash;Passed=$true;ProductStarted=$false;NativeWrites=0} | ConvertTo-Json | Set-Content (Join-Path $output 'verification.json') -Encoding utf8
