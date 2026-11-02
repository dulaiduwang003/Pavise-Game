param([string]$OutputDirectory, [string]$BaselineDirectory, [switch]$CompileOnly, [switch]$AssertImprovement, [switch]$PrivacyCacheOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$latencyWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-UiSwitchLatency-' + [Guid]::NewGuid().ToString('N')) }
$latencyOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $latencyOutput -Force
$latencySources = @(& rg --files (Join-Path $latencyWorkspace 'src') -g '*.cs')
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate product sources' }
foreach ($relative in @('tests/UiSwitchLatencyChecks.cs')) {
    $latencySources += Join-Path $latencyWorkspace $relative
}
$latencyInputs = @($latencySources | ForEach-Object {
    $original = [IO.Path]::GetFullPath($_)
    $relative = $original.Substring($latencyWorkspace.Length + 1)
    $frozen = Join-Path (Join-Path $latencyOutput 'frozen-inputs') $relative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
    [IO.File]::WriteAllBytes($frozen, [IO.File]::ReadAllBytes($original))
    [pscustomobject]@{ Path = $relative; Original = $original; Frozen = $frozen; SHA256 = (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$latencyInputs | Export-Csv -LiteralPath (Join-Path $latencyOutput 'input-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $latencyOutput 'Test-UiSwitchLatency.ps1'), [IO.File]::ReadAllBytes($PSCommandPath))
$latencyPresentation = Join-Path $latencyOutput 'frozen-inputs/src/Ui/PanelForm.Presentation.cs'
$latencySource = [IO.File]::ReadAllText($latencyPresentation)
$latencyRebuildAnchor = [regex]::new('(?m)^([ \t]*private void RebuildUi\(\)[ \t]*\r?\n[ \t]*\{)(\r?\n)')
if ($latencyRebuildAnchor.Matches($latencySource).Count -ne 1) { throw 'Cannot identify the RebuildUi entry point for benchmark instrumentation' }
$latencySource = $latencyRebuildAnchor.Replace($latencySource, {
    param($match)
    $match.Groups[1].Value + $match.Groups[2].Value + '            UiSwitchLatencyChecks.Stage("rebuild.enter");' + $match.Groups[2].Value
}, 1)
foreach ($latencyStage in @(
    @('RefreshStrategyControls();', 'mode.strategy'),
    @('PerformancePreset effective = gameMode.ActivePreset;', 'mode.strategy.done'),
    @('using (Icon icon = IconArt.MakeMultiIcon(effective, enabled)) SetRuntimeIcon(icon);', 'mode.icon'),
    @('RefreshAutomationPage();', 'mode.icon.done'),
    @('Theme.SetLight(light);', 'theme.palette'),
    @('Logger.Log(Lang.T("log.panelform.8")', 'theme.palette.done'),
    @('Controls.Clear();', 'rebuild.clear'),
    @('foreach (var c in old) c.Dispose();', 'rebuild.dispose'),
    @('acGroups.Clear(); acCards.Clear(); acToggles.Clear();', 'rebuild.dispose.done'),
    @('BuildUi(appIcon);', 'rebuild.build'),
    @('mainReturnPage = keepReturn;', 'rebuild.build.done'),
    @('if (UiActive) RefreshSlowStateAsync();', 'rebuild.exit')
)) {
    if (-not $latencySource.Contains($latencyStage[0])) { Write-Verbose ('Benchmark stage no longer present: ' + $latencyStage[0]); continue }
    $latencySource = $latencySource.Replace($latencyStage[0], ('UiSwitchLatencyChecks.Stage("' + $latencyStage[1] + '"); ' + $latencyStage[0]))
}
[IO.File]::WriteAllText($latencyPresentation, $latencySource, (New-Object Text.UTF8Encoding($false)))
$latencyInputs | ForEach-Object { [pscustomobject]@{Path=$_.Path; OriginalSHA256=$_.SHA256; TestedSHA256=(Get-FileHash -LiteralPath $_.Frozen -Algorithm SHA256).Hash.ToLowerInvariant()} } | Export-Csv -LiteralPath (Join-Path $latencyOutput 'instrumented-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
function Invoke-LatencyHidden([string]$File, [string]$Arguments, [int]$LimitMs) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File; $start.Arguments = $Arguments; $start.WorkingDirectory = $latencyOutput
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $start.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    $child = New-Object Diagnostics.Process; $child.StartInfo = $start
    try {
        $null = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        $watch = [Diagnostics.Stopwatch]::StartNew(); $timedOut = $false
        while (-not $child.WaitForExit(1000)) {
            if ($watch.ElapsedMilliseconds -gt $LimitMs) { $timedOut = $true; $child.Kill(); break }
        }
        $child.WaitForExit()
        [pscustomobject]@{ Exit = $(if ($timedOut) { 124 } else { $child.ExitCode }); Text = $stdout.Result + $stderr.Result; TimedOut = $timedOut }
    } finally { $child.Dispose() }
}
$latencyCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$latencyExe = Join-Path $latencyOutput 'UiSwitchLatencyChecks.exe'
[IO.File]::WriteAllBytes(($latencyExe + '.config'), [IO.File]::ReadAllBytes((Join-Path $latencyWorkspace 'Pavise.exe.config')))
$latencyManifest = Join-Path $latencyOutput 'fixture.manifest'
$latencyManifestText = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <assemblyIdentity version="1.0.0.0" name="Pavise.UiSwitchLatencyChecks" />
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
 <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
  <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
 </windowsSettings></application>
 <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /></application></compatibility>
</assembly>
'@
[IO.File]::WriteAllText($latencyManifest, $latencyManifestText, (New-Object Text.UTF8Encoding($false)))
$latencyRsp = Join-Path $latencyOutput 'sources.rsp'
$latencyArgs = @('/nologo', '/unsafe+', '/target:exe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', '/nowarn:0162,0649',
    '/define:PAVISE_SELFTEST;PAVISE_SWITCH_LATENCY_CHECKS',
    '/main:PaviseApp.UiSwitchLatencyChecks', ('/out:"' + $latencyExe + '"'),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll', '/reference:System.Xml.dll', ('/win32manifest:"' + $latencyManifest + '"'))
$latencyArgs += @($latencyInputs | ForEach-Object { '"' + $_.Frozen + '"' })
[IO.File]::WriteAllLines($latencyRsp, $latencyArgs, (New-Object Text.UTF8Encoding($false)))
$latencyResults = New-Object 'Collections.Generic.List[object]'
try {
    $compile = Invoke-LatencyHidden $latencyCompiler ('@"' + $latencyRsp + '"') 60000
    [IO.File]::WriteAllText((Join-Path $latencyOutput 'compile.log'), $compile.Text)
    $latencyResults.Add([pscustomobject]@{ Step = 'compile'; Exit = $compile.Exit; Pass = ($compile.Exit -eq 0) })
    if ($compile.Exit -ne 0) { throw ('UI switch latency fixture did not compile: ' + $compile.Text) }
    if (-not $CompileOnly) {
        $latencyMode = if ($PrivacyCacheOnly) { ' --privacy-cache-only' } else { '' }
        $run = Invoke-LatencyHidden $latencyExe ('"' + $latencyOutput + '"' + $latencyMode) 60000
        [IO.File]::WriteAllText((Join-Path $latencyOutput 'result.txt'), $run.Text); Write-Output $run.Text
        $pass = $run.Exit -eq 0 -and $run.Text -match 'PASS UI switch latency;'
        $latencyResults.Add([pscustomobject]@{ Step = 'private desktop UI'; Exit = $run.Exit; TimedOut = $run.TimedOut; Pass = $pass })
        if (-not $pass) { throw 'UI switch latency UI checks failed or timed out' }
        function Get-LatencySummary([string]$Directory) {
            $rows = @(Import-Csv -LiteralPath (Join-Path $Directory 'latency.tsv') -Delimiter ([char]9))
            foreach ($group in @($rows | Group-Object -Property { if ($_.Operation.StartsWith('mode.')) { 'mode' } elseif ($_.Operation.StartsWith('theme.policy.')) { 'theme.policy' } else { 'theme.overview' } })) {
                $summary = [ordered]@{Operation=$group.Name; Samples=$group.Count; Rebuilds=(@($group.Group | ForEach-Object {[int]$_.Rebuilds}) | Measure-Object -Sum).Sum}
                foreach ($metric in @('HandlerMs','FirstCompletePaintMs','HeartbeatMaxMs')) {
                    $values = @($group.Group | ForEach-Object { [double]::Parse($_.$metric, [Globalization.CultureInfo]::InvariantCulture) } | Sort-Object)
                    $summary[$metric + 'P50'] = $values[[Math]::Max(0, [Math]::Ceiling($values.Count * 0.5) - 1)]
                    $summary[$metric + 'P95'] = $values[[Math]::Max(0, [Math]::Ceiling($values.Count * 0.95) - 1)]
                }
                [pscustomobject]$summary
            }
        }
        $summary = if ($PrivacyCacheOnly) { @() } else { @(Get-LatencySummary $latencyOutput) }
        $comparison = @()
        if ($BaselineDirectory) {
            $baseline = @(Get-LatencySummary ([IO.Path]::GetFullPath($BaselineDirectory)))
            $comparison = @($summary | ForEach-Object {
                $currentGroup = $_; $old = $baseline | Where-Object Operation -eq $currentGroup.Operation | Select-Object -First 1
                if ($old) { [pscustomobject]@{Operation=$_.Operation; BaselineFirstPaintP95=$old.FirstCompletePaintMsP95; CurrentFirstPaintP95=$_.FirstCompletePaintMsP95;
                    FirstPaintImprovementPercent=[Math]::Round(100 * (1 - $_.FirstCompletePaintMsP95 / $old.FirstCompletePaintMsP95), 1);
                    BaselineHeartbeatP95=$old.HeartbeatMaxMsP95; CurrentHeartbeatP95=$_.HeartbeatMaxMsP95; Rebuilds=$_.Rebuilds} }
            })
        }
        [pscustomobject]@{Current=$summary; Comparison=$comparison} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $latencyOutput 'summary.json') -Encoding UTF8
        if ($AssertImprovement) {
            if (-not $BaselineDirectory) { throw 'AssertImprovement needs BaselineDirectory' }
            foreach ($item in @($comparison | Where-Object {$_.Operation.StartsWith('theme.')})) {
                if ($item.CurrentFirstPaintP95 -gt $item.BaselineFirstPaintP95 * 0.7) { throw ('Theme first-paint improvement below 30%: ' + $item.Operation) }
            }
        }
    } else { Write-Output 'PASS UI switch latency compile-only; fixture not executed' }
} finally {
    $changed = @($latencyInputs | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256 })
    [pscustomobject]@{ CompileOnly = [bool]$CompileOnly; PrivacyCacheOnly = [bool]$PrivacyCacheOnly; BaselineDirectory = $BaselineDirectory; AssertImprovement = [bool]$AssertImprovement; TestedFrozenSourceCount = $latencyInputs.Count;
        WorkspaceMatchesSnapshot = ($changed.Count -eq 0); ChangedSources = @($changed | ForEach-Object { $_.Path });
        Screenshots = @(Get-ChildItem -LiteralPath $latencyOutput -File -Filter '*.png' | ForEach-Object { $_.Name })
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $latencyOutput 'verification.json') -Encoding UTF8
    $latencyResults | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $latencyOutput 'results.json') -Encoding UTF8
    Write-Output ('OUTPUT ' + $latencyOutput)
}
if ($changed.Count -ne 0) { throw 'Source changed during checks; results apply only to retained frozen inputs' }
