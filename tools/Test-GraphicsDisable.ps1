param([string]$Output = 'build/GraphicsDisable-final')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath((Join-Path $workspace $Output))
$sources = Join-Path $outputRoot 'sources'
[IO.Directory]::CreateDirectory($sources) | Out-Null
$files = @('src/PaviseApp/GraphicsPreferenceTransaction.cs','src/PaviseApp/DirectXGlobalPreference.cs','src/PaviseApp/AmdSamPreference.cs','src/PaviseApp/AmdSamTweak.cs','src/PaviseApp/HagsPreference.cs','src/PaviseApp/HagsTweak.cs','src/PaviseApp/PrefFieldText.cs','tests/Official3106/GraphicsDisableChecks.cs')
$manifest = @()
foreach ($relative in $files) {
    $source = Join-Path $workspace $relative
    $destination = Join-Path $sources ([IO.Path]::GetFileName($relative))
    [IO.File]::WriteAllBytes($destination,[IO.File]::ReadAllBytes($source))
    $manifest += [pscustomobject]@{Source=$relative;SHA256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash}
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'production-inputs.json') -Encoding UTF8
$panelPath = Join-Path $workspace 'src/PaviseApp/PanelForm.cs'
$panel = [IO.File]::ReadAllText($panelPath)
$manifest += [pscustomobject]@{Source='src/PaviseApp/PanelForm.cs';SHA256=(Get-FileHash -LiteralPath $panelPath -Algorithm SHA256).Hash}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'production-inputs.json') -Encoding UTF8
function Get-Method([string]$Source, [string]$Declaration) {
    $start = $Source.IndexOf($Declaration, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $Source.IndexOf($Declaration, $start + $Declaration.Length, [StringComparison]::Ordinal) -ge 0) { throw ('Method anchor missing/ambiguous: ' + $Declaration) }
    $brace = $Source.IndexOf('{', $start); $depth = 1; $end = $brace + 1
    while ($end -lt $Source.Length -and $depth -gt 0) { if ($Source[$end] -eq '{') { $depth++ }; if ($Source[$end] -eq '}') { $depth-- }; $end++ }
    if ($depth -ne 0) { throw 'Method extraction failed' }
    return $Source.Substring($start, $end - $start)
}
$ui = 'using System; namespace PaviseApp { internal static class PanelForm { private static string AutoText(string zh, string en) { return zh; } internal static void ApplyGraphics(EnvironmentPreferenceObservation value, SettingCard card, Toggle toggle) { ApplyEnvironmentPreference(value, card, toggle); }' +
    (Get-Method $panel 'private static void ApplyEnvironmentPreference(') +
    (Get-Method $panel 'private static void LockEnvCard(') +
    (Get-Method $panel 'internal static HagsUiPresentation DescribeHags(') + '} }'
