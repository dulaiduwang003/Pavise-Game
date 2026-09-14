param([ValidateRange(1, 10)][int]$Repeat = 3)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$benchDirectory = $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $benchDirectory '..\..'))
$runName = 'run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $benchDirectory ('results\' + $runName)
$null = New-Item -ItemType Directory -Path $runDirectory
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework x64 C# compiler is required.' }

# Exact whitelist, excludes the full SelfTests runtime and unrelated ignored test files
$testNames = @('Candidate', 'Release', 'Handoff', 'Learning', 'Coordinator')
$testFiles = @($testNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $repositoryRoot ('tests\SelfTests.Renderer' + $_ + '.cs')) })
$testFiles += Get-Item -LiteralPath (Join-Path $repositoryRoot 'tests\SelfTests.FamilySuppression.cs')
$testFiles += Get-Item -LiteralPath (Join-Path $repositoryRoot 'tests\SelfTests.GenericRenderer.cs')
$testFiles += Get-Item -LiteralPath (Join-Path $repositoryRoot 'tests\SelfTests.GameInstallScope.cs')
$testFiles += Get-Item -LiteralPath (Join-Path $repositoryRoot 'tests\SelfTests.GameFamilyHistory.cs')
$testFiles += Get-Item -LiteralPath (Join-Path $repositoryRoot 'tests\SelfTests.GameFamilyIntegration.cs')
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File -Filter '*.cs' | Sort-Object FullName)
$benchFiles = @(@('HandoffBench.cs', 'FixtureHost.cs', 'Run-Bench.ps1', 'README.md') | ForEach-Object { Get-Item -LiteralPath (Join-Path $benchDirectory $_) })
$productionExe = Join-Path $repositoryRoot 'Pavise.exe'
function Get-ProtectedInputs {
    $inputs = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File -Filter '*.cs')
    $inputs += @($testFiles) + @($benchFiles)
    if (Test-Path -LiteralPath $productionExe) { $inputs += Get-Item -LiteralPath $productionExe }
    @($inputs | Sort-Object FullName -Unique)
}
$before = @(Get-ProtectedInputs | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$before | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $runDirectory 'source-hashes.json') -Encoding utf8
$productionPresentBefore = Test-Path -LiteralPath $productionExe
$benchExit = 2
$benchTimedOut = $false
$benchProcess = $null

