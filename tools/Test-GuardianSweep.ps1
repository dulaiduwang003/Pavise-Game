param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$sweepWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-GuardianSweep-'+[Guid]::NewGuid().ToString('N'))}
$sweepOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $sweepOutput -Force
# Production sources (no PAVISE_SELFTEST: the sweep is native code) plus the check entry point.
$sweepSources=@(Get-ChildItem -LiteralPath (Join-Path $sweepWorkspace 'src') -Recurse -File -Filter '*.cs'|ForEach-Object{$_.FullName})
$sweepSources+=Join-Path $sweepWorkspace 'tests\GuardianSweepChecks.cs'
$sweepExe=Join-Path $sweepOutput 'GuardianSweepChecks.exe'
$sweepRsp=Join-Path $sweepOutput 'sources.rsp'
$sweepArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0169,0414',
 '/define:PAVISE_STABILITY_COMPARISON;PAVISE_GUARDIAN_SWEEP_CHECKS','/main:PaviseApp.GuardianSweepChecks',('/out:"'+$sweepExe+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$sweepArgs+=@($sweepSources|ForEach-Object{'"'+$_+'"'})
[IO.File]::WriteAllLines($sweepRsp,$sweepArgs,(New-Object Text.UTF8Encoding($false)))
function Invoke-SweepHidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$sweepOutput
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=$info.StandardOutputEncoding
 $child=[Diagnostics.Process]::Start($info)
 try{$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(120000)){$child.Kill();return [pscustomobject]@{Exit=124;Text='timed out'}}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}}finally{$child.Dispose()}
}
$compile=Invoke-SweepHidden (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') ('@"'+$sweepRsp+'"')
[IO.File]::WriteAllText((Join-Path $sweepOutput 'compile.log'),$compile.Text)
if($compile.Exit -ne 0){throw ('Guardian sweep checks did not compile: '+$compile.Text)}
$run=Invoke-SweepHidden $sweepExe ''
[IO.File]::WriteAllText((Join-Path $sweepOutput 'result.txt'),$run.Text);Write-Output $run.Text
if($run.Exit -ne 0 -or -not $run.Text.Contains('PASS guardian sweep checks=')){throw 'Guardian sweep checks failed'}
$negative=Invoke-SweepHidden $sweepExe '--negative-control'
if($negative.Exit -ne 3){throw 'The guardian sweep negative control did not return its distinct exit code'}
Write-Output ('OUTPUT '+$sweepOutput)
