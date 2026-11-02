<#
.SYNOPSIS
Validates real critical-thread preference APIs and temporary profile persistence.
.DESCRIPTION
By default freezes the current workspace src, then compiles only the isolated
test entry with PAVISE_SELFTEST. Windows are on a private desktop, preferences
are transient, and GameMode.Start/product Main/native workers are never called.
An explicit frozen source response file may be supplied before source promotion.
This is preference/store/UI validation, not native placement or performance proof.
#>
param([string]$WorkspaceDirectory,[string]$OverlayResponseFile,[string]$OutputDirectory,[switch]$CompileOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($WorkspaceDirectory){$checkRoot=[IO.Path]::GetFullPath($WorkspaceDirectory)}
else{
 $checkRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
 while(-not ((Test-Path -LiteralPath (Join-Path $checkRoot 'src') -PathType Container) -and
              (Test-Path -LiteralPath (Join-Path $checkRoot 'Pavise.exe.config') -PathType Leaf))){
  $checkParent=Split-Path $checkRoot -Parent
  if(-not $checkParent -or $checkParent -eq $checkRoot){throw 'Workspace not found; supply -WorkspaceDirectory'}
  $checkRoot=$checkParent
 }
}
if(-not (Test-Path -LiteralPath (Join-Path $checkRoot 'src') -PathType Container)){throw 'Workspace src directory missing'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-CriticalThreadActualApiUi-'+[Guid]::NewGuid().ToString('N'))}
$checkOutput=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $checkOutput){throw 'Use a new output directory; existing evidence is never overwritten'}
$null=New-Item -ItemType Directory -Path $checkOutput
$checkSources=New-Object 'Collections.Generic.List[string]'
$checkNames=@{};$checkProducerPins=@();$checkOverlay=$null;$checkOverlayInputs=$null
function Get-WorkspaceCheckSources{
 $command=Get-Command rg -ErrorAction SilentlyContinue
 if($null -ne $command){
  $items=@(& $command.Source --files (Join-Path $checkRoot 'src') -g '*.cs')
  if($LASTEXITCODE -ne 0){throw 'Unable to enumerate workspace sources'}
  return @($items|ForEach-Object{[IO.Path]::GetFullPath($_)}|Sort-Object)
 }
 return @(Get-ChildItem -LiteralPath (Join-Path $checkRoot 'src') -Recurse -Filter '*.cs' -File|ForEach-Object{$_.FullName}|Sort-Object)
}
if($OverlayResponseFile){
 $checkOverlay=[IO.Path]::GetFullPath($OverlayResponseFile)
 $checkOverlayInputs=Join-Path ([IO.Path]::GetDirectoryName($checkOverlay)) 'inputs.csv'
 if(Test-Path -LiteralPath $checkOverlayInputs){
  $checkProducerPins=@(Import-Csv -LiteralPath $checkOverlayInputs)
  foreach($pin in $checkProducerPins){
   if((Get-FileHash -LiteralPath $pin.Frozen -Algorithm SHA256).Hash -ne $pin.Hash){throw 'Overlay frozen input manifest mismatch'}
   $checkNames[[IO.Path]::GetFullPath($pin.Frozen)]=[IO.Path]::GetFileName($pin.Path)
  }
 }
 foreach($line in [IO.File]::ReadAllLines($checkOverlay)){
  if($line -notmatch '^"(.+\.cs)"$'){continue}
  $source=$Matches[1]
  if(-not [IO.Path]::IsPathRooted($source)){$source=Join-Path $checkRoot $source}
  $checkSources.Add([IO.Path]::GetFullPath($source))
 }
}else{
 foreach($source in @(Get-WorkspaceCheckSources)){$checkSources.Add($source)}
}
function Get-CheckSourceName([string]$path){
 if($checkNames.ContainsKey($path)){return $checkNames[$path]}
 return [IO.Path]::GetFileName($path)
}
$checkSelected=New-Object 'Collections.Generic.List[string]'
foreach($source in $checkSources){
 $name=Get-CheckSourceName $source
 if($name -in @('UiOnlyApiStubs.cs','UiOnlyChecks.cs')){throw 'API stub/fake source overlays are not accepted'}
 if($name -in @('CriticalThreadActualApiUiChecks.cs','ActualApiUiChecks.cs','PerformanceWorkspaceChecks.cs','PerformanceWorkspacePageChecks.cs')){continue}
 $checkSelected.Add($source)
}
foreach($required in @('GameMode.CriticalThreadRules.cs','PanelForm.CoreThreads.cs')){
 if(-not @($checkSelected|Where-Object{(Get-CheckSourceName $_) -eq $required}).Count){throw ('Required real source missing: '+$required+'; promote sources or supply a complete explicit overlay')}
}
$checkTest=Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/CriticalThreadActualApiUiChecks.cs'
foreach($source in @($checkTest,(Join-Path $checkRoot 'tests/PerformanceWorkspaceChecks.cs'),(Join-Path $checkRoot 'tests/PerformanceWorkspacePageChecks.cs'))){
 if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw ('Required isolated test source missing: '+$source)}
 $checkSelected.Add([IO.Path]::GetFullPath($source))
}
$checkIndex=0
$checkAudit=@($checkSelected|Select-Object -Unique|ForEach-Object{
 $source=$_;$name=Get-CheckSourceName $source
 $frozen=Join-Path $checkOutput ('sources/'+($checkIndex++).ToString('0000')+'-'+$name)
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
 $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
 [IO.File]::Copy($source,$frozen)
 if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash -or
    (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash -ne $hash){throw 'Source changed during freeze'}
 [pscustomobject]@{Name=$name;Original=$source;Frozen=$frozen;SHA256=$hash}
})
$checkSourceCount=$checkAudit.Count
$checkAudit|Export-Csv -LiteralPath (Join-Path $checkOutput 'source-sha256.csv') -NoTypeInformation
function Add-CheckAudit([string]$source,[string]$name){
 $frozen=Join-Path $checkOutput $name
 $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
 [IO.File]::Copy($source,$frozen)
 if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash -or
    (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash -ne $hash){throw 'Supporting input changed during freeze'}
 return [pscustomobject]@{Name=$name;Original=$source;Frozen=$frozen;SHA256=$hash}
}
$checkExe=Join-Path $checkOutput 'CriticalThreadActualApiUiChecks.exe'
$checkAudit+=Add-CheckAudit $PSCommandPath 'Test-CriticalThreadActualApiUi.ps1'
$checkAudit+=Add-CheckAudit (Join-Path $checkRoot 'Pavise.exe.config') 'CriticalThreadActualApiUiChecks.exe.config'
if($checkOverlay){$checkAudit+=Add-CheckAudit $checkOverlay 'original-overlay.rsp'}
if($checkOverlayInputs -and (Test-Path -LiteralPath $checkOverlayInputs)){$checkAudit+=Add-CheckAudit $checkOverlayInputs 'original-overlay-inputs.csv'}
$checkAudit|Export-Csv -LiteralPath (Join-Path $checkOutput 'input-sha256.csv') -NoTypeInformation
$checkManifest=Join-Path $checkOutput 'fixture.manifest'
$checkManifestText=@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <assemblyIdentity version="1.0.0.0" name="Pavise.CriticalActualApiUi" />
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
 <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware><dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness></windowsSettings></application>
 <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /></application></compatibility>
