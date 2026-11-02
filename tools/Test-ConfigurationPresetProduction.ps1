param([string]$OutputDirectory,[switch]$MissingAudioRestoreNegative)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build\ConfigurationPresetProduction-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$frozen = Join-Path $output 'frozen'
if (Test-Path -LiteralPath $frozen) { throw 'Choose a fresh output directory; prior evidence is retained' }
New-Item -ItemType Directory -Path $frozen -Force | Out-Null
$manifest = @()
foreach ($name in @('ConfigurationPresetCatalog.cs','ConfigurationPresetCoordinator.cs','ConfigurationPresetBackend.cs','ConfigurationPresetRuntime.cs','GameMode.ConfigurationPresets.cs','GameMode.ManualLaboratory.cs')) {
    $original = Join-Path $workspace ('src\PaviseApp\' + $name)
    $target = Join-Path $frozen $name
    Copy-Item -LiteralPath $original -Destination $target
    $manifest += [pscustomobject]@{ Name=$name; Original=$original; SHA256=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash }
}
function Set-HelperBody([string]$Text,[string]$Signature,[string]$Body) {
    if ([regex]::Matches($Text,[regex]::Escape($Signature)).Count -ne 1) { throw ('Native helper must match once: ' + $Signature) }
    $start = $Text.IndexOf($Signature,[StringComparison]::Ordinal)
    $open = $Text.IndexOf('{',$start); $depth=0; $close=-1
    for ($i=$open; $i -lt $Text.Length; $i++) { if ($Text[$i] -eq '{') { $depth++ } elseif ($Text[$i] -eq '}') { $depth--; if ($depth -eq 0) { $close=$i; break } } }
    if ($close -lt 0) { throw ('Unbalanced native helper: ' + $Signature) }
    $Text.Substring(0,$open+1) + $Body + $Text.Substring($close)
}
# Only read-only discovery leaves are intercepted in the copied adapter.
# Policy decisions, dispatch, waits, group rollback and the GameMode preset method remain production code.
$backendPath = Join-Path $frozen 'ConfigurationPresetBackend.cs'
$backend = [IO.File]::ReadAllText($backendPath)
$backend = Set-HelperBody $backend 'private static bool ServiceExists(string name)' ' return PresetProductionProbe.Services; '
$backend = Set-HelperBody $backend 'private static bool NtfsAvailable()' ' return PresetProductionProbe.Ntfs; '
$backend = Set-HelperBody $backend 'private static bool RegistryAvailable(string path)' ' return PresetProductionProbe.Registry; '
[IO.File]::WriteAllText($backendPath,$backend,[Text.UTF8Encoding]::new($false))
if ($MissingAudioRestoreNegative) {
    $gamePath = Join-Path $frozen 'GameMode.ConfigurationPresets.cs'
    $game = [IO.File]::ReadAllText($gamePath)
    $line = 'if (key == LaboratoryAudio.Key && !LaboratoryAudio.Restore()) return false;'
    if ([regex]::Matches($game,[regex]::Escape($line)).Count -ne 1) { throw 'Missing audio restore control must match once' }
    [IO.File]::WriteAllText($gamePath,$game.Replace($line,'// Negative control: audio restore deliberately removed.'),[Text.UTF8Encoding]::new($false))
}
$fixture = Join-Path $output 'ConfigurationPresetProductionChecks.cs'
Copy-Item -LiteralPath (Join-Path $workspace 'tests\ConfigurationPresetProductionChecks.cs') -Destination $fixture
$exe = Join-Path $output 'ConfigurationPresetProductionChecks.exe'
$rsp = Join-Path $output 'sources.rsp'
$arguments = @('/nologo','/target:exe','/platform:x64','/optimize+','/codepage:65001','/nowarn:0649,0414','/main:PaviseApp.ConfigurationPresetProductionChecks',('/out:"'+$exe+'"'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Security.dll')
$arguments += @(Get-ChildItem -LiteralPath $frozen -Filter '*.cs' -File | ForEach-Object { '"' + $_.FullName + '"' })
$arguments += '"' + $fixture + '"'
[IO.File]::WriteAllLines($rsp,$arguments,[Text.UTF8Encoding]::new($false))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$compile = (& $compiler ('@' + $rsp) 2>&1 | Out-String)
[IO.File]::WriteAllText((Join-Path $output 'compile.log'),$compile)
if ($LASTEXITCODE -ne 0) { throw ('Production adapter fixture compilation failed: ' + $compile) }
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $exe; $info.WorkingDirectory = $output; $info.UseShellExecute = $false
$info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
$child = [Diagnostics.Process]::new(); $child.StartInfo = $info
try {
    $child.Start() | Out-Null
    $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(30000)) { throw 'Production adapter fixture timed out' }
    $result = $stdout.Result + $stderr.Result
    [IO.File]::WriteAllText((Join-Path $output 'result.txt'),$result)
    $result
    if ($MissingAudioRestoreNegative) {
        if ($child.ExitCode -eq 0 -or -not $result.Contains('Low-buffer audio OFF failed to drain its previous session')) { throw 'Missing audio restore negative control was not rejected' }
    } elseif ($child.ExitCode -ne 0 -or -not $result.Contains('PASS production preset adapter assertions=')) { throw 'Production adapter fixture failed' }
    $changed = @($manifest | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256 })
    [ordered]@{ ProductionSources=$manifest; ChangedSources=@($changed | ForEach-Object Name); MissingAudioRestoreNegative=[bool]$MissingAudioRestoreNegative; ResultExit=$child.ExitCode; NativeWrites=$false; ProductWorkers=$false; DiscoveryHelpers='ServiceExists, NtfsAvailable, RegistryAvailable'; Services='Fake Settings, Native, performance transactions, Privacy, NVIDIA, Laboratory'; ActualCode='Production preset catalog/coordinator/runtime/backend and GameMode preset dispatcher/manual gate' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
    if ($changed.Count -gt 0) { throw 'Production adapter sources changed during the fixture; rerun against the frozen final source' }
} finally { $child.Dispose() }
