param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$motionWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$motionOutput=[IO.Path]::GetFullPath($OutputDirectory)
$motionFrozen=Join-Path $motionOutput 'frozen-inputs'
if(Test-Path -LiteralPath $motionFrozen){throw 'Choose a fresh output directory to preserve earlier evidence.'}
$null=New-Item -ItemType Directory -Path $motionFrozen -Force
$motionSources=@(& rg --files (Join-Path $motionWorkspace 'src') -g '*.cs')
if($LASTEXITCODE -ne 0){throw 'Cannot enumerate product source'}
$motionSources+=@('tests/CoreMotionChecks.cs','tests/PerformanceWorkspaceChecks.cs' | ForEach-Object {Join-Path $motionWorkspace $_})
$motionManifest=@($motionSources | ForEach-Object {
 $original=[IO.Path]::GetFullPath($_);$relative=$original.Substring($motionWorkspace.Length+1);$frozen=Join-Path $motionFrozen $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
 [IO.File]::WriteAllBytes($frozen,[IO.File]::ReadAllBytes($original))
 [pscustomobject]@{Path=$relative;Original=$original;Frozen=$frozen;SHA256=(Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash}
})
$motionManifest | Export-Csv -LiteralPath (Join-Path $motionOutput 'input-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $motionOutput 'Test-CoreMotion.ps1'),[IO.File]::ReadAllBytes($PSCommandPath))
$motionCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$motionExe=Join-Path $motionOutput 'CoreMotionChecks.exe'
[IO.File]::WriteAllBytes(($motionExe+'.config'),[IO.File]::ReadAllBytes((Join-Path $motionWorkspace 'Pavise.exe.config')))
$motionArgs=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/utf8output','/nowarn:0162,0649,0414',
 '/define:PAVISE_SELFTEST;PAVISE_PERFORMANCE_PAGE_CHECKS;PAVISE_CORE_MOTION_CHECKS',
 '/main:PaviseApp.CoreMotionChecks',('/out:"'+$motionExe+'"'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll')
$motionArgs+=@($motionManifest | ForEach-Object {'"'+$_.Frozen+'"'})
$motionRsp=Join-Path $motionOutput 'sources.rsp';[IO.File]::WriteAllLines($motionRsp,$motionArgs,(New-Object Text.UTF8Encoding($false)))
& $motionCompiler ('@'+$motionRsp) *> (Join-Path $motionOutput 'compile.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $motionOutput 'compile.log');throw 'Motion fixture compile failed'}
$motionStart=New-Object Diagnostics.ProcessStartInfo
$motionStart.FileName=$motionExe;$motionStart.Arguments='"'+$motionOutput+'"';$motionStart.WorkingDirectory=$motionOutput
$motionStart.UseShellExecute=$false;$motionStart.CreateNoWindow=$true;$motionStart.WindowStyle='Hidden'
$motionStart.RedirectStandardOutput=$true;$motionStart.RedirectStandardError=$true
$motionStart.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$motionStart.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
$motionChild=New-Object Diagnostics.Process;$motionChild.StartInfo=$motionStart
try{
 $null=$motionChild.Start();$stdout=$motionChild.StandardOutput.ReadToEndAsync();$stderr=$motionChild.StandardError.ReadToEndAsync()
 if(-not $motionChild.WaitForExit(180000)){$motionChild.Kill();throw 'Motion fixture timed out'}
 $motionChild.WaitForExit();$motionText=$stdout.Result+$stderr.Result
 [IO.File]::WriteAllText((Join-Path $motionOutput 'result.txt'),$motionText);Write-Output $motionText
 $motionExit=$motionChild.ExitCode
}finally{$motionChild.Dispose()}
$changed=@($motionManifest | Where-Object {(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256})
[pscustomobject]@{Pass=($motionExit -eq 0);Exit=$motionExit;FrozenSourceCount=$motionManifest.Count;WorkspaceMatchesSnapshot=($changed.Count -eq 0);ChangedSources=@($changed | ForEach-Object {$_.Path});ProductEntryPointRun=$false;PaviseRestarted=$false} |
 ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $motionOutput 'verification.json') -Encoding UTF8
if($motionExit -ne 0){throw 'Motion fixture failed'}
if($changed.Count -ne 0){throw 'Shared sources changed during checks; the retained snapshot is the tested revision.'}
