param([string]$WorkspaceRoot,[string]$SourceFile,[switch]$Mutations)
$ErrorActionPreference='Stop'
if(-not $WorkspaceRoot){$WorkspaceRoot=Split-Path $PSScriptRoot -Parent}
if(-not $SourceFile){$SourceFile=Join-Path $WorkspaceRoot 'src/Core/Program.CriticalThreadGuardian.cs'}
$test=Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/CriticalThreadGuardianEntrypointChecks.cs'
$output=Join-Path $env:TEMP ('Pavise-GuardianEntry-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $output
$sourcePaths=@([IO.Path]::GetFullPath($SourceFile),[IO.Path]::GetFullPath($test))
$pins=@($sourcePaths|ForEach-Object{[pscustomobject]@{Original=$_;Frozen=(Join-Path $output ([IO.Path]::GetFileName($_)));Hash=(Get-FileHash -LiteralPath $_).Hash}})
foreach($pin in $pins){[IO.File]::Copy($pin.Original,$pin.Frozen);if((Get-FileHash -LiteralPath $pin.Frozen).Hash -ne $pin.Hash){throw 'Entrypoint source changed while freezing'}}
$pins|Export-Csv -LiteralPath (Join-Path $output 'inputs.csv') -NoTypeInformation
function Invoke-Entry([string]$name,[string]$source){
 $exe=Join-Path $output ($name+'.exe');$rsp=Join-Path $output ($name+'.rsp')
 [IO.File]::WriteAllLines($rsp,@('/nologo','/target:exe','/platform:x64','/codepage:65001','/utf8output','/define:PAVISE_GUARDIAN_ENTRY_CHECKS','/main:PaviseApp.CriticalThreadGuardianEntrypointChecks',('/out:"'+$exe+'"'),'/reference:System.dll',('"'+$source+'"'),('"'+$pins[1].Frozen+'"')),(New-Object Text.UTF8Encoding($false)))
 & (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') ('@'+$rsp) *> (Join-Path $output ($name+'.compile.log'))
 if($LASTEXITCODE -ne 0){throw ('Compile failure is not a semantic result: '+$name)}
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$exe;$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{
  $null=$child.Start();$a=$child.StandardOutput.ReadToEndAsync();$b=$child.StandardError.ReadToEndAsync()
  if(-not $child.WaitForExit(15000)){$child.Kill();$child.WaitForExit();throw 'Timeout is not a semantic result'}
  $child.WaitForExit();$text=$a.Result+$b.Result;[IO.File]::WriteAllText((Join-Path $output ($name+'.log')),$text)
  return [pscustomobject]@{Name=$name;Compile=0;Exit=$child.ExitCode;Text=$text}
 }finally{$child.Dispose()}
}
$baseline=Invoke-Entry 'baseline' $pins[0].Frozen
Write-Output $baseline.Text
if($baseline.Exit -ne 0){Write-Output ('OUTPUT '+$output);throw 'The reserved child entrypoint failed its fault fixture'}
$rows=@($baseline|Select-Object Name,Compile,Exit)
if($Mutations){
 $text=[IO.File]::ReadAllText($pins[0].Frozen).Replace("`r`n","`n")
 foreach($mutation in @(
  @('exception-escapes',"            catch (Exception) { Environment.ExitCode = 14; }`n",''),
  @('failed-child-reports-success','catch (Exception) { Environment.ExitCode = 14; }','catch (Exception) { Environment.ExitCode = 0; }'),
  @('ordinary-arguments-consumed','if (!reserved) return false;','if (false) return false;')
 )){
  if($text.Split(@([string]$mutation[1]),[StringSplitOptions]::None).Length -ne 2){throw ('Mutation anchor must be unique: '+$mutation[0])}
  $file=Join-Path $output ($mutation[0]+'.cs');[IO.File]::WriteAllText($file,$text.Replace($mutation[1],$mutation[2]),(New-Object Text.UTF8Encoding($false)))
  $result=Invoke-Entry $mutation[0] $file
  if($result.Exit -ne 1 -or -not $result.Text.Contains('FAIL ')){throw ('Mutation did not fail semantically: '+$mutation[0])}
  $rows+=$result|Select-Object Name,Compile,Exit
 }
}
$changed=@($pins|Where-Object{(Get-FileHash -LiteralPath $_.Original).Hash -ne $_.Hash -or (Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.Hash})
$rows|Export-Csv -LiteralPath (Join-Path $output 'results.csv') -NoTypeInformation
[pscustomobject]@{Baseline=0;MutationKills=$rows.Count-1;SourceChanges=$changed.Count;Native=$false;GUI=$false;ProductMain=$false;Output=$output}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($changed.Count){throw 'Entrypoint test inputs changed'}
Write-Output ('PASS guardian entrypoint mutations='+($rows.Count-1)+'; OUTPUT '+$output)
