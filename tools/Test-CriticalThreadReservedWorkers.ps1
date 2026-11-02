param([string]$WorkspaceRoot,[string]$OverlayDirectory,[switch]$Mutations,[switch]$KeepMirror)
# A1 held cores and the worker range of a hard affinity target
# The worker capacity of the rule and the range the target factory issues both leave out cores another CPU set owner already holds
# Freezes the whole src  optionally with an overlay of src relative files that replace existing ones  plus one test entry
# compiles PAVISE_SELFTEST with an explicit Main and never runs the application
# The only native calls are query limited reads of the test process
# Run from its stage with no src beside tools it overlays the Core files of the stage on the workspace three levels up
# after checking that every replaced workspace file still has the base hash the stage lists
$ErrorActionPreference='Stop'
$here=Split-Path $PSScriptRoot -Parent
if(-not $WorkspaceRoot){
 if(Test-Path -LiteralPath (Join-Path $here 'src\Core')){$WorkspaceRoot=$here}
 else{$WorkspaceRoot=Join-Path $here '..\..\..';if(-not $OverlayDirectory){$OverlayDirectory=$here}}
}
$WorkspaceRoot=[IO.Path]::GetFullPath($WorkspaceRoot)
$test=[IO.Path]::GetFullPath((Join-Path $here 'tests\CriticalThreadReservedWorkerChecks.cs'))
$output=Join-Path $env:TEMP ('Pavise-CriticalReservedWorkers-'+[Guid]::NewGuid().ToString('N'))
$frozen=Join-Path $output 'frozen'
$null=New-Item -ItemType Directory -Path $frozen
$src=Join-Path $WorkspaceRoot 'src'
$inputs=[ordered]@{}
foreach($file in @(Get-ChildItem -LiteralPath $src -Recurse -File -Filter '*.cs'|Sort-Object FullName)){$inputs[('src\'+$file.FullName.Substring($src.Length+1))]=$file.FullName}
$replaced=@()
if($OverlayDirectory){
 $overlay=[IO.Path]::GetFullPath($OverlayDirectory).TrimEnd('\')
 $base=@{}
 $baseFile=Join-Path $overlay 'BASE-ACTUAL.txt'
 if(Test-Path -LiteralPath $baseFile){foreach($line in [IO.File]::ReadAllLines($baseFile)){if($line -match '^(src\\\S+)\s+([0-9A-Fa-f]{64})$'){$base[$Matches[1]]=$Matches[2].ToUpperInvariant()}}}
 foreach($file in @(Get-ChildItem -LiteralPath $overlay -Recurse -File -Filter '*.cs'|Sort-Object FullName)){
  $relative=$file.FullName.Substring($overlay.Length+1)
  # Only the Core files of the stage are sources  never its tests tools or evidence
  if(-not $relative.StartsWith('Core\')){continue}
  $key='src\'+$relative
  if(-not $inputs.Contains($key)){throw ('The overlay only replaces existing sources: '+$key)}
  if(-not $base.ContainsKey($key)){throw ('No base hash for the replaced source: '+$key)}
  $current=(Get-FileHash -LiteralPath $inputs[$key]).Hash
  if($current -ne $base[$key]){throw ('The workspace source moved since the stage was frozen: '+$key+' '+$current)}
  $replaced+=[pscustomobject]@{Relative=$key;Base=$current;Overlay=(Get-FileHash -LiteralPath $file.FullName).Hash}
  $inputs[$key]=$file.FullName
 }
}
$inputs['tests\CriticalThreadReservedWorkerChecks.cs']=$test
$pins=@()
foreach($key in $inputs.Keys){
 $target=Join-Path $frozen $key
 $null=New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
 $hash=(Get-FileHash -LiteralPath $inputs[$key]).Hash
 [IO.File]::Copy($inputs[$key],$target)
 if((Get-FileHash -LiteralPath $target).Hash -ne $hash){throw ('An input changed while freezing: '+$key)}
 $pins+=[pscustomobject]@{Relative=$key;Original=$inputs[$key];Frozen=$target;Hash=$hash}
}
$pins|Select-Object Relative,Original,Hash|Export-Csv -LiteralPath (Join-Path $output 'inputs.csv') -NoTypeInformation
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$options=@('/nologo','/target:exe','/platform:x64','/unsafe+','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0169,0649,0414',
 '/define:PAVISE_SELFTEST;PAVISE_CRITICAL_THREAD_RESERVED_WORKER_CHECKS','/main:PaviseApp.CriticalThreadReservedWorkerChecks',
 '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
 '/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
function Invoke-Hidden([string]$program,[string]$arguments,[int]$limit){
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
function Invoke-Entry([string]$name,[string]$replaceRelative,[string]$replacement){
 $exe=Join-Path $output ($name+'.exe');$rsp=Join-Path $output ($name+'.rsp')
 $files=@($pins|ForEach-Object{if($_.Relative -eq $replaceRelative){'"'+$replacement+'"'}else{'"'+$_.Frozen+'"'}})
 [IO.File]::WriteAllLines($rsp,($options+@('/out:"'+$exe+'"')+$files),(New-Object Text.UTF8Encoding($false)))
 $compile=Invoke-Hidden $csc ('@"'+$rsp+'"') 300000
 [IO.File]::WriteAllText((Join-Path $output ($name+'.compile.log')),$compile.Text)
 if($compile.Exit -ne 0){throw ('Compile failure is not a semantic result: '+$name)}
 $run=Invoke-Hidden $exe '' 60000
 [IO.File]::WriteAllText((Join-Path $output ($name+'.log')),$run.Text)
 return [pscustomobject]@{Name=$name;Exit=$run.Exit;Text=$run.Text;Exe=$exe}
}
$baseline=Invoke-Entry 'baseline' '' ''
Write-Output $baseline.Text
if($baseline.Exit -ne 0 -or -not $baseline.Text.Contains('PASS critical thread reserved worker assertions=')){Write-Output ('OUTPUT '+$output);throw 'Reserved worker checks failed'}
$negative=Invoke-Hidden $baseline.Exe '--negative-control' 15000
if($negative.Exit -ne 3){throw 'The negative control did not return its distinct exit code'}
$rows=@([pscustomobject]@{Name='baseline';File='';Exit=0;Expected=''},[pscustomobject]@{Name='negative-control';File='';Exit=$negative.Exit;Expected='exit 3'})
if($Mutations){
 $rule='src\Core\CriticalThreadRule.cs';$authority='src\Core\GameMode.CriticalThreadAuthority.cs'
 $mutants=@(
  [pscustomobject]@{Name='a1-workers-count-reserved';File=$rule;From='ulong workers = allowed & ~(exclusive | reserved);';To='ulong workers = allowed & ~exclusive;';Expect='workers counted cores another owner holds'},
  [pscustomobject]@{Name='a1-workers-keep-reserved-sibling';File=$rule;From='reserved = AutoCores.WholeCores(reserved, physical);';To='';Expect='workers counted cores another owner holds'},
  [pscustomobject]@{Name='a1-workers-overreserved';File=$rule;From='ulong workers = allowed & ~(exclusive | reserved);';To='ulong workers = reserved == 0 ? allowed & ~exclusive : 0;';Expect='a rule with two free worker cores was refused'},
  [pscustomobject]@{Name='a1-issued-range-keeps-reserved';File=$authority;From='nativePath, allowed & ~reserved,';To='nativePath, allowed,';Expect='the bound range still holds cores another owner holds'},
  [pscustomobject]@{Name='a1-issued-range-keeps-reserved-sibling';File=$authority;From='ulong reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask,
                    CpuTopology.PhysicalCoreMasks());';To='ulong reserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;';Expect='the bound range retained the sibling of a reserved logical processor'},
  [pscustomobject]@{Name='a1-check-without-reserved';File=$authority;From='CpuTopology.PhysicalCoreMasks(), allowed, reserved, true);';To='CpuTopology.PhysicalCoreMasks(), allowed, 0, true);';Expect='a target was issued with fewer than two free worker cores'}
 )
 foreach($mutation in $mutants){
  $pin=@($pins|Where-Object{$_.Relative -eq $mutation.File})
  if($pin.Count -ne 1){throw ('Mutation target is not a frozen input: '+$mutation.File)}
  $text=[IO.File]::ReadAllText($pin[0].Frozen).Replace("`r`n","`n")
  $from=([string]$mutation.From).Replace("`r`n","`n");$to=([string]$mutation.To).Replace("`r`n","`n")
  if($text.Split(@($from),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation anchor must be unique: '+$mutation.Name)}
  $file=Join-Path $output ($mutation.Name+'.cs');[IO.File]::WriteAllText($file,$text.Replace($from,$to),(New-Object Text.UTF8Encoding($false)))
  $result=Invoke-Entry $mutation.Name $mutation.File $file
  if($result.Exit -ne 1 -or -not $result.Text.Contains('FAIL '+$mutation.Expect)){throw ('Mutation did not fail at its intended check: '+$mutation.Name+' | '+$result.Text.Trim())}
  $rows+=[pscustomobject]@{Name=$mutation.Name;File=$mutation.File;Exit=$result.Exit;Expected=$mutation.Expect}
 }
}
$changed=@($pins|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.Hash -or (Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$rows|Export-Csv -LiteralPath (Join-Path $output 'results.csv') -NoTypeInformation
$replaced|Export-Csv -LiteralPath (Join-Path $output 'overlay.csv') -NoTypeInformation
if(-not $KeepMirror){Remove-Item -LiteralPath $frozen -Recurse -Force;Get-ChildItem -LiteralPath $output -Filter '*.exe'|Remove-Item -Force}
[pscustomobject]@{Baseline=0;NegativeControl=$negative.Exit;MutationKills=@($rows|Where-Object{$_.Exit -eq 1}).Count;Inputs=$pins.Count;Overlay=$replaced.Count;SourceChanges=$changed.Count;Native='query-limited handle to this test process only';GUI=$false;ProductMain=$false;Output=$output}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($changed.Count){throw 'Reserved worker test inputs changed'}
Write-Output ('PASS critical thread reserved worker mutations='+@($rows|Where-Object{$_.Exit -eq 1}).Count+'; OUTPUT '+$output)
