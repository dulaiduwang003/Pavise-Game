param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build/SecurityStartup-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
$frozen = Join-Path $output 'frozen'
$null = New-Item -ItemType Directory -Path $frozen
$encoding = New-Object Text.UTF8Encoding($false)
foreach ($name in @('SecurityProductCatalog.cs', 'SecurityProductSnapshot.cs', 'SecurityStartupProbe.cs')) {
    Copy-Item -LiteralPath (Join-Path $workspace ('src/PaviseApp/' + $name)) -Destination (Join-Path $frozen $name)
}
Copy-Item -LiteralPath (Join-Path $workspace 'tests/SecurityStartupChecks.cs') -Destination (Join-Path $frozen 'SecurityStartupChecks.cs')
$probe = Join-Path $frozen 'SecurityStartupProbe.cs'
$source = [IO.File]::ReadAllText($probe)
$start = $source.IndexOf('        private static SecurityProductSnapshot Collect()', [StringComparison]::Ordinal)
$end = $source.IndexOf('        // A complete inventory', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'Frozen provider replacement boundary missing.' }
$source = $source.Substring(0, $start) + "        private static SecurityProductSnapshot Collect() { return SecurityStartupChecks.CollectFixture(); }`r`n`r`n" + $source.Substring($end)
$stopAnchor = 'private static void StopHelper(Process child)'
$stopStart = $source.IndexOf($stopAnchor, [StringComparison]::Ordinal)
$stopOpen = $source.IndexOf('{', $stopStart)
if ($stopStart -lt 0 -or $stopOpen -le $stopStart) { throw 'Frozen helper termination boundary missing.' }
$source = $source.Insert($stopOpen + 1, "`r`n            if (Environment.GetEnvironmentVariable(`"PAVISE_SECURITY_FIXTURE`") == `"termination`") throw new SecurityProbeTerminationException(`"Synthetic termination refusal`", null);`r`n")
$queryAnchor = 'if (!child.HasExited) child.Kill();'
if (([regex]::Matches($source, [regex]::Escape($queryAnchor))).Count -ne 1) { throw 'Frozen exit-query boundary missing.' }
$source = $source.Replace($queryAnchor, 'if (Environment.GetEnvironmentVariable("PAVISE_SECURITY_FIXTURE") == "exitquery") throw new System.ComponentModel.Win32Exception("Synthetic exit-state query failure"); ' + $queryAnchor)
[IO.File]::WriteAllText($probe, $source, $encoding)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $output 'SecurityStartupChecks.exe'
$sources = @(Get-ChildItem -LiteralPath $frozen -File -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /main:PaviseApp.SecurityStartupChecks ('/out:' + $exe) /reference:System.dll /reference:System.Core.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll $sources *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'compile.log'); throw 'Security startup fixture compile failed.' }
foreach ($mode in @('success', 'failure', 'timeout', 'termination', 'exitquery')) {
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = 'Hidden'
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['PAVISE_SECURITY_FIXTURE'] = $mode
    $startInfo.EnvironmentVariables['PAVISE_SECURITY_FIXTURE_LOG'] = Join-Path $output ($mode + '-launches.txt')
    $child = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(20000)) { $child.Kill(); throw 'Security startup fixture timed out.' }
        $result = $stdout.Result + $stderr.Result
        [IO.File]::WriteAllText((Join-Path $output ($mode + '-result.txt')), $result, $encoding)
        Write-Output $result
        if ($child.ExitCode -ne 0) { throw ('Security startup checks failed: ' + $mode) }
    } finally { $child.Dispose() }
}
Write-Output ('OUTPUT ' + $output)
