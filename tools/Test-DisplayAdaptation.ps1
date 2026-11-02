param([string]$OutputDirectory, [switch]$SingleCase, [switch]$CompileOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$layoutWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-DisplayAdaptation-'+[Guid]::NewGuid().ToString('N'))}
$layoutOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $layoutOutput -Force
$layoutSources=@(& rg --files (Join-Path $layoutWorkspace 'src') -g '*.cs')
foreach($relative in @('tests/PerformanceWorkspaceChecks.cs','tests/ManualModeChecks.cs','tests/DisplayAdaptationChecks.cs')){$layoutSources+=Join-Path $layoutWorkspace $relative}
$layoutManifest=@($layoutSources|ForEach-Object{
 $original=[IO.Path]::GetFullPath($_);$relative=$original.Substring($layoutWorkspace.Length+1);$frozen=Join-Path (Join-Path $layoutOutput 'frozen-inputs') $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
 [IO.File]::WriteAllBytes($frozen,[IO.File]::ReadAllBytes($original))
 [pscustomobject]@{Path=$relative;Original=$original;Frozen=$frozen;SHA256=(Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$layoutManifest|Export-Csv -LiteralPath (Join-Path $layoutOutput 'source-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $layoutOutput ([IO.Path]::GetFileName($PSCommandPath))),[IO.File]::ReadAllBytes($PSCommandPath))
$layoutAudit=@($layoutManifest)+[pscustomobject]@{Path='tools/Test-DisplayAdaptation.ps1';Original=$PSCommandPath;Frozen=(Join-Path $layoutOutput ([IO.Path]::GetFileName($PSCommandPath)));SHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()}
function Invoke-LayoutHidden([string]$File,[string]$Arguments,[int]$LimitMs){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$layoutOutput;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden'
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$info.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{
  $null=$child.Start();$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync();$clock=[Diagnostics.Stopwatch]::StartNew();$timedOut=$false
  while(-not $child.WaitForExit(1000)){if($clock.ElapsedMilliseconds -gt $LimitMs){$timedOut=$true;$child.Kill();break}}
  $child.WaitForExit()
  return [pscustomobject]@{Exit=$(if($timedOut){124}else{$child.ExitCode});Text=$stdout.Result+$stderr.Result;TimedOut=$timedOut}
 }finally{$child.Dispose()}
}
$layoutCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$layoutExe=Join-Path $layoutOutput 'DisplayAdaptationChecks.exe'
$displayConfig=Join-Path $layoutWorkspace 'Pavise.exe.config'
$displayFrozenConfig=Join-Path $layoutOutput 'frozen-inputs/Pavise.exe.config'
[IO.File]::WriteAllBytes($displayFrozenConfig,[IO.File]::ReadAllBytes($displayConfig))
[IO.File]::WriteAllBytes(($layoutExe+'.config'),[IO.File]::ReadAllBytes($displayFrozenConfig))
$layoutAudit += [pscustomobject]@{Path='Pavise.exe.config';Original=$displayConfig;Frozen=$displayFrozenConfig;SHA256=(Get-FileHash -LiteralPath $displayFrozenConfig -Algorithm SHA256).Hash.ToLowerInvariant()}
$displayManifest=Join-Path $layoutOutput 'fixture.manifest'
$displayManifestText=@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <assemblyIdentity version="1.0.0.0" name="Pavise.DisplayAdaptationChecks" />
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
 <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
  <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
 </windowsSettings></application>
 <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /></application></compatibility>
</assembly>
'@
[IO.File]::WriteAllText($displayManifest,$displayManifestText,(New-Object Text.UTF8Encoding($false)))
$layoutAudit += [pscustomobject]@{Path='fixture.manifest (generated asInvoker isolation)';Original=$displayManifest;Frozen=$displayManifest;SHA256=(Get-FileHash -LiteralPath $displayManifest -Algorithm SHA256).Hash.ToLowerInvariant()}
$layoutAudit|Export-Csv -LiteralPath (Join-Path $layoutOutput 'input-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
$layoutRsp=Join-Path $layoutOutput 'sources.rsp'
$layoutArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649','/define:PAVISE_SELFTEST;PAVISE_MANUAL_MODE_CHECKS;PAVISE_PERFORMANCE_PAGE_CHECKS;PAVISE_DISPLAY_ADAPTATION_CHECKS','/main:PaviseApp.DisplayAdaptationChecks',('/out:"'+$layoutExe+'"'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll')
$layoutArgs+=@($layoutManifest|ForEach-Object{'"'+$_.Frozen+'"'})
$layoutArgs+=('/win32manifest:"'+$displayManifest+'"')
[IO.File]::WriteAllLines($layoutRsp,$layoutArgs,(New-Object Text.UTF8Encoding($false)))
$layoutResults=New-Object 'Collections.Generic.List[object]'
try{
 $compile=Invoke-LayoutHidden $layoutCompiler ('@"'+$layoutRsp+'"') 60000
 [IO.File]::WriteAllText((Join-Path $layoutOutput 'compile.log'),$compile.Text)
 $layoutResults.Add([pscustomobject]@{Step='compile';Exit=$compile.Exit;Pass=($compile.Exit -eq 0)})
 if($compile.Exit -ne 0){throw ('Layout fixture did not compile: '+$compile.Text)}
 if(-not $CompileOnly){
  $mode=if($SingleCase){' --single'}else{''}
  $run=Invoke-LayoutHidden $layoutExe ('"'+$layoutOutput+'"'+$mode) 360000
  [IO.File]::WriteAllText((Join-Path $layoutOutput 'result.txt'),$run.Text);Write-Output $run.Text
  $layoutResults.Add([pscustomobject]@{Step='real private-desktop UI';Exit=$run.Exit;TimedOut=$run.TimedOut;Pass=($run.Exit -eq 0 -and $run.Text -match 'PASS display adaptation assertions=')})
  if($run.Exit -ne 0 -or $run.Text -notmatch 'PASS display adaptation assertions='){throw 'Real layout checks failed or timed out; not a passing result'}
  $negative=Invoke-LayoutHidden $layoutExe '--negative-control' 10000
  [IO.File]::WriteAllText((Join-Path $layoutOutput 'negative-control.txt'),$negative.Text)
  $layoutResults.Add([pscustomobject]@{Step='intentional negative control';Exit=$negative.Exit;Pass=($negative.Exit -eq 3 -and $negative.Text.Contains('intentional oversized default window'))})
  if($negative.Exit -ne 3 -or -not $negative.Text.Contains('intentional oversized default window')){throw 'Layout negative control did not fail as expected'}
 }else{Write-Output 'PASS layout compile-only; fixture not executed'}
 $changed=@($layoutAudit|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256})
 [pscustomobject]@{SingleCase=[bool]$SingleCase;CompileOnly=[bool]$CompileOnly;TestedFrozenSourceCount=$layoutManifest.Count;WorkspaceMatchesSnapshot=($changed.Count -eq 0);ChangedSources=@($changed|ForEach-Object{$_.Path});Screenshots=@(Get-ChildItem -LiteralPath $layoutOutput -File -Filter '*.png'|ForEach-Object{$_.Name})}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $layoutOutput 'verification.json') -Encoding UTF8
 if($changed.Count -ne 0){throw 'Source changed during checks; results apply only to retained frozen input'}
}finally{
 if(-not (Test-Path -LiteralPath (Join-Path $layoutOutput 'verification.json'))){
  $failedChanged=@($layoutAudit|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256})
  [pscustomobject]@{SingleCase=[bool]$SingleCase;CompileOnly=[bool]$CompileOnly;TestedFrozenSourceCount=$layoutManifest.Count;WorkspaceMatchesSnapshot=($failedChanged.Count -eq 0);ChangedSources=@($failedChanged|ForEach-Object{$_.Path});Screenshots=@(Get-ChildItem -LiteralPath $layoutOutput -File -Filter '*.png'|ForEach-Object{$_.Name})}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $layoutOutput 'verification.json') -Encoding UTF8
 }
 $layoutResults|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $layoutOutput 'results.json') -Encoding UTF8
 Write-Output ('OUTPUT '+$layoutOutput)
}
