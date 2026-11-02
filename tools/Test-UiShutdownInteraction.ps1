param()
$ErrorActionPreference = 'Stop'
$uiShutdownWorkspace = Split-Path $PSScriptRoot -Parent
$uiShutdownOutput = Join-Path $env:TEMP ('Pavise-UiShutdownInteraction-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $uiShutdownOutput
Push-Location $uiShutdownWorkspace
try {
    $uiShutdownCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $uiShutdownExe = Join-Path $uiShutdownOutput 'Pavise.ui-shutdown-check.exe'
    $uiShutdownInputs = @(Get-ChildItem -LiteralPath (Join-Path $uiShutdownWorkspace 'src') -Recurse -File -Filter '*.cs')
    $uiShutdownInputs += Get-Item -LiteralPath (Join-Path $uiShutdownWorkspace 'tests/UiShutdownInteractionRunner.cs')
    $uiShutdownInputs += Get-Item -LiteralPath (Join-Path $uiShutdownWorkspace 'tests/SelfTests.UiAsyncState.cs')
    $uiShutdownInputs += Get-Item -LiteralPath $PSCommandPath
    $uiShutdownBefore = @($uiShutdownInputs | ForEach-Object { [pscustomobject]@{
        Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    & $uiShutdownCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_UI_SHUTDOWN_RUNNER;PAVISE_STABILITY_COMPARISON' `
        -main:PaviseApp.UiShutdownInteractionAudit ('-out:' + $uiShutdownExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\UiShutdownInteractionRunner.cs tests\SelfTests.UiAsyncState.cs
    if ($LASTEXITCODE -ne 0) { throw 'Isolated UI shutdown interaction build failed.' }
    $uiShutdownStart = New-Object Diagnostics.ProcessStartInfo
    $uiShutdownStart.FileName = $uiShutdownExe
    $uiShutdownStart.WorkingDirectory = $uiShutdownOutput
    $uiShutdownStart.UseShellExecute = $false
    $uiShutdownStart.CreateNoWindow = $true
    $uiShutdownStart.RedirectStandardOutput = $true
    $uiShutdownStart.RedirectStandardError = $true
    $uiShutdownProcess = New-Object Diagnostics.Process
    $uiShutdownProcess.StartInfo = $uiShutdownStart
    try {
        $null = $uiShutdownProcess.Start()
        $uiShutdownStdout = $uiShutdownProcess.StandardOutput.ReadToEndAsync()
        $uiShutdownStderr = $uiShutdownProcess.StandardError.ReadToEndAsync()
        if (-not $uiShutdownProcess.WaitForExit(30000)) {
            $uiShutdownProcess.Kill()
            throw 'Owned UI shutdown interaction runner timed out.'
        }
        $uiShutdownProcess.WaitForExit()
        $uiShutdownResult = $uiShutdownStdout.Result + $uiShutdownStderr.Result
        [IO.File]::WriteAllText((Join-Path $uiShutdownOutput 'result.txt'), $uiShutdownResult)
        Write-Output $uiShutdownResult
        $uiShutdownChanged = @($uiShutdownBefore | Where-Object {
            -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256 })
        [pscustomobject]@{
            SourceUnchanged = $uiShutdownChanged.Count -eq 0
            ProtectedInputs = $uiShutdownBefore.Count
            ChangedInputs = $uiShutdownChanged
            RuntimeStarted = $false
            RealSystemRestore = $false
            RealShaderCleanup = $false
            RealAuditSampling = $false
            StabilityComparisonBuild = $true
            OffscreenProgressRenders = Join-Path $uiShutdownOutput 'progress-card-renders'
            SettingsStore = 'Transient in-process dictionary'
            OutputDirectory = $uiShutdownOutput
            ExitCode = $uiShutdownProcess.ExitCode
        } | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $uiShutdownOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $uiShutdownOutput)
        if ($uiShutdownChanged.Count -ne 0) { throw 'Inputs changed; rerun after sources are frozen.' }
        if ($uiShutdownProcess.ExitCode -ne 0) { throw 'UI shutdown interaction regression failed; inspect result.txt.' }
    } finally { $uiShutdownProcess.Dispose() }
} finally { Pop-Location }
