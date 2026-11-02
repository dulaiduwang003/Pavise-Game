param(
    [ValidateSet('All','Power','CurrentIdle','EppEligibility','Gpu','Audio','Integration','Lifecycle','Uwp')][string]$Mode = 'All',
    [string]$SourceRoot = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $SourceRoot) { $SourceRoot = Join-Path $workspace 'src/PaviseApp' }
$sourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$appRoot = if (Test-Path -LiteralPath (Join-Path $sourceRoot 'PaviseApp')) { Join-Path $sourceRoot 'PaviseApp' } else { $sourceRoot }
$testRoot = Join-Path $workspace 'tests'
$fixtureRoot = Join-Path $testRoot 'Official3106'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build/OfficialLaboratory-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$outRoot = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $outRoot -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$fullSources = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
if ([IO.Path]::GetFileName($sourceRoot) -eq 'PaviseApp') {
    $fullSources += @(Get-ChildItem -LiteralPath (Split-Path $sourceRoot -Parent) -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
}
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'LaboratorySession.cs'))) { throw 'SourceRoot must contain the migrated production LaboratorySession.cs' }
$productionManifest = @($fullSources | ForEach-Object { [pscustomobject]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$productionManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outRoot 'production-source-manifest.json') -Encoding UTF8
function Replace-MethodBody([string]$Text, [string]$Signature, [string]$Body) {
    # These fixed primitive methods contain no braces inside literals; count their balanced C# body.
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0 -or $Text.IndexOf($Signature, $start + 1, [StringComparison]::Ordinal) -ge 0) { throw ('Missing or ambiguous test seam: ' + $Signature) }
    $open = $Text.IndexOf('{', $start); $depth = 1; $close = $open + 1
    while ($depth -gt 0 -and $close -lt $Text.Length) {
        if ($Text[$close] -eq '{') { $depth++ }
        if ($Text[$close] -eq '}') { $depth-- }
        $close++
    }
    if ($depth -ne 0) { throw ('Unbalanced primitive body: ' + $Signature) }
    return $Text.Substring(0,$open) + '{ ' + $Body + ' }' + $Text.Substring($close)
}
function Sources-WithPrimitives([string]$Kind) {
    $scratch = Join-Path $outRoot ('test-primitives-' + $Kind)
    $null = New-Item -ItemType Directory -Path $scratch -Force
    $sources = @($fullSources | Where-Object { [IO.Path]::GetFileName($_) -ne 'Settings.cs' })
    $sources += Join-Path $fixtureRoot 'MemorySettings.cs'
    $sources += Join-Path $fixtureRoot 'PlatformMocks.cs'
    if ($Kind -eq 'Lifecycle') {
        $original = Join-Path $appRoot 'PowerPlan.cs'
        $text = [IO.File]::ReadAllText($original)
        $text = Replace-MethodBody $text 'private static Guid? RestoreCurrentPlan()' 'return OfficialPlatformMocks.Plan;'
        $text = Replace-MethodBody $text 'private static bool RestoreSetPlan(Guid scheme)' 'return OfficialPlatformMocks.ForbiddenPowerWrite();'
        # Any unexpected route into a power write is a hard test failure, never a native operation.
        $pattern = '\[DllImport\([^\]]+\)\]\s*private static extern uint (Power(?:Set|Write|Delete|Duplicate)\w+)\(([^;]+)\);'
        $matches = [regex]::Matches($text, $pattern)
        if ($matches.Count -ne 7) { throw ('Unexpected number of power write primitives: ' + $matches.Count) }
        $text = [regex]::Replace($text, $pattern, 'private static uint $1($2) { throw new InvalidOperationException("Native power writes forbidden in tests"); }')
        $target = Join-Path $scratch 'PowerPlan.cs'
    } else {
        $original = Join-Path $appRoot 'PrivacyNativeBackend.cs'
        $text = [IO.File]::ReadAllText($original)
        $text = Replace-MethodBody $text 'private static string CurrentMachine()' 'return OfficialPlatformMocks.Machine;'
        $text = Replace-MethodBody $text 'private static string CurrentUser()' 'return OfficialPlatformMocks.User;'
        $text = Replace-MethodBody $text 'private static PrivacyPlatform Platform()' 'return OfficialPlatformMocks.Privacy;'
        $text = Replace-MethodBody $text 'private static string RegistryRead(string target)' 'return OfficialPlatformMocks.ReadRegistry(target);'
        $text = Replace-MethodBody $text 'private static bool RegistryWrite(string target, string text)' 'return OfficialPlatformMocks.WriteRegistry(target,text);'
        $target = Join-Path $scratch 'PrivacyNativeBackend.cs'
        $memoryOriginal = Join-Path $appRoot 'EnvironmentEnhancementEligibility.cs'
        $memory = Replace-MethodBody ([IO.File]::ReadAllText($memoryOriginal)) 'private static MemorySnapshot Sample()' 'return new MemorySnapshot { Installed=8*GiB, Total=8*GiB, Available=GiB };'
        $memoryTarget = Join-Path $scratch 'EnvironmentEnhancementEligibility.cs'
        [IO.File]::WriteAllText($memoryTarget,$memory)
        $sources = @($sources | Where-Object { $_ -ne $memoryOriginal }) + $memoryTarget
    }
    [IO.File]::WriteAllText($target, $text)
    [pscustomobject]@{ Source=$original; SHA256=(Get-FileHash -LiteralPath $original).Hash; TestCopy=$target; Purpose='Only native platform primitive bodies replaced in an output-directory copy' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $scratch 'seam-manifest.json') -Encoding UTF8
    return @($sources | Where-Object { $_ -ne $original }) + $target
}
function Run-Check([string]$Name, [string[]]$Sources, [string]$Defines, [string]$Main) {
    $output = Join-Path $outRoot $Name
    $null = New-Item -ItemType Directory -Path $output -Force
    $exe = Join-Path $output ($Name + '.exe')
    $rsp = Join-Path $output 'sources.rsp'
    $manifest = @($Sources | ForEach-Object { [pscustomobject]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'source-manifest.json') -Encoding UTF8
    [IO.File]::WriteAllLines($rsp, [string[]]@($Sources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
    & $compiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 ('-define:' + $Defines) ('-main:' + $Main) ('-out:' + $exe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@' + $rsp) *> (Join-Path $output 'compile.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'compile.log'); throw ('Compile failed: ' + $Name) }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe; $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $child = New-Object Diagnostics.Process
    $child.StartInfo = $info
    try {
        $null = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(45000)) { $child.Kill(); $child.WaitForExit(); throw ('Timeout: ' + $Name) }
        $child.WaitForExit()
        $result = $stdout.Result + $stderr.Result
        [IO.File]::WriteAllText((Join-Path $output 'result.txt'), $result)
        Write-Output $result
        if ($child.ExitCode -ne 0) { throw ('Failed: ' + $Name) }
    } finally { $child.Dispose() }
    $changed = @($manifest | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
    if ($changed.Count -ne 0) { throw ('Source changed during test: ' + $Name) }
}
if ($Mode -in @('All','Power')) {
    Run-Check 'Power' @((Join-Path $appRoot 'LaboratoryPower.cs'), (Join-Path $testRoot 'LaboratoryPowerChecks.cs')) 'PAVISE_LAB_POWER_CHECKS;PAVISE_LAB_POWER_ISOLATED' 'PaviseApp.LaboratoryPowerChecks'
}
if ($Mode -in @('All','CurrentIdle')) {
    Run-Check 'CurrentPlanIdle' @((Join-Path $appRoot 'LaboratoryPower.cs'), (Join-Path $testRoot 'LaboratoryPowerChecks.cs'), (Join-Path $fixtureRoot 'CurrentPlanIdleChecks.cs')) 'PAVISE_LAB_POWER_CHECKS;PAVISE_LAB_POWER_ISOLATED' 'PaviseApp.CurrentPlanIdleChecks'
}
if ($Mode -in @('All','Gpu')) {
    Run-Check 'Gpu' @($fullSources + (Join-Path $testRoot 'LaboratoryGpuChecks.cs')) 'PAVISE_LAB_GPU_CHECKS' 'PaviseApp.LaboratoryGpuChecks'
}
if ($Mode -in @('All','EppEligibility')) {
    Run-Check 'EppEligibility' @($fullSources + (Join-Path $fixtureRoot 'LaboratoryEppEligibilityChecks.cs')) 'PAVISE_SELFTEST' 'PaviseApp.LaboratoryEppEligibilityChecks'
}
if ($Mode -in @('All','Audio')) {
    Run-Check 'Audio' @((Join-Path $appRoot 'LaboratoryAudio.cs'), (Join-Path $appRoot 'LowLatencyAudioNative.cs'), (Join-Path $testRoot 'LaboratoryAudioChecks.cs')) 'PAVISE_LAB_AUDIO_CHECKS' 'PaviseApp.LaboratoryAudioChecks'
}
if ($Mode -in @('All','Integration')) {
    Run-Check 'Integration' @(@($fullSources | Where-Object { [IO.Path]::GetFileName($_) -ne 'Settings.cs' }) + (Join-Path $fixtureRoot 'MemorySettings.cs') + (Join-Path $testRoot 'LaboratoryIntegrationChecks.cs')) 'PAVISE_SELFTEST;PAVISE_LAB_INTEGRATION' 'PaviseApp.LaboratoryIntegrationChecks'
}
if ($Mode -in @('All','Lifecycle')) {
    Run-Check 'Lifecycle' @((Sources-WithPrimitives 'Lifecycle') + (Join-Path $fixtureRoot 'LifecycleChecks.cs')) 'PAVISE_SELFTEST' 'PaviseApp.OfficialLifecycleChecks'
}
if ($Mode -in @('All','Uwp')) {
    Run-Check 'Uwp' @((Sources-WithPrimitives 'Uwp') + (Join-Path $fixtureRoot 'UwpChecks.cs')) 'PAVISE_SELFTEST' 'PaviseApp.OfficialUwpChecks'
}
$productionChanged = @($productionManifest | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
if ($productionChanged.Count -ne 0) { throw 'Production sources changed during the verification run; retry against a settled source tree' }
Write-Output ('OUTPUT ' + $outRoot)
