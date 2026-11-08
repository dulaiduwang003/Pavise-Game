param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-PublicTests-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; earlier results are retained.' }
$null = New-Item -ItemType Directory -Path $output
$suites = @(
    @{Name='SmartGovernanceThreshold'; Arguments=''},
    @{Name='BackgroundGovernanceRuntime'; Arguments=''},
    @{Name='ConfigurationPresetReads'; Arguments=''},
    @{Name='ConfigurationPresetIdentityRetry'; Arguments=' -Mutations'},
    @{Name='AutomaticRetryOwner'; Arguments=''},
    @{Name='ConsumerRecovery'; Arguments=''},
    @{Name='TimerRetryBoundary'; Arguments=''},
    @{Name='TamerRetry'; Arguments=''},
    @{Name='SecurityStartup'; Arguments=''},
    @{Name='SourceCleanup'; Arguments=''}
)
$results = New-Object 'Collections.Generic.List[object]'
foreach ($suite in $suites) {
    $script = Join-Path $PSScriptRoot ('Test-' + $suite.Name + '.ps1')
    if (-not (Test-Path -LiteralPath $script)) { throw ('Missing test entry point: ' + $suite.Name) }
    $suiteOutput = Join-Path $output $suite.Name
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -OutputDirectory "' + $suiteOutput + '"' + $suite.Arguments
    $start.WorkingDirectory=$workspace; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $child = [Diagnostics.Process]::Start($start)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(180000)) { $child.Kill(); throw ('Suite exceeded 180 seconds; no automatic retry: ' + $suite.Name) }
        $child.WaitForExit(); $text=$stdout.Result+$stderr.Result
        [IO.File]::WriteAllText((Join-Path $output ($suite.Name + '.log')), $text)
        $passed = $child.ExitCode -eq 0
        $results.Add([pscustomobject]@{Name=$suite.Name;Passed=$passed;ExitCode=$child.ExitCode;Seconds=$watch.Elapsed.TotalSeconds})
        Write-Output ('[' + $suite.Name + '] ' + $text.Trim())
        if (-not $passed) { throw ('Public test failed; results retained without a retry: ' + $suite.Name) }
    } finally {
        $watch.Stop(); $child.Dispose()
        [pscustomobject]@{Version='3.3.0.8';Passed=$results.Count -eq $suites.Count -and @($results | Where-Object { -not $_.Passed }).Count -eq 0;Suites=$results.ToArray();ProductStarted=$false;ResultsAreGameBenchmarks=$false} |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding UTF8
    }
}
Write-Output ('PASS ' + $results.Count + ' public suites; OUTPUT ' + $output)
