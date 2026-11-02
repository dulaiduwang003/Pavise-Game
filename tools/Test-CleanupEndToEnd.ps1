param([switch]$NativeOnly, [switch]$CompileOnly, [string]$ResultDir)
$ErrorActionPreference = 'Stop'
$cleanupWorkspace = Split-Path $PSScriptRoot -Parent
$cleanupOutput = if ($ResultDir) { [IO.Path]::GetFullPath($ResultDir) } else { Join-Path $env:TEMP ('Pavise-CleanupEndToEnd-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $cleanupOutput) { throw 'A new unique result directory is required.' }
$null = New-Item -ItemType Directory -Path $cleanupOutput
Push-Location $cleanupWorkspace
try {
    $cleanupCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $cleanupExe = Join-Path $cleanupOutput 'Pavise.cleanup-end-to-end-check.exe'
    $cleanupInputs = @(Get-ChildItem -LiteralPath (Join-Path $cleanupWorkspace 'src') -Recurse -File -Filter '*.cs')
    $cleanupInputs += Get-Item -LiteralPath (Join-Path $cleanupWorkspace 'tests/CleanupEndToEndRunner.cs')
    $cleanupBefore = @($cleanupInputs | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    & $cleanupCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_CLEANUP_END_TO_END' `
        -main:PaviseApp.CleanupEndToEndRunner ('-out:' + $cleanupExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\CleanupEndToEndRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Cleanup end-to-end regression compilation failed.' }
    if ($CompileOnly) {
        [pscustomobject]@{Runner=$cleanupExe;NativeArguments=@('--native-only',$cleanupOutput);ProtectedInputs=$cleanupBefore} |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $cleanupOutput 'compilation.json') -Encoding utf8
        Write-Output ('COMPILED ' + $cleanupExe)
        Write-Output ('OUTPUT ' + $cleanupOutput)
        return
    }
    $cleanupStart = New-Object Diagnostics.ProcessStartInfo
    $cleanupStart.FileName = $cleanupExe
    $cleanupStart.Arguments = $(if ($NativeOnly) { '--native-only ' } else { '' }) + '"' + $cleanupOutput + '"'
    $cleanupStart.WorkingDirectory = $cleanupOutput
    $cleanupStart.UseShellExecute = $false
    $cleanupStart.CreateNoWindow = $true
    $cleanupStart.RedirectStandardOutput = $true
    $cleanupStart.RedirectStandardError = $true
    $cleanupProcess = New-Object Diagnostics.Process
    $cleanupProcess.StartInfo = $cleanupStart
    try {
        $null = $cleanupProcess.Start()
        $cleanupStdout = $cleanupProcess.StandardOutput.ReadToEndAsync()
        $cleanupStderr = $cleanupProcess.StandardError.ReadToEndAsync()
        if (-not $cleanupProcess.WaitForExit($(if ($NativeOnly) { 600000 } else { 50000 }))) {
            if ($NativeOnly) { throw 'Native cleanup runner exceeded its budget; it was left running to finish its owned-key finally cleanup.' }
            $cleanupProcess.Kill(); throw 'Cleanup end-to-end runner timed out.'
        }
        $cleanupProcess.WaitForExit()
        $cleanupResult = $cleanupStdout.Result + $cleanupStderr.Result
        [IO.File]::WriteAllText((Join-Path $cleanupOutput 'result.txt'), $cleanupResult)
        Write-Output $cleanupResult
        $cleanupChanged = @($cleanupBefore | Where-Object {-not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256})
        [pscustomobject]@{SourceUnchanged=$cleanupChanged.Count -eq 0;ProtectedInputs=$cleanupBefore.Count;ChangedInputs=$cleanupChanged;
            RuntimeStarted=$false;NativeSystemRestore=[bool]$NativeOnly;NativeTargetScope=$(if ($NativeOnly) { 'owned-guid-only' } else { 'mocked' });TaskSchedulerMutation=$false;
            DataDirectoryKind='unique-system-temp';OutputDirectory=$cleanupOutput;ExitCode=$cleanupProcess.ExitCode} |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $cleanupOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $cleanupOutput)
        if ($cleanupChanged.Count -ne 0) { throw 'Inputs changed; rerun after sources are frozen.' }
        if ($cleanupProcess.ExitCode -ne 0) { throw 'Cleanup end-to-end regression failed; inspect result.txt.' }
    } finally {$cleanupProcess.Dispose()}
} finally {Pop-Location}
