param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$nvWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-NvSessionApply-'+[Guid]::NewGuid().ToString('N'))}
$nvOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $nvOutput -Force
# Production sources plus the NVIDIA session apply checks (fake driver, transient settings).
$nvSources=@(Get-ChildItem -LiteralPath (Join-Path $nvWorkspace 'src') -Recurse -File -Filter '*.cs'|ForEach-Object{$_.FullName})
$nvSources+=Join-Path $nvWorkspace 'tests\NvSessionApplyChecks.cs'
$nvExe=Join-Path $nvOutput 'NvSessionApplyChecks.exe'
$nvRsp=Join-Path $nvOutput 'sources.rsp'
$nvArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0169,0414',
 '/define:PAVISE_SELFTEST;PAVISE_NV_SESSION_CHECKS','/main:PaviseApp.SelfTests',('/out:"'+$nvExe+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$nvArgs+=@($nvSources|ForEach-Object{'"'+$_+'"'})
[IO.File]::WriteAllLines($nvRsp,$nvArgs,(New-Object Text.UTF8Encoding($false)))
function Invoke-NvHidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$nvOutput
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=$info.StandardOutputEncoding
 $child=[Diagnostics.Process]::Start($info)
 try{$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(180000)){$child.Kill();return [pscustomobject]@{Exit=124;Text='timed out'}}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}}finally{$child.Dispose()}
}
$compile=Invoke-NvHidden (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') ('@"'+$nvRsp+'"')
[IO.File]::WriteAllText((Join-Path $nvOutput 'compile.log'),$compile.Text)
if($compile.Exit -ne 0){throw ('NVIDIA session checks did not compile: '+$compile.Text)}
$run=Invoke-NvHidden $nvExe ''
[IO.File]::WriteAllText((Join-Path $nvOutput 'result.txt'),$run.Text);Write-Output $run.Text
if($run.Exit -ne 0 -or -not $run.Text.Contains('PASS NVIDIA session apply checks=')){throw 'NVIDIA session checks failed'}
$negative=Invoke-NvHidden $nvExe '--negative-control'
if($negative.Exit -ne 3){throw 'The NVIDIA session negative control did not return its distinct exit code'}
Write-Output ('OUTPUT '+$nvOutput)
