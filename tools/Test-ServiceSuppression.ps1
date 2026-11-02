param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$serviceWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-ServiceSuppression-'+[Guid]::NewGuid().ToString('N'))}
$serviceOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $serviceOutput -Force
# Production sources plus the pure policy entry point.
$serviceSources=@(Get-ChildItem -LiteralPath (Join-Path $serviceWorkspace 'src') -Recurse -File -Filter '*.cs'|ForEach-Object{$_.FullName})
$serviceSources+=Join-Path $serviceWorkspace 'tests\ServiceSuppressionChecks.cs'
$serviceExe=Join-Path $serviceOutput 'ServiceSuppressionChecks.exe'
$serviceRsp=Join-Path $serviceOutput 'sources.rsp'
$serviceArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0169,0414',
 '/define:PAVISE_STABILITY_COMPARISON;PAVISE_SERVICE_SUPPRESSION_CHECKS','/main:PaviseApp.ServiceSuppressionChecks',('/out:"'+$serviceExe+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$serviceArgs+=@($serviceSources|ForEach-Object{'"'+$_+'"'})
[IO.File]::WriteAllLines($serviceRsp,$serviceArgs,(New-Object Text.UTF8Encoding($false)))
function Invoke-ServiceHidden([string]$File,[string]$Arguments){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$serviceOutput
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=$info.StandardOutputEncoding
 $child=[Diagnostics.Process]::Start($info)
 try{$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(120000)){$child.Kill();return [pscustomobject]@{Exit=124;Text='timed out'}}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}}finally{$child.Dispose()}
}
$compile=Invoke-ServiceHidden (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') ('@"'+$serviceRsp+'"')
[IO.File]::WriteAllText((Join-Path $serviceOutput 'compile.log'),$compile.Text)
if($compile.Exit -ne 0){throw ('Service suppression checks did not compile: '+$compile.Text)}
$run=Invoke-ServiceHidden $serviceExe ''
[IO.File]::WriteAllText((Join-Path $serviceOutput 'result.txt'),$run.Text);Write-Output $run.Text
if($run.Exit -ne 0 -or -not $run.Text.Contains('PASS service suppression checks=')){throw 'Service suppression checks failed'}
$negative=Invoke-ServiceHidden $serviceExe '--negative-control'
if($negative.Exit -ne 3){throw 'The service suppression negative control did not return its distinct exit code'}
Write-Output ('OUTPUT '+$serviceOutput)