try {
    # Independent console entries, bypass build.cmd, the production EXE and Program.Main
    $helperOutput = Join-Path $runDirectory 'FixtureHost.exe'
    $helperArgs = @('-nologo', '-target:exe', '-platform:x64', '-langversion:5', '-optimize+', '-codepage:65001',
        ('-out:' + $helperOutput), '-reference:System.dll', '-reference:System.Core.dll', (Join-Path $benchDirectory 'FixtureHost.cs'))
    & $compiler @helperArgs 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'compile-helper.log')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }

    $benchOutput = Join-Path $runDirectory 'RendererHandoffBench.exe'
    $compilerArgs = @('-nologo', '-target:exe', '-platform:x64', '-langversion:5', '-optimize+', '-codepage:65001',
        '-define:PAVISE_SELFTEST;PAVISE_RENDERER_BENCH', '-main:PaviseApp.RendererHandoffBench', ('-out:' + $benchOutput),
        '-reference:System.dll', '-reference:System.Core.dll', '-reference:System.Drawing.dll', '-reference:System.Windows.Forms.dll',
        '-reference:System.Management.dll', '-reference:System.Xml.dll', '-reference:System.Web.Extensions.dll')
    $compilerArgs += @($sourceFiles | ForEach-Object { $_.FullName })
    $compilerArgs += @($testFiles | ForEach-Object { $_.FullName })
    $compilerArgs += Join-Path $benchDirectory 'HandoffBench.cs'
    & $compiler @compilerArgs 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'compile-bench.log')
    if ($LASTEXITCODE -ne 0) { throw 'Bench compilation failed.' }

    $fixturePaths = @('fixtures\same-root\GameLauncher.exe', 'fixtures\same-root\GameRenderer.exe',
        'fixtures\same-root\Previous\OldRenderer.exe', 'fixtures\split-root\Menu\GameLauncher.exe',
        'fixtures\split-root\Client\GameRenderer.exe', 'fixtures\split-root\Previous\OldRenderer.exe')
    foreach ($relative in $fixturePaths) {
        $destination = Join-Path $runDirectory $relative
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
        Copy-Item -LiteralPath $helperOutput -Destination $destination
    }
    @($fixturePaths | ForEach-Object {
        $path = Join-Path $runDirectory $_
        [pscustomobject]@{ Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }) | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $runDirectory 'fixture-hashes.json') -Encoding utf8

    $stdout = Join-Path $runDirectory 'console.log'
    $stderr = Join-Path $runDirectory 'stderr.log'
    $benchProcess = Start-Process -FilePath $benchOutput -ArgumentList @(('"' + $runDirectory + '"'), [string]$Repeat) -WorkingDirectory $runDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $ownedBenchHandle = $benchProcess.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(60, $Repeat * 30))
    while (-not $benchProcess.WaitForExit(500)) {
        if ([DateTime]::UtcNow -gt $deadline) {
            $benchTimedOut = $true
            # Only manages the process created above, the helpers' stdin gets closed and their own timeouts are bounded
            if ($benchProcess.Handle -eq $ownedBenchHandle -and -not $benchProcess.HasExited) { $benchProcess.Kill() }
            $null = $benchProcess.WaitForExit(3000)
            throw 'The isolated bench exceeded its bounded runtime.'
        }
    }
    $benchExit = $benchProcess.ExitCode
    Get-Content -LiteralPath $stdout
    if ((Get-Item -LiteralPath $stderr).Length -gt 0) { Get-Content -LiteralPath $stderr }
}
finally {
    if ($null -ne $benchProcess) { $benchProcess.Dispose() }
    $changedInputs = @($before | Where-Object {
        -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256
    })
    $afterPaths = @(Get-ProtectedInputs | ForEach-Object { $_.FullName })
    $beforePaths = @($before | ForEach-Object { $_.Path })
    $addedPaths = @($afterPaths | Where-Object { $beforePaths -notcontains $_ })
    $removedPaths = @($beforePaths | Where-Object { $afterPaths -notcontains $_ })
    $inputSetUnchanged = $addedPaths.Count -eq 0 -and $removedPaths.Count -eq 0
    $productionChanged = @($changedInputs | Where-Object { $_.Path -eq $productionExe }).Count -gt 0 -or $productionPresentBefore -ne (Test-Path -LiteralPath $productionExe)
    $verification = [pscustomobject]@{
        SourceTestsBenchAndProductionExeUnchanged = ($changedInputs.Count -eq 0 -and $inputSetUnchanged)
        SourceAndProductionExeUnchanged = ($changedInputs.Count -eq 0 -and $inputSetUnchanged)
        InputSetUnchanged = $inputSetUnchanged
        ChangedInputs = $changedInputs
        AddedInputs = $addedPaths
        RemovedInputs = $removedPaths
        ProductionExecutableReplaced = $productionChanged
        Defines = @('PAVISE_SELFTEST', 'PAVISE_RENDERER_BENCH')
        EntryPoint = 'PaviseApp.RendererHandoffBench'
        FocusedTestSources = @($testFiles | ForEach-Object { $_.FullName })
        Repeat = $Repeat
        OutputDirectory = $runDirectory
        BenchExitCode = $benchExit
        BenchTimedOut = $benchTimedOut
    }
    $verification | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $runDirectory 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $runDirectory)
    if ($changedInputs.Count -gt 0 -or -not $inputSetUnchanged) { throw 'Inputs changed while the bench ran; inspect verification.json and rerun after freezing sources.' }
}
if ($benchExit -ne 0) { throw ('Bench failed with exit code ' + $benchExit) }
