param([string]$WorkspaceRoot, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $WorkspaceRoot) { $WorkspaceRoot = Split-Path $PSScriptRoot -Parent }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-AudioEffects-' + [Guid]::NewGuid().ToString('N')) }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskSources = @('src/PaviseApp/LaboratoryManualContracts.cs', 'src/PaviseApp/LaboratoryAudioEffects.cs', 'src/PaviseApp/AudioEffectsNative.cs', 'tests/LaboratoryAudioEffectsChecks.cs')
$taskFrozen = @()
foreach ($taskRelative in $taskSources) {
    $taskSource = Join-Path $WorkspaceRoot $taskRelative
    $taskTarget = Join-Path $taskOutput ([IO.Path]::GetFileName($taskSource))
    [IO.File]::Copy($taskSource, $taskTarget, $true)
    if ((Get-FileHash -LiteralPath $taskSource).Hash -ne (Get-FileHash -LiteralPath $taskTarget).Hash) { throw 'Audio effects source changed while freezing.' }
    $taskFrozen += $taskTarget
}
$taskExe = Join-Path $taskOutput 'LaboratoryAudioEffectsChecks.exe'
$taskRsp = Join-Path $taskOutput 'sources.rsp'
$taskArgs = @('/nologo', '/target:exe', '/platform:x64', '/codepage:65001', '/utf8output', '/define:PAVISE_SELFTEST;PAVISE_LAB_AUDIO_EFFECTS_CHECKS', '/main:PaviseApp.LaboratoryAudioEffectsChecks', ('/out:"' + $taskExe + '"'), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Security.dll')
$taskArgs += @($taskFrozen | ForEach-Object { '"' + $_ + '"' })
[IO.File]::WriteAllLines($taskRsp, $taskArgs, (New-Object Text.UTF8Encoding($true)))
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') ('@' + $taskRsp) *> (Join-Path $taskOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $taskOutput 'compile.log'); throw 'Audio effects isolated fixture compile failed.' }
$taskInfo = New-Object Diagnostics.ProcessStartInfo
$taskInfo.FileName = $taskExe
$taskInfo.UseShellExecute = $false
$taskInfo.CreateNoWindow = $true
$taskInfo.WindowStyle = 'Hidden'
$taskInfo.RedirectStandardOutput = $true
$taskInfo.RedirectStandardError = $true
$taskChild = New-Object Diagnostics.Process
$taskChild.StartInfo = $taskInfo
try {
    $null = $taskChild.Start()
    $taskStdout = $taskChild.StandardOutput.ReadToEndAsync()
    $taskStderr = $taskChild.StandardError.ReadToEndAsync()
    if (-not $taskChild.WaitForExit(20000)) { $taskChild.Kill(); $taskChild.WaitForExit(); throw 'Audio effects isolated fixture timed out.' }
    $taskChild.WaitForExit()
    $taskText = $taskStdout.Result + $taskStderr.Result
    [IO.File]::WriteAllText((Join-Path $taskOutput 'checks.log'), $taskText)
    Write-Output $taskText
    if ($taskChild.ExitCode -ne 0) { throw 'Audio effects isolated checks failed.' }
} finally { $taskChild.Dispose() }
Write-Output ('OUTPUT ' + $taskOutput)
