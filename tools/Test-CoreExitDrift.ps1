param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$driftWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-CoreExitDrift-'+[Guid]::NewGuid().ToString('N'))}
$driftOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $driftOutput -Force
$driftSources=@(Get-ChildItem -LiteralPath (Join-Path $driftWorkspace 'src') -Recurse -File -Filter '*.cs'|ForEach-Object{$_.FullName})
$driftSources+=@('tests\CoreExitDriftChecks.cs','tests\SelfTests.FamilySuppression.cs','tests\SelfTests.RendererRelease.cs'|ForEach-Object{Join-Path $driftWorkspace $_})
$driftExe=Join-Path $driftOutput 'CoreExitDriftChecks.exe'
$driftRsp=Join-Path $driftOutput 'sources.rsp'
$driftArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0169,0414',
 '/define:PAVISE_SELFTEST;PAVISE_CORE_EXIT_DRIFT_CHECKS','/main:PaviseApp.CoreExitDriftChecks',('/out:"'+$driftExe+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll')
$driftArgs+=@($driftSources|ForEach-Object{'"'+$_+'"'})
[IO.File]::WriteAllLines($driftRsp,$driftArgs,(New-Object Text.UTF8Encoding($false)))
function Invoke-DriftHidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$driftOutput
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=$info.StandardOutputEncoding
 $child=[Diagnostics.Process]::Start($info)
 try{$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(120000)){$child.Kill();return [pscustomobject]@{Exit=124;Text='timed out'}}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}}finally{$child.Dispose()}
}
$compile=Invoke-DriftHidden (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') ('@"'+$driftRsp+'"')
[IO.File]::WriteAllText((Join-Path $driftOutput 'compile.log'),$compile.Text)
if($compile.Exit -ne 0){throw ('Core exit drift checks did not compile: '+$compile.Text)}
$run=Invoke-DriftHidden $driftExe ''
[IO.File]::WriteAllText((Join-Path $driftOutput 'result.txt'),$run.Text);Write-Output $run.Text
if($run.Exit -ne 0 -or -not $run.Text.Contains('PASS core exit drift checks=')){throw 'Core exit drift checks failed'}
$negative=Invoke-DriftHidden $driftExe '--negative-control'
if($negative.Exit -ne 3){throw 'The core exit drift negative control did not return its distinct exit code'}
Write-Output ('OUTPUT '+$driftOutput)
