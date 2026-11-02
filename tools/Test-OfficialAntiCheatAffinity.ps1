param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $workspace ('build/OfficialAntiCheat-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$frozen=Join-Path $output 'frozen'
if(Test-Path -LiteralPath $frozen){throw 'Use a fresh output directory to preserve frozen evidence'}
$null=New-Item -ItemType Directory -Path $frozen -Force
$files=@(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs')
$files+=@('AntiCheatTopologyChecks.cs','AntiCheatAffinityChecks.cs','AntiCheatEligibilityChecks.cs','AntiCheatPlacementIsolationChecks.cs','AntiCheatRuntimeChecks.cs','AntiCheatNativeChecks.cs' | ForEach-Object {Get-Item -LiteralPath (Join-Path $workspace ('tests/Official3106/'+$_))})
$manifest=@()
foreach($file in $files){
 $relative=$file.FullName.Substring($workspace.Length+1)
 $copy=Join-Path $frozen $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force
 [IO.File]::WriteAllBytes($copy,[IO.File]::ReadAllBytes($file.FullName))
 $manifest+= [pscustomobject]@{Original=$file.FullName;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash}
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'inputs.json') -Encoding UTF8
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
function Run-Hidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$output
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=[Diagnostics.Process]::Start($info)
 try{$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(60000)){$child.Kill();throw 'Anti-cheat check timed out'}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$stdout.Result+$stderr.Result}
 }finally{$child.Dispose()}
}
$baseOptions=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/nowarn:0162,0649,0414,0675,1718','/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$results=@()
foreach($name in @('AntiCheatTopologyChecks','AntiCheatAffinityChecks','AntiCheatEligibilityChecks','AntiCheatRuntimeChecks','AntiCheatPlacementIsolationChecks','AntiCheatNativeChecks')){
 $folder=Join-Path $output $name;$null=New-Item -ItemType Directory -Path $folder -Force
 $exe=Join-Path $folder 'Checks.exe';$rsp=Join-Path $folder 'sources.rsp'
 if($name -eq 'AntiCheatPlacementIsolationChecks'){
  $selection=@('SuppressionCore.cs','SuppressionAffinityPolicy.cs','SuppressReason.cs','SuppressionLevel.cs','AcquireResult.cs','BackgroundReleaseState.cs','AntiCheatPlacementIsolationChecks.cs')
  $selected=@($manifest | Where-Object {[IO.Path]::GetFileName($_.Frozen) -in $selection})
  $options=$baseOptions+@('/define:PAVISE_AC_ISOLATION_STUBS')
 }else{
  $selected=@($manifest | Where-Object { $_.Original.StartsWith((Join-Path $workspace 'src')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($_.Frozen) -eq ($name+'.cs') })
  $options=$baseOptions
 }
 $options+=@('/main:PaviseApp.'+$name,('/out:"'+$exe+'"'))
 $options+=@($selected | ForEach-Object {'"'+$_.Frozen+'"'})
 [IO.File]::WriteAllLines($rsp,$options,(New-Object Text.UTF8Encoding($false)))
 $compile=Run-Hidden $compiler ('@"'+$rsp+'"')
 [IO.File]::WriteAllText((Join-Path $folder 'compile.log'),$compile.Text)
 if($compile.Exit -ne 0){throw ('Compilation failed: '+$name+' '+$compile.Text)}
 $run=Run-Hidden $exe ('"'+(Join-Path $folder 'state')+'"')
 [IO.File]::WriteAllText((Join-Path $folder 'result.txt'),$run.Text)
 Write-Output $run.Text
 if($run.Exit -ne 0 -or $run.Text -notmatch 'PASS|SKIP'){throw ('Check failed: '+$name)}
 $results+=[pscustomobject]@{Suite=$name;Exit=$run.Exit;Result=$run.Text}
}
$changed=@($manifest | Where-Object {(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256})
[pscustomobject]@{Suites=$results;Inputs=$manifest.Count;SourceChanges=$changed.Count;ProductMainExecuted=$false;RealAntiCheatAffinityWrites=$false;OwnHiddenTestProcessAffinityCheck=$true;VisibleWindows=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($changed.Count -ne 0){throw 'Sources changed during validation'}
Write-Output ('OUTPUT '+$output)