$uiFile = Join-Path $sources 'GraphicsPresentation.cs'
[IO.File]::WriteAllText($uiFile,$ui,[Text.UTF8Encoding]::new($false))
# The extracted presentation methods use only the observed snapshot, never a window or native endpoint.
foreach ($binding in @('toggle.Checked ? preference.Enable() : preference.Disable()', 'swAmdSam.Checked ? AmdSamTweak.Enable() : AmdSamTweak.Disable()', 'hagsUiPresentation.Checked = flag2 ? false')) {
    if (-not $panel.Contains($binding)) { throw ('Explicit Off presentation/binding missing: ' + $binding) }
}
# All native I/O is replaced only in frozen copies. Real DirectX field/receipt
# adapters execute against in-memory raw registry text; HAGS driver discovery is fake.
$dxFile = Join-Path $sources 'DirectXGlobalPreference.cs'
$dx = [IO.File]::ReadAllText($dxFile)
$dxRead = Get-Method $dx 'private static string Read('
$dxWrite = Get-Method $dx 'private static void Write('
$dx = $dx.Replace($dxRead,'private static string Read() { return GraphicsDisableChecks.DirectXRegistryRaw; }')
$dx = $dx.Replace($dxWrite,'private static void Write(string value) { GraphicsDisableChecks.DirectXMemoryWrites++; GraphicsDisableChecks.DirectXRegistryRaw = value; }')
[IO.File]::WriteAllText($dxFile,$dx,[Text.UTF8Encoding]::new($false))
$hagsFile = Join-Path $sources 'HagsPreference.cs'
$hags = [IO.File]::ReadAllText($hagsFile)
if (($hags.Split(@('new NativeStore()'),[StringSplitOptions]::None).Length - 1) -ne 1) { throw 'HAGS isolation anchor missing' }
$hags = $hags.Replace('new NativeStore()', 'new DeferredFakeStore()')
$hags = $hags.Replace('private sealed class NativeStore : IGraphicsPreferenceStore', 'private sealed class DeferredFakeStore : IGraphicsPreferenceStore { public string ReadValue() { return GraphicsDisableChecks.HagsStore.ReadValue(); } public bool WriteValue(string value) { return GraphicsDisableChecks.HagsStore.WriteValue(value); } public string LoadReceipt() { return GraphicsDisableChecks.HagsStore.LoadReceipt(); } public bool SaveReceipt(string value) { bool ok = GraphicsDisableChecks.HagsStore.SaveReceipt(value); Settings.Values["PrevHwSch"] = GraphicsDisableChecks.HagsStore.Receipt; return ok; } } private sealed class NativeStore : IGraphicsPreferenceStore')
[IO.File]::WriteAllText($hagsFile,$hags,[Text.UTF8Encoding]::new($false))
$hagsWrapperFile = Join-Path $sources 'HagsTweak.cs'
$hagsWrapperSource = [IO.File]::ReadAllText($hagsWrapperFile)
$queryMethod = Get-Method $hagsWrapperSource 'public static bool TryQueryState('
$hagsWrapperSource = $hagsWrapperSource.Replace($queryMethod, 'public static bool TryQueryState(out bool supported, out bool enabled) { supported = GraphicsDisableChecks.HagsSupported; enabled = true; return GraphicsDisableChecks.HagsKnown; }')
$configuredMethod = Get-Method $hagsWrapperSource 'internal static int? ConfiguredMode('
$hagsWrapperSource = $hagsWrapperSource.Replace($configuredMethod, 'internal static int? ConfiguredMode() { throw new System.InvalidOperationException("Native registry query forbidden"); }')
[IO.File]::WriteAllText($hagsWrapperFile,$hagsWrapperSource,[Text.UTF8Encoding]::new($false))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $outputRoot 'GraphicsDisableChecks.exe'
$arguments = '/nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /main:PaviseApp.GraphicsDisableChecks /out:"' + $exe + '" "' + $uiFile + '" ' + (($files | ForEach-Object { '"' + (Join-Path $sources ([IO.Path]::GetFileName($_))) + '"' }) -join ' ')
function Invoke-Hidden([string]$File, [string]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new(); $info.FileName=$File; $info.Arguments=$Arguments; $info.WorkingDirectory=$outputRoot
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    try { $null=$process.Start(); $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync(); if(-not $process.WaitForExit(30000)){ $process.Kill(); throw 'Test timeout' }; $process.WaitForExit(); return [pscustomobject]@{ExitCode=$process.ExitCode;Text=$stdout.Result+$stderr.Result} }
    finally { $process.Dispose() }
}
$compile = Invoke-Hidden $compiler $arguments
$compile.Text | Set-Content -LiteralPath (Join-Path $outputRoot 'compile.log') -Encoding UTF8
if ($compile.ExitCode -ne 0) { throw $compile.Text }
$run = Invoke-Hidden $exe ''
$run.Text | Set-Content -LiteralPath (Join-Path $outputRoot 'checks.log') -Encoding UTF8
if ($run.ExitCode -ne 0 -or $run.Text -notmatch 'PASS \d+ graphics') { throw $run.Text }
$negative = Invoke-Hidden $exe '--negative-control'
if ($negative.ExitCode -ne 1 -or $negative.Text -notmatch 'intentional negative control') { throw 'Negative control unexpectedly accepted' }
$transactionFile = Join-Path $sources 'GraphicsPreferenceTransaction.cs'
$transactionSource = [IO.File]::ReadAllText($transactionFile)
$mutationAnchor = 'current != applied && current != previous'
if (($transactionSource.Split(@($mutationAnchor),[StringSplitOptions]::None).Length - 1) -ne 2) { throw 'Transition mutation anchors changed' }
$mutantFile = Join-Path $outputRoot 'GraphicsPreferenceTransaction-missing-transition.cs'
[IO.File]::WriteAllText($mutantFile,$transactionSource.Replace($mutationAnchor,'current != applied'),[Text.UTF8Encoding]::new($false))
$mutantExe = Join-Path $outputRoot 'GraphicsDisableChecks-missing-transition.exe'
$mutantArgs = $arguments.Replace('"' + $transactionFile + '"','"' + $mutantFile + '"').Replace('/out:"' + $exe + '"','/out:"' + $mutantExe + '"')
$mutantCompile = Invoke-Hidden $compiler $mutantArgs
if ($mutantCompile.ExitCode -ne 0) { throw ('Mutant did not compile: ' + $mutantCompile.Text) }
$mutantRun = Invoke-Hidden $mutantExe ''
$mutantRun.Text | Set-Content -LiteralPath (Join-Path $outputRoot 'missing-transition-negative.log') -Encoding UTF8
if ($mutantRun.ExitCode -ne 1 -or $mutantRun.Text -notmatch 'FAIL: retarget failure restores original') { throw ('Missing transition guard was not rejected: ' + $mutantRun.Text) }
$changed = @($manifest | Where-Object { (Get-FileHash -LiteralPath (Join-Path $workspace $_.Source) -Algorithm SHA256).Hash -ne $_.SHA256 })
if ($changed.Count -ne 0) { throw 'Production inputs changed during tests' }
[ordered]@{Checks=$run.Text.Trim();NegativeControlRejected=$true;MissingTransitionGuardRejected=$true;SourceChanges=0;NativeRegistryWrites=$false;NativeDriverWrites=$false;ProductStarted=$false} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $outputRoot 'verification.json')
