param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$sharedWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sharedWorkspace ('build\SharedManagedPowerChecks-' + [Guid]::NewGuid().ToString('N')) }
$sharedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $sharedOutput -Force
function Replace-SharedBody([string]$Text, [string]$Signature, [string]$Body) {
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $Text.IndexOf($Signature, $start + 1, [StringComparison]::Ordinal) -ge 0) { throw ('Missing or ambiguous fixture endpoint: ' + $Signature) }
    $open = $Text.IndexOf('{', $start); $depth = 1; $close = $open + 1
    while ($depth -gt 0 -and $close -lt $Text.Length) {
        if ($Text[$close] -eq '{') { $depth++ }
        if ($Text[$close] -eq '}') { $depth-- }
        $close++
    }
    if ($depth -ne 0) { throw ('Unbalanced fixture endpoint: ' + $Signature) }
    return $Text.Substring(0, $open) + '{ ' + $Body + ' }' + $Text.Substring($close)
}
$sharedSources = @(Get-ChildItem -LiteralPath (Join-Path $sharedWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$sharedAssets = @(Get-ChildItem -LiteralPath (Join-Path $sharedWorkspace 'src\Assets\PowerPlans') -File -Filter '*.pow')
if ($sharedAssets.Count -ne 7) { throw 'Exactly seven original power-plan resources must be embedded.' }
$sharedTests = @((Join-Path $sharedWorkspace 'tests\Official3106\MemorySettings.cs'), (Join-Path $sharedWorkspace 'tests\Official3106\SharedManagedPowerChecks.cs'), (Join-Path $sharedWorkspace 'tests\ManagedPowerPresetChecks.cs'))
$sharedManifest = @(@($sharedSources) + @($sharedAssets.FullName) + @($sharedTests) + @($PSCommandPath) | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$sharedManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sharedOutput 'production-inputs.json') -Encoding UTF8
$sharedEndpoints = [ordered]@{
    'private static IManagedPowerPlans ManagedPlans' = 'get { return SharedManagedPowerFixture.Plans; }'
    'private static ManagedPowerHardwareFacts ReadManagedHardwareFacts()' = 'return SharedManagedPowerFixture.Facts;'
    'private static Guid? Current()' = 'return SharedManagedPowerFixture.Plans.Current();'
    'private static bool Set(Guid g)' = 'return SharedManagedPowerFixture.Plans.Activate(g);'
    'private static bool SchemeUsable(Guid g)' = 'return SharedManagedPowerFixture.Plans.Exists(g);'
    'private static bool? RestorePlanIsUsable(Guid scheme)' = 'return SharedManagedPowerFixture.Plans.Exists(scheme);'
    'private static string ReadName(Guid g)' = 'return SharedManagedPowerFixture.Plans.Name(g);'
    'private static bool TryEnumerateSchemes(out List<Guid> list)' = 'list = new List<Guid> { SharedManagedPowerFixture.Base }; list.AddRange(SharedManagedPowerFixture.Plans.Created); list.AddRange(SharedManagedPowerFixture.Plans.LocalPlans); return true;'
    'private static bool SettingPresent(Guid scheme, Guid sub, Guid setting)' = 'return SharedManagedPowerFixture.Plans.Read(scheme, sub, setting, true).HasValue;'
    'private static uint Clamp(Guid sub, Guid setting, uint v)' = 'return v;'
    'private static bool RestoreLaboratoryBeforeCoreChange()' = 'return SharedManagedPowerFixture.LabRestoreOk;'
    'private static void ReadAutonomousScaling(Guid scheme, out bool? ac, out bool? dc)' = 'ac = true; dc = true;'
    'private static bool ImportPresetNative(ManagedPowerPreset preset, Guid destination)' = 'throw SharedManagedPowerFixture.Forbidden();'
    'internal static bool ReadAc(Guid scheme, Guid sub, Guid setting, out uint value)' = 'return SharedManagedPowerFixture.Read(scheme, sub, setting, true, out value);'
    'internal static bool ReadDc(Guid scheme, Guid sub, Guid setting, out uint value)' = 'return SharedManagedPowerFixture.Read(scheme, sub, setting, false, out value);'
    'internal static bool WriteAc(Guid scheme, Guid sub, Guid setting, uint value)' = 'return SharedManagedPowerFixture.Plans.Write(scheme, sub, setting, true, value);'
    'internal static bool WriteDc(Guid scheme, Guid sub, Guid setting, uint value)' = 'return SharedManagedPowerFixture.Plans.Write(scheme, sub, setting, false, value);'
    'internal static void SyncDisplayFeel(Guid src, Guid dst)' = 'foreach (Guid setting in new Guid[] { VideoBrightness, VideoDimBrightness, VideoAdaptive }) { uint value; if (ReadAc(src, SubVideo, setting, out value)) WriteAc(dst, SubVideo, setting, value); if (ReadDc(src, SubVideo, setting, out value)) WriteDc(dst, SubVideo, setting, value); }'
}
$sharedReplaced = @{}; $sharedWriteEndpoints = 0
$sharedInputs = @()
foreach ($source in $sharedSources) {
    if ($source -eq (Join-Path $sharedWorkspace 'src\PaviseApp\Settings.cs')) { continue }
    if ([IO.Path]::GetFileName($source) -notlike 'PowerPlan*.cs') { $sharedInputs += $source; continue }
    $copy = [IO.File]::ReadAllText($source)
    foreach ($signature in $sharedEndpoints.Keys) {
        if ($copy.Contains($signature)) { $copy = Replace-SharedBody $copy $signature $sharedEndpoints[$signature]; $sharedReplaced[$signature] = $true }
    }
    $copy = $copy.Replace('Native.HasSystemBattery()', 'SharedManagedPowerFixture.Facts.Mobile').Replace('CpuTopology.Hybrid', 'SharedManagedPowerFixture.Facts.Hybrid').Replace('CpuTopology.AsymCache', 'SharedManagedPowerFixture.Facts.AsymmetricCache')
    # Reads are guarded as well as writes. Unanticipated native calls fail instead of touching the machine.
    $primitive = '\[DllImport\([^\]]+\)\]\s*private static extern uint ((?:Power\w+|EnumerateProcessorSetting|EnumeratePresetSubgroup))\(([^;]+)\);'
    $sharedWriteEndpoints += [regex]::Matches($copy, $primitive).Count
    $copy = [regex]::Replace($copy, $primitive, 'private static uint $1($2) { throw SharedManagedPowerFixture.Forbidden(); }')
    $frozen = Join-Path $sharedOutput ([IO.Path]::GetFileNameWithoutExtension($source) + '.memory.cs')
    [IO.File]::WriteAllText($frozen, $copy, (New-Object Text.UTF8Encoding($true)))
    $sharedInputs += $frozen
}
foreach ($required in @('private static IManagedPowerPlans ManagedPlans', 'private static ManagedPowerHardwareFacts ReadManagedHardwareFacts()', 'private static bool ImportPresetNative(ManagedPowerPreset preset, Guid destination)', 'private static bool RestoreLaboratoryBeforeCoreChange()', 'private static bool? RestorePlanIsUsable(Guid scheme)')) {
    if (-not $sharedReplaced.ContainsKey($required)) { throw ('Missing required native boundary: ' + $required) }
}
if ($sharedWriteEndpoints -lt 7) { throw 'Expected native power boundaries were not found.' }
$sharedInputs += $sharedTests
[IO.File]::WriteAllText((Join-Path $sharedOutput 'fixture-endpoints.txt'), 'Output copies only: memory Settings, hardware facts, plan import/enumeration/indices/activation and laboratory recovery. Production source selection, lifecycle, receipt ordering, AC/DC adaptation and exact restoration stay intact. Native power entry points and native powercfg import throw. Seven original binary resources are embedded with pinned identities. The historical 2223 digest is retained as history and is not the expectation for this new strategy. No product entry point runs.')
$sharedCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Build-SharedFixture([string[]]$Inputs, [string]$Name) {
    $exe = Join-Path $sharedOutput ($Name + '.exe'); $rsp = Join-Path $sharedOutput ($Name + '.rsp')
    $lines = @($Inputs | ForEach-Object { '"' + $_ + '"' })
    $lines += @($sharedAssets | ForEach-Object { '-resource:"' + $_.FullName + '",Pavise.PowerPlans.' + $_.Name })
    [IO.File]::WriteAllLines($rsp, [string[]]$lines, (New-Object Text.UTF8Encoding($true)))
    & $sharedCompiler -nologo -unsafe+ -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        -main:PaviseApp.SharedManagedPowerChecks ('-out:' + $exe) -reference:System.dll -reference:System.Core.dll `
        -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll `
        -reference:System.Xml.dll -reference:System.Security.dll ('@' + $rsp) *> (Join-Path $sharedOutput ($Name + '.compile.log'))
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $sharedOutput ($Name + '.compile.log')); throw 'Shared managed power fixture did not compile.' }
    return $exe
}
function Run-SharedFixture([string]$Exe, [string]$Name) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe; $start.Arguments = '"' + $sharedOutput + '"'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Shared managed power fixture timed out.' }
        $process.WaitForExit(); $result = $stdout.Result + $stderr.Result
        [IO.File]::WriteAllText((Join-Path $sharedOutput ($Name + '.result.txt')), $result)
        return [pscustomobject]@{ExitCode=$process.ExitCode;Text=$result}
    } finally { $process.Dispose() }
}
$sharedExe = Build-SharedFixture $sharedInputs 'SharedManagedPowerChecks'
$sharedResult = Run-SharedFixture $sharedExe 'SharedManagedPowerChecks'
Write-Output $sharedResult.Text
if ($sharedResult.ExitCode -ne 0) { throw 'Shared managed power checks failed.' }
# A production policy mutation must be caught by independently stated mode expectations.
$sharedPolicyOriginal = Join-Path $sharedWorkspace 'src\PaviseApp\PerformancePolicy.cs'
$sharedNegativePath = Join-Path $sharedOutput 'PerformancePolicy.negative.cs'
$sharedNegative = Replace-SharedBody ([IO.File]::ReadAllText($sharedPolicyOriginal)) 'internal static bool SceneTunesProcessor(PerformancePreset mode)' 'return PresetValue.IsValid((int)mode);'
[IO.File]::WriteAllText($sharedNegativePath, $sharedNegative, (New-Object Text.UTF8Encoding($true)))
$sharedNegativeInputs = @($sharedInputs | Where-Object { $_ -ne $sharedPolicyOriginal }) + $sharedNegativePath
$sharedNegativeExe = Build-SharedFixture $sharedNegativeInputs 'SharedManagedPowerNegative'
$sharedNegativeResult = Run-SharedFixture $sharedNegativeExe 'SharedManagedPowerNegative'
if ($sharedNegativeResult.ExitCode -ne 1 -or $sharedNegativeResult.Text -notmatch 'Mode processor-tuning admission changed') { throw 'The mode-policy negative control did not fail at the expected assertion.' }
$sharedChanged = @($sharedManifest | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
if ($sharedChanged.Count) { throw 'Production or fixture inputs changed during verification; rerun against stable sources.' }
$sharedCurrentSources = @(Get-ChildItem -LiteralPath (Join-Path $sharedWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$sharedCurrentAssets = @(Get-ChildItem -LiteralPath (Join-Path $sharedWorkspace 'src\Assets\PowerPlans') -File -Filter '*.pow' | Select-Object -ExpandProperty FullName)
if (@(Compare-Object $sharedSources $sharedCurrentSources).Count -or @(Compare-Object @($sharedAssets.FullName) $sharedCurrentAssets).Count) {
    throw 'Production or resource inventory changed during verification; rerun against stable sources.'
}
[pscustomobject]@{ExitCode=0;InputCount=$sharedManifest.Count;ChangedInputs=0;NegativeControlExit=$sharedNegativeResult.ExitCode;NativePowerEndpointsGuarded=$sharedWriteEndpoints;OriginalResources=7;PaviseStarted=$false;Output=$sharedOutput} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sharedOutput 'verification.json') -Encoding UTF8
Write-Output 'PASS mode-policy negative control rejected; source and fixture manifests unchanged'
Write-Output ('OUTPUT ' + $sharedOutput)
