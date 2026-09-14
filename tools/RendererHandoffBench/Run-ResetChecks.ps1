param([ValidateRange(1, 10)][int]$Repeat = 3)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resetBenchDirectory = $PSScriptRoot
$resetRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $resetBenchDirectory '..\..'))
$resetRunName = 'PaviseResetChecks-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$resetRunDirectory = Join-Path ([IO.Path]::GetTempPath()) $resetRunName
$null = New-Item -ItemType Directory -Path $resetRunDirectory
$resetCompiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $resetCompiler)) { throw 'The .NET Framework x64 C# compiler is required.' }

# Exact whitelist, the full SelfTests runtime and unrelated test files are excluded
$resetTestNames = @(
    'SelfTests.RendererCandidate.cs', 'SelfTests.RendererRelease.cs', 'SelfTests.RendererHandoff.cs',
    'SelfTests.RendererLearning.cs', 'SelfTests.RendererCoordinator.cs',
    'SelfTests.FamilySuppression.cs', 'SelfTests.GenericRenderer.cs',
    'SelfTests.GameInstallScope.cs', 'SelfTests.GameFamilyHistory.cs', 'SelfTests.GameFamilyIntegration.cs',
    'SelfTests.ResetCleanup.cs', 'SelfTests.ResetFlow.cs'
)
$resetTestFiles = @($resetTestNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $resetRepositoryRoot ('tests\' + $_)) })
$resetSourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $resetRepositoryRoot 'src') -Recurse -File -Filter '*.cs' | Sort-Object FullName)
$resetSupport = Join-Path $resetBenchDirectory 'HandoffBench.cs'
$resetScript = Join-Path $resetBenchDirectory 'Run-ResetChecks.ps1'
$resetProductionExe = Join-Path $resetRepositoryRoot 'Pavise.exe'

function Get-ResetCheckInputs {
    $resetInputs = @(Get-ChildItem -LiteralPath (Join-Path $resetRepositoryRoot 'src') -Recurse -File -Filter '*.cs')
    $resetInputs += @($resetTestFiles)
    $resetInputs += Get-Item -LiteralPath $resetSupport
    $resetInputs += Get-Item -LiteralPath $resetScript
    if (Test-Path -LiteralPath $resetProductionExe) { $resetInputs += Get-Item -LiteralPath $resetProductionExe }
    @($resetInputs | Sort-Object FullName -Unique)
}

$resetBefore = @(Get-ResetCheckInputs | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$resetBefore | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $resetRunDirectory 'source-hashes.json') -Encoding utf8
$resetProductionExisted = Test-Path -LiteralPath $resetProductionExe
$resetExit = 2
$resetTimedOut = $false
$resetProcess = $null

try {
    $resetExecutable = Join-Path $resetRunDirectory 'ResetCheckRunner.exe'
    $resetArgs = @('-nologo', '-target:exe', '-platform:x64', '-langversion:5', '-optimize+', '-codepage:65001',
        '-define:PAVISE_SELFTEST;PAVISE_RENDERER_BENCH', '-main:PaviseApp.ResetCheckRunner', ('-out:' + $resetExecutable),
        '-reference:System.dll', '-reference:System.Core.dll', '-reference:System.Drawing.dll',
        '-reference:System.Windows.Forms.dll', '-reference:System.Management.dll', '-reference:System.Xml.dll',
        '-reference:System.Web.Extensions.dll')
    $resetArgs += @($resetSourceFiles | ForEach-Object { $_.FullName })
    $resetArgs += @($resetTestFiles | ForEach-Object { $_.FullName })
    $resetArgs += $resetSupport
    & $resetCompiler @resetArgs 2>&1 | Tee-Object -FilePath (Join-Path $resetRunDirectory 'compile.log')
    if ($LASTEXITCODE -ne 0) { throw 'Reset-check compilation failed.' }

    $resetStdout = Join-Path $resetRunDirectory 'console.log'
    $resetStderr = Join-Path $resetRunDirectory 'stderr.log'
    # This dedicated entry only calls the reset checks and the focused discovery and renderer suites
    # No Program.Main, no GameMode.Start or Stop, and never touches the real process matrix
    $resetProcess = Start-Process -FilePath $resetExecutable -ArgumentList @(('"' + $resetRunDirectory + '"'), [string]$Repeat) -WorkingDirectory $resetRunDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput $resetStdout -RedirectStandardError $resetStderr
    $resetOwnedHandle = $resetProcess.Handle
    $resetDeadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(45, $Repeat * 15))
    while (-not $resetProcess.WaitForExit(250)) {
        if ([DateTime]::UtcNow -gt $resetDeadline) {
            $resetTimedOut = $true
            if ($resetProcess.Handle -eq $resetOwnedHandle -and -not $resetProcess.HasExited) { $resetProcess.Kill() }
            $null = $resetProcess.WaitForExit(3000)
            throw 'The owned reset-check runner exceeded its bounded runtime.'
        }
    }
    $resetExit = $resetProcess.ExitCode
    Get-Content -LiteralPath $resetStdout
    if ((Get-Item -LiteralPath $resetStderr).Length -gt 0) { Get-Content -LiteralPath $resetStderr }
}
finally {
    if ($null -ne $resetProcess) { $resetProcess.Dispose() }
    $resetChanged = @($resetBefore | Where-Object {
        -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256
    })
    $resetAfterPaths = @(Get-ResetCheckInputs | ForEach-Object { $_.FullName })
    $resetBeforePaths = @($resetBefore | ForEach-Object { $_.Path })
    $resetAdded = @($resetAfterPaths | Where-Object { $resetBeforePaths -notcontains $_ })
    $resetRemoved = @($resetBeforePaths | Where-Object { $resetAfterPaths -notcontains $_ })
    $resetInputsUnchanged = $resetChanged.Count -eq 0 -and $resetAdded.Count -eq 0 -and $resetRemoved.Count -eq 0
    $resetProductionChanged = @($resetChanged | Where-Object { $_.Path -eq $resetProductionExe }).Count -gt 0 -or $resetProductionExisted -ne (Test-Path -LiteralPath $resetProductionExe)
    [pscustomobject]@{
        SourceTestsRunnerAndProductionExeUnchanged = $resetInputsUnchanged
        ProtectedInputCount = $resetBefore.Count
        ChangedInputs = $resetChanged
        AddedInputs = $resetAdded
        RemovedInputs = $resetRemoved
        ProductionExecutableReplaced = $resetProductionChanged
        EntryPoint = 'PaviseApp.ResetCheckRunner'
        Defines = @('PAVISE_SELFTEST', 'PAVISE_RENDERER_BENCH')
        ExactTestSources = @($resetTestFiles | ForEach-Object { $_.FullName })
        Repeat = $Repeat
        OutputDirectory = $resetRunDirectory
        ExitCode = $resetExit
        TimedOut = $resetTimedOut
        NormalApplicationRun = $false
        RealRestoreOrRegistryDeletion = $false
    } | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $resetRunDirectory 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $resetRunDirectory)
    if (-not $resetInputsUnchanged) { throw 'Inputs changed; preserve this run and rerun only after sources are frozen.' }
}
if ($resetExit -ne 0) { throw ('Reset checks failed with exit code ' + $resetExit) }
