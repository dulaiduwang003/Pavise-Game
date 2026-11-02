param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$taskWorkspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $taskWorkspace ('build\LaboratoryManualActions-'+[Guid]::NewGuid().ToString('N'))}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $taskOutput -Force
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskExe=Join-Path $taskOutput 'Pavise.manual-laboratory-checks.exe'
$taskSources=@(Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src') -Recurse -File -Filter '*.cs' | Where-Object Name -ne 'Settings.cs' | Select-Object -ExpandProperty FullName)
$taskSources+=Join-Path $taskWorkspace 'tests\Official3106\MemorySettings.cs'
$taskSources+=Join-Path $taskWorkspace 'tests\LaboratoryManualActionsChecks.cs'
$taskResponse=Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse,[string[]]@($taskSources | ForEach-Object {'"'+$_+'"'}),[Text.UTF8Encoding]::new($true))
& $taskCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 -define:PAVISE_SELFTEST -main:PaviseApp.LaboratoryManualActionsChecks ('-out:'+$taskExe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@'+$taskResponse) *> (Join-Path $taskOutput 'compile.txt')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $taskOutput 'compile.txt');throw 'Manual laboratory isolated build failed.'}
$taskStart=[Diagnostics.ProcessStartInfo]::new()
$taskStart.FileName=$taskExe
$taskStart.UseShellExecute=$false
$taskStart.CreateNoWindow=$true
$taskStart.RedirectStandardOutput=$true
$taskStart.RedirectStandardError=$true
$taskProcess=[Diagnostics.Process]::new();$taskProcess.StartInfo=$taskStart
try{
 $null=$taskProcess.Start();$taskOut=$taskProcess.StandardOutput.ReadToEndAsync();$taskErr=$taskProcess.StandardError.ReadToEndAsync()
 if(-not $taskProcess.WaitForExit(45000)){$taskProcess.Kill();throw 'Manual laboratory check timed out.'}
 $taskProcess.WaitForExit();$taskText=$taskOut.Result+$taskErr.Result
 [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'),$taskText)
 Write-Output $taskText
 if($taskProcess.ExitCode -ne 0){throw ('Manual laboratory check exited '+$taskProcess.ExitCode)}
}finally{$taskProcess.Dispose();Write-Output ('OUTPUT '+$taskOutput)}
