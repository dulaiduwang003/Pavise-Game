param([string]$OutputDirectory='', [switch]$NativeReadOnly)
$ErrorActionPreference='Stop'
$taskWorkspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-FileFilter-'+[Guid]::NewGuid().ToString('N'))}
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskExe=Join-Path $taskOutput 'Pavise.file-filter-checks.exe'
$taskSources=@('src/PaviseApp/LaboratoryManualContracts.cs','src/PaviseApp/LaboratoryFileFilter.cs','src/PaviseApp/FileFilterNative.cs','tests/LaboratoryFileFilterChecks.cs')|ForEach-Object {Join-Path $taskWorkspace $_}
$taskResponse=Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse,[string[]]@($taskSources|ForEach-Object {'"'+$_+'"'}),[Text.UTF8Encoding]::new($true))
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $taskCompiler -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 '-define:PAVISE_FILE_FILTER_CHECKS;PAVISE_FILE_FILTER_STANDALONE' -main:PaviseApp.LaboratoryFileFilterChecks ('-out:'+$taskExe) -reference:System.dll -reference:System.Core.dll -reference:System.Security.dll ('@'+$taskResponse) *> (Join-Path $taskOutput 'compile.txt')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $taskOutput 'compile.txt');throw 'File-filter isolated compilation failed.'}
$taskStart=[Diagnostics.ProcessStartInfo]::new()
$taskStart.FileName=$taskExe;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
if($NativeReadOnly){$taskStart.Arguments='--native-readonly'}
$taskProcess=[Diagnostics.Process]::new();$taskProcess.StartInfo=$taskStart
try{
    $null=$taskProcess.Start();$taskStdout=$taskProcess.StandardOutput.ReadToEndAsync();$taskStderr=$taskProcess.StandardError.ReadToEndAsync()
    if(-not $taskProcess.WaitForExit(45000)){$taskProcess.Kill();throw 'File-filter isolated check timed out.'}
    $taskProcess.WaitForExit();$taskText=$taskStdout.Result+$taskStderr.Result
    [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'),$taskText)
    Write-Output $taskText
    if($taskProcess.ExitCode -ne 0){throw ('File-filter check exited '+$taskProcess.ExitCode)}
}finally{$taskProcess.Dispose();Write-Output ('OUTPUT '+$taskOutput)}
