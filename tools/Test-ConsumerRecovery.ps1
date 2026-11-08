param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/ConsumerRecovery-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory'}
$null=New-Item -ItemType Directory -Path (Join-Path $output 'frozen')
$manifest=@(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs' | ForEach-Object {
 $relative=$_.FullName.Substring($workspace.Length+1);$copy=Join-Path (Join-Path $output 'frozen') $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force
 Copy-Item -LiteralPath $_.FullName -Destination $copy
 [pscustomobject]@{Original=$_.FullName;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $copy).Hash}
})
$test=Join-Path $output 'ConsumerRecoveryChecks.cs';Copy-Item -LiteralPath (Join-Path $workspace 'tests/ConsumerRecoveryChecks.cs') -Destination $test
$sources=@($manifest|ForEach-Object{'"'+$_.Frozen+'"'})+@('"'+$test+'"')
$rsp=Join-Path $output 'sources.rsp';[IO.File]::WriteAllLines($rsp,[string[]]$sources)
$exe=Join-Path $output 'ConsumerRecoveryChecks.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') -nologo -target:exe -unsafe+ -optimize+ -warn:0 -codepage:65001 -define:PAVISE_SELFTEST -main:PaviseApp.ConsumerRecoveryChecks ('-out:'+$exe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@'+$rsp) *> (Join-Path $output 'compile.txt')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $output 'compile.txt');throw 'Compile failed'}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$exe;$start.WorkingDirectory=$output;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($start)
try{$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Owned fixture timed out'}
 $result=$stdout.Result+$stderr.Result;[IO.File]::WriteAllText((Join-Path $output 'result.txt'),$result);Write-Output $result;if($process.ExitCode -ne 0){throw 'Consumer recovery fixture failed'}
}finally{$process.Dispose()}
$drift=@($manifest|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.SHA256}|Select-Object -ExpandProperty Original)
[pscustomobject]@{Passed=$true;SourceDrift=$drift;ProductionSource=$manifest;ProductionStarted=$false;NativeWrites=$false;Settings='production transient in-memory mode';Fixture='Full production compilation; restore delegates and privacy backend in memory; private phase methods reflected without native calls'}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding utf8
Write-Output ('OUTPUT '+$output)
