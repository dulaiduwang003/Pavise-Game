param([string]$OutputDirectory='', [switch]$NativeReadOnly)
$ErrorActionPreference='Stop'
$taskWorkspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $taskWorkspace ('build\LaboratoryClockRepair-'+[Guid]::NewGuid().ToString('N'))}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force|Out-Null
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExe=Join-Path $taskOutput 'Pavise.clock-repair-checks.exe'
$taskSources=@(Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src') -Recurse -File -Filter '*.cs'|Select-Object -ExpandProperty FullName)
$taskSources+=Join-Path $taskWorkspace 'tests\LaboratoryClockRepairChecks.cs'
$taskResponse=Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse,[string[]]@($taskSources|ForEach-Object {'"'+$_+'"'}),[Text.UTF8Encoding]::new($true))
& $taskCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 -define:PAVISE_SELFTEST -main:PaviseApp.LaboratoryClockRepairChecks ('-out:'+$taskExe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@'+$taskResponse) *> (Join-Path $taskOutput 'compile.txt')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $taskOutput 'compile.txt');throw 'Clock repair isolated build failed.'}
$taskStart=[Diagnostics.ProcessStartInfo]::new()
$taskStart.FileName=$taskExe
$taskStart.UseShellExecute=$false
$taskStart.CreateNoWindow=$true
$taskStart.RedirectStandardOutput=$true
$taskStart.RedirectStandardError=$true
if($NativeReadOnly){$taskStart.Arguments='--native-readonly'}
$taskProcess=[Diagnostics.Process]::new();$taskProcess.StartInfo=$taskStart
try{
 $null=$taskProcess.Start();$taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
 if(-not $taskProcess.WaitForExit(90000)){$taskProcess.Kill();throw 'Clock repair check timeout.'}
 $taskProcess.WaitForExit();$taskText=$taskOut.Result+$taskErr.Result
 [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'),$taskText)
 Write-Output $taskText
 if($taskProcess.ExitCode -ne 0){throw ('Clock repair check exited '+$taskProcess.ExitCode)}
}finally{$taskProcess.Dispose();Write-Output ('OUTPUT '+$taskOutput)}