</assembly>
'@
[IO.File]::WriteAllText($checkManifest,$checkManifestText,(New-Object Text.UTF8Encoding($false)))
# Only these fixed symbols are allowed. Overlay Main/defines/options are never inherited.
$checkArguments=@('/nologo','/target:exe','/platform:x64','/unsafe+','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0414',
 '/define:PAVISE_SELFTEST;PAVISE_PERFORMANCE_PAGE_CHECKS;PAVISE_CRITICAL_THREAD_ACTUAL_UI_CHECKS',
 '/main:PaviseApp.CriticalThreadActualApiUiChecks',('/out:"'+$checkExe+'"'),('/win32manifest:"'+$checkManifest+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$checkArguments+=@($checkAudit|Select-Object -First $checkSourceCount|ForEach-Object{'"'+$_.Frozen+'"'})
$checkRsp=Join-Path $checkOutput 'sources.rsp'
[IO.File]::WriteAllLines($checkRsp,$checkArguments,(New-Object Text.UTF8Encoding($false)))
function Invoke-CheckHidden([string]$program,[string]$arguments,[int]$limit){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$program;$info.Arguments=$arguments;$info.WorkingDirectory=$checkOutput
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{
  $null=$child.Start();$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit($limit)){$child.Kill();$child.WaitForExit();throw 'Isolated actual API UI check timed out; timeout is not a semantic failure'}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$stdout.Result+$stderr.Result}
 }finally{$child.Dispose()}
}
$checkCompile=$null;$checkRun=$null;$checkNegative=$null
try{
 $result=Invoke-CheckHidden (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') ('@"'+$checkRsp+'"') 60000
 $checkCompile=$result.Exit;[IO.File]::WriteAllText((Join-Path $checkOutput 'compile.log'),$result.Text)
 if($checkCompile -ne 0){throw $result.Text}
 if(-not $CompileOnly){
  $result=Invoke-CheckHidden $checkExe ('"'+$checkOutput+'"') 180000
  $checkRun=$result.Exit;[IO.File]::WriteAllText((Join-Path $checkOutput 'result.txt'),$result.Text);Write-Output $result.Text
  if($checkRun -ne 0 -or -not $result.Text.Contains('PASS critical-thread actual API UI checks=')){throw 'Actual API/store UI fixture failed'}
  $result=Invoke-CheckHidden $checkExe '--negative-control' 15000
  $checkNegative=$result.Exit;[IO.File]::WriteAllText((Join-Path $checkOutput 'negative.txt'),$result.Text)
  if($checkNegative -ne 3){throw 'Intentional negative control did not exit3'}
 }
}finally{
 $checkSetChanged=$false
 if(-not $checkOverlay){$checkSetChanged=@(Compare-Object -ReferenceObject @($checkSources|Sort-Object) -DifferenceObject @(Get-WorkspaceCheckSources)).Count -ne 0}
 $changed=@($checkAudit|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256 -or
                                     (Get-FileHash -LiteralPath $_.Frozen -Algorithm SHA256).Hash -ne $_.SHA256})
 [pscustomobject]@{SourceMode=$(if($checkOverlay){'explicit frozen overlay'}else{'workspace src'});CompileOnly=[bool]$CompileOnly;
  CompileExit=$checkCompile;RunExit=$checkRun;NegativeExit=$checkNegative;CSharpInputs=$checkSourceCount;AuditedInputs=$checkAudit.Count;
  SourceSetChecked=(-not [bool]$checkOverlay);SourceSetChanged=$(if($checkOverlay){$null}else{$checkSetChanged});
  InputsUnchanged=($changed.Count -eq 0);Changed=@($changed|ForEach-Object{$_.Name});NoApiStubs=$true;SelfTest=$true;
  Entry='PaviseApp.CriticalThreadActualApiUiChecks';NativeExecuted=$false;ProductStarted=$false;
  Scope='Real preference APIs and temporary profile store on a private desktop, with isolated source facts; not native affinity, recovery or performance acceptance';
  ManifestSHA256=(Get-FileHash -LiteralPath $checkManifest -Algorithm SHA256).Hash}|ConvertTo-Json -Depth 4|
  Set-Content -LiteralPath (Join-Path $checkOutput 'verification.json') -Encoding UTF8
 Write-Output ('OUTPUT '+$checkOutput)
 if($changed.Count){throw 'Frozen or original inputs changed during validation'}
 if($checkSetChanged){throw 'The workspace src source set changed during validation'}
}
