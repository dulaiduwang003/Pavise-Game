# 按旧版本自身收据恢复后再清除设置，不向新版导入。
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$LegacyExe,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$legacyPath = (Resolve-Path -LiteralPath $LegacyExe).ProviderPath
if ([IO.Path]::GetExtension($legacyPath) -ine '.exe') { throw 'Provide the original Pavise .exe.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($legacyPath)
if ($version.ProductName -ne 'Pavise' -or $version.FileVersion -ne '2.2.2.3') {
    throw 'This wrapper is verified for legacy Pavise 2.2.2.3 only. Use that release to restore; never pass the Auto executable.'
}
$reportPath = Join-Path ([IO.Path]::GetTempPath()) ('LegacyRestore-' + [Guid]::NewGuid().ToString('N') + '.txt')
Write-Output ('Legacy executable: ' + $legacyPath)
Write-Output ('Verified version: ' + $version.FileVersion)
if ($CheckOnly) { Write-Output 'CHECK ONLY: no processes stopped, no settings changed.'; return }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $admin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
finally { $identity.Dispose() }
if (-not $admin) { throw 'Run this script from an elevated PowerShell. No elevation window was opened.' }
if (-not $PSCmdlet.ShouldProcess($legacyPath, 'Restore legacy changes by receipt and clear legacy configuration')) { return }
$instances = @(Get-Process | Where-Object { $_.ProcessName -like 'Pavise*' })
foreach ($instance in $instances) {
    $runningPath = $instance.Path
    if ([string]::IsNullOrWhiteSpace($runningPath) -or $runningPath -ine $legacyPath) {
        throw 'Another Pavise build or test is running. Close it before restoring the legacy installation.'
    }
}
if ($instances.Count -gt 0) {
    $signal = [Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit')
    try { $null = $signal.Set() } finally { $signal.Dispose() }
    foreach ($instance in $instances) {
        if (-not $instance.WaitForExit(30000)) { throw 'Legacy Pavise is still restoring. It was not force-killed; wait and retry.' }
    }
}
# 旧卸载器带强杀路径，调用前必须确认旧实例已全部退出。
if (@(Get-Process | Where-Object { $_.ProcessName -like 'Pavise*' }).Count -gt 0) {
    throw 'A Pavise process appeared during shutdown; restore was not started.'
}
$runner = Start-Process -FilePath $legacyPath -ArgumentList ('--uninstall "' + $reportPath + '"') -WorkingDirectory (Split-Path -LiteralPath $legacyPath) -WindowStyle Hidden -PassThru
try {
    if (-not $runner.WaitForExit(180000)) {
        throw ('The restore is still running and was not killed. Wait for it to finish. Report: ' + $reportPath)
    }
    $runner.WaitForExit()
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw 'Legacy restore returned no verification report. Do not start Auto yet.' }
    $report = Get-Content -LiteralPath $reportPath -Raw
    Write-Output $report
    if ($runner.ExitCode -ne 0 -or $report -notmatch '(?m)^cleared=1\r?$') {
        throw ('Legacy restoration is incomplete. Preserve remaining receipts and inspect: ' + $reportPath)
    }
    Write-Output 'Legacy restoration completed. Auto will start with fresh settings.'
    Write-Output ('Report: ' + $reportPath)
} finally { $runner.Dispose() }
