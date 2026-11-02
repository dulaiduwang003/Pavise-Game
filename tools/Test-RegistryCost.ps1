param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$costWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-RegistryCost-'+[Guid]::NewGuid().ToString('N'))}
$costOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $costOutput -Force
# Production sources plus the settings-cache and power-audit checks.
$costSources=@(Get-ChildItem -LiteralPath (Join-Path $costWorkspace 'src') -Recurse -File -Filter '*.cs'|ForEach-Object{$_.FullName})
$costSources+=Join-Path $costWorkspace 'tests\RegistryCostChecks.cs'
$costExe=Join-Path $costOutput 'RegistryCostChecks.exe'
$costRsp=Join-Path $costOutput 'sources.rsp'
$costArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0169,0414',
 '/define:PAVISE_SELFTEST;PAVISE_REGISTRY_COST_CHECKS','/main:PaviseApp.SelfTests',('/out:"'+$costExe+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$costArgs+=@($costSources|ForEach-Object{'"'+$_+'"'})
[IO.File]::WriteAllLines($costRsp,$costArgs,(New-Object Text.UTF8Encoding($false)))
function Invoke-CostHidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$costOutput
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=$info.StandardOutputEncoding
 $child=[Diagnostics.Process]::Start($info)
 try{$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(180000)){$child.Kill();return [pscustomobject]@{Exit=124;Text='timed out'}}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}}finally{$child.Dispose()}
}
$compile=Invoke-CostHidden (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') ('@"'+$costRsp+'"')
[IO.File]::WriteAllText((Join-Path $costOutput 'compile.log'),$compile.Text)
if($compile.Exit -ne 0){throw ('Registry cost checks did not compile: '+$compile.Text)}
$run=Invoke-CostHidden $costExe ''
[IO.File]::WriteAllText((Join-Path $costOutput 'result.txt'),$run.Text);Write-Output $run.Text
if($run.Exit -ne 0 -or -not $run.Text.Contains('PASS registry cost checks=')){throw 'Registry cost checks failed'}
$negative=Invoke-CostHidden $costExe '--negative-control'
if($negative.Exit -ne 3){throw 'The registry cost negative control did not return its distinct exit code'}
Write-Output ('OUTPUT '+$costOutput)
