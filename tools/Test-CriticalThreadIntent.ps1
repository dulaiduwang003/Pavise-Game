param([switch]$Mutations)
$ErrorActionPreference='Stop'
$intentWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$intentOutput=Join-Path $env:TEMP ('Pavise-CriticalIntent-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path (Join-Path $intentOutput 'sources')
function Get-IntentInputs {
 @((Get-ChildItem -LiteralPath (Join-Path $intentWorkspace 'src') -Recurse -Filter '*.cs' -File|Select-Object -ExpandProperty FullName),
   (Join-Path $intentWorkspace 'tests/CriticalThreadIntentChecks.cs'))|ForEach-Object{$_}|Sort-Object -Unique
}
$intentSources=@(Get-IntentInputs)
$intentPins=@();$intentIndex=0
foreach($source in $intentSources){
 $pin= [pscustomobject]@{Path=$source;Hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash;Frozen=(Join-Path $intentOutput ('sources/'+($intentIndex++).ToString('0000')+'-'+[IO.Path]::GetFileName($source)))}
 [IO.File]::Copy($source,$pin.Frozen)
 if((Get-FileHash -LiteralPath $pin.Frozen -Algorithm SHA256).Hash -ne $pin.Hash){throw 'An input changed while freezing'}
 $intentPins+=$pin
}
$intentPins|Export-Csv -LiteralPath (Join-Path $intentOutput 'inputs.csv') -NoTypeInformation
$intentExe=Join-Path $intentOutput 'CriticalThreadIntentChecks.exe'
$intentRsp=Join-Path $intentOutput 'checks.rsp'
$intentArgs=@('/nologo','/target:exe','/platform:x64','/unsafe+','/optimize+','/codepage:65001','/utf8output',
 '/nowarn:0162,0169,0649,0414','/define:PAVISE_SELFTEST;PAVISE_CRITICAL_THREAD_INTENT_CHECKS',
 '/main:PaviseApp.CriticalThreadIntentChecks',('/out:"'+$intentExe+'"'),
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll',
 '/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
[IO.File]::WriteAllLines($intentRsp,($intentArgs+@($intentPins|ForEach-Object{'"'+$_.Frozen+'"'})),(New-Object Text.UTF8Encoding($false)))
function Invoke-IntentHidden([string]$program,[string]$arguments,[int]$limit){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$program;$info.Arguments=$arguments;$info.UseShellExecute=$false
 $info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{
  $null=$child.Start();$out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit($limit)){$child.Kill();$child.WaitForExit();throw 'A timeout is not a semantic test result'}
  $child.WaitForExit();return [pscustomobject]@{Exit=$child.ExitCode;Text=$out.Result+$err.Result}
 }finally{$child.Dispose()}
}
$intentCompile=Invoke-IntentHidden (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') ('@"'+$intentRsp+'"') 60000
[IO.File]::WriteAllText((Join-Path $intentOutput 'compile.log'),$intentCompile.Text)
if($intentCompile.Exit -ne 0){throw ('Intent checks failed to compile: '+$intentCompile.Text)}
# Only this explicitly selected pure entry runs; the application Main never runs.
$intentRun=Invoke-IntentHidden $intentExe '' 15000
[IO.File]::WriteAllText((Join-Path $intentOutput 'run.log'),$intentRun.Text)
Write-Output $intentRun.Text
if($intentRun.Exit -ne 0 -or -not $intentRun.Text.Contains('PASS critical thread intent checks=')){throw 'Pure intent checks failed'}
if($Mutations){
 & (Join-Path $PSScriptRoot 'Test-CriticalThreadIntentMutations.ps1') -SnapshotDirectory $intentOutput
 if(-not $?){throw 'Intent mutation checks failed'}
}
$intentChanged=@($intentPins|Where-Object{(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash -or (Get-FileHash -LiteralPath $_.Frozen -Algorithm SHA256).Hash -ne $_.Hash})
$intentSetChanged=@(Compare-Object $intentSources @(Get-IntentInputs)).Count -ne 0
$intentResult=[pscustomobject]@{Compile=$intentCompile.Exit;Run=$intentRun.Exit;Sources=$intentPins.Count;Changed=$intentChanged.Count;SourceSetChanged=$intentSetChanged;Native=$false;UI=$false;ApplicationMain=$false;Output=$intentOutput}
$intentResult|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $intentOutput 'verification.json') -Encoding UTF8
$intentResult|ConvertTo-Json
if($intentChanged.Count -ne 0 -or $intentSetChanged){throw 'Intent test inputs changed during verification'}
