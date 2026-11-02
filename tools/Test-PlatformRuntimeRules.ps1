param([ValidateRange(60, 300)][int]$TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$platformRulesWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$platformRulesOutput = Join-Path ([IO.Path]::GetTempPath()) ('PavisePlatformRuntimeRules-' + [Guid]::NewGuid().ToString('N'))
$platformRulesFrozen = Join-Path $platformRulesOutput 'frozen'
$null = New-Item -ItemType Directory -Path $platformRulesFrozen
$platformRulesManifest = New-Object 'Collections.Generic.List[object]'
foreach ($platformRulesOriginal in @(& rg --files (Join-Path $platformRulesWorkspace 'src') -g '*.cs')) {
    $platformRulesRelative = $platformRulesOriginal.Substring($platformRulesWorkspace.Length + 1)
    $platformRulesCopy = Join-Path $platformRulesFrozen $platformRulesRelative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($platformRulesCopy)) -Force
    [IO.File]::WriteAllBytes($platformRulesCopy, [IO.File]::ReadAllBytes($platformRulesOriginal))
    $platformRulesManifest.Add([pscustomobject]@{Path=$platformRulesRelative;Original=$platformRulesOriginal;SHA256=(Get-FileHash -LiteralPath $platformRulesCopy -Algorithm SHA256).Hash})
}
function Find-ClosingBrace([string]$Text, [int]$Start) {
    $depth=0; $state='code'
    for ($i=$Start; $i -lt $Text.Length; $i++) {
        $c=$Text[$i]; $next=if($i+1 -lt $Text.Length){$Text[$i+1]}else{[char]0}
        if($state -eq 'line'){if($c -eq "`n"){$state='code'};continue}
        if($state -eq 'block'){if($c -eq '*' -and $next -eq '/'){$state='code';$i++};continue}
        if($state -eq 'verbatim'){if($c -eq '"'){if($next -eq '"'){$i++}else{$state='code'}};continue}
        if($state -eq 'string' -or $state -eq 'char'){if($c -eq '\'){$i++}elseif(($state -eq 'string' -and $c -eq '"') -or ($state -eq 'char' -and $c -eq "'")){$state='code'};continue}
        if($c -eq '/' -and $next -eq '/'){$state='line';$i++;continue}
        if($c -eq '/' -and $next -eq '*'){$state='block';$i++;continue}
        if($c -eq '@' -and $next -eq '"'){$state='verbatim';$i++;continue}
        if($c -eq '"'){$state='string';continue}
        if($c -eq "'"){$state='char';continue}
        if($c -eq '{'){$depth++}elseif($c -eq '}'){$depth--;if($depth -eq 0){return $i}}
    }
    throw 'Unbalanced fixture source braces'
}
function Set-FixtureBody([string]$Text, [string]$Signature, [string]$Body) {
    if(([regex]::Matches($Text,[regex]::Escape($Signature))).Count -ne 1){throw "Fixture seam must match exactly once: $Signature"}
    $start=$Text.IndexOf($Signature,[StringComparison]::Ordinal); $open=$Text.IndexOf('{',$start); $close=Find-ClosingBrace $Text $open
    return $Text.Substring(0,$open+1)+"`n"+$Body+"`n"+$Text.Substring($close)
}
function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path,$Text,(New-Object Text.UTF8Encoding($false))) }
# Only environment/mutation boundaries in the frozen copy change. Policy,
# classification, caches, whitelist evaluation, serialization and reset are real.
$platformRulesGamePath=Join-Path $platformRulesFrozen 'src/PaviseApp/GameMode.cs'
$platformRulesGame=[IO.File]::ReadAllText($platformRulesGamePath)
$platformRulesGame=Set-FixtureBody $platformRulesGame 'private int ReleaseCurrentWhitelistMatches(out int matched)' 'matched=0; return 0;'
$platformRulesGame=Set-FixtureBody $platformRulesGame 'private void RequestPolicyApply([CallerMemberName] string caller = null)' '// No product workers or native policy application in a pure whitelist fixture.'
Write-Utf8 $platformRulesGamePath $platformRulesGame
$platformRulesCapturePath=Join-Path $platformRulesFrozen 'src/PaviseApp/ProcessSnapshotSource.cs'
$platformRulesCapture=[IO.File]::ReadAllText($platformRulesCapturePath)
$platformRulesCapture=Set-FixtureBody $platformRulesCapture 'public static ProcessSnapshot Capture(int pathSession, int maxAgeMs)' 'throw new InvalidOperationException("Only supplied synthetic process snapshots are allowed");'
Write-Utf8 $platformRulesCapturePath $platformRulesCapture
$platformRulesSettingsPath=Join-Path $platformRulesFrozen 'src/PaviseApp/Settings.cs'
$platformRulesSettings=[IO.File]::ReadAllText($platformRulesSettingsPath)
foreach($signature in @('private static RegistryKey ProfiledOpen()','private static RegistryKey ProfiledCreate()','private static RegistryKey ProfiledOpenWritable()')) {
    $platformRulesSettings=Set-FixtureBody $platformRulesSettings $signature 'throw new InvalidOperationException("The platform fixture must use transient settings, never the actual user registry");'
}
Write-Utf8 $platformRulesSettingsPath $platformRulesSettings
$platformRulesPanel=[IO.File]::ReadAllText((Join-Path $platformRulesFrozen 'src/PaviseApp/PanelForm.cs'))
if($platformRulesPanel -notmatch 'btnRestorePlatformProtection\.Enabled\s*=\s*PlatformRuntimePolicy\.Mask\s*!=\s*0\s*;') {
    throw 'Update the restore-button state contract test after changing its UI wiring.'
}
$platformRulesRunner=Join-Path $platformRulesWorkspace 'tests/PlatformRuntimeRulesRunner.cs'
$platformRulesFixture=Join-Path $platformRulesOutput 'PlatformRuntimeRulesRunner.cs'
[IO.File]::WriteAllBytes($platformRulesFixture,[IO.File]::ReadAllBytes($platformRulesRunner))
$platformRulesManifest.Add([pscustomobject]@{Path='tests/PlatformRuntimeRulesRunner.cs';Original=$platformRulesRunner;SHA256=(Get-FileHash -LiteralPath $platformRulesFixture -Algorithm SHA256).Hash})
$platformRulesManifest | Export-Csv -LiteralPath (Join-Path $platformRulesOutput 'source-manifest.tsv') -Delimiter ([char]9) -NoTypeInformation
Write-Utf8 (Join-Path $platformRulesOutput 'test-only-seams.txt') 'Frozen copy only: GameMode.ReleaseCurrentWhitelistMatches returns no matches; RequestPolicyApply is inactive; ProcessSnapshotSource.Capture throws; real settings registry APIs throw. Production policy/classification/evaluation/manual serialization/reset/Settings transient store remain unchanged. No UI construction, product worker startup, native pressure fixture or app runtime launch. Restore-button mask wiring is a source contract; this suite does not verify rendering.'
$platformRulesSources = @(Get-ChildItem -LiteralPath $platformRulesFrozen -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$platformRulesSources += $platformRulesFixture
$platformRulesCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$platformRulesExe = Join-Path $platformRulesOutput 'PlatformRuntimeRules.exe'
$platformRulesResponse = Join-Path $platformRulesOutput 'sources.rsp'
[IO.File]::WriteAllLines($platformRulesResponse, [string[]]@($platformRulesSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
& $platformRulesCompiler /nologo /nowarn:0649,0414 /target:exe /platform:x64 /unsafe+ /optimize+ /codepage:65001 '/define:PAVISE_SELFTEST;PAVISE_PLATFORM_RULES' /main:PaviseApp.PlatformRuntimeRulesRunner ('/out:' + $platformRulesExe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll /reference:System.Security.dll ('@' + $platformRulesResponse) *> (Join-Path $platformRulesOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $platformRulesOutput 'compile.log'); throw ('Platform runtime rules compilation failed: ' + $platformRulesOutput) }
$platformRulesStart = New-Object Diagnostics.ProcessStartInfo
$platformRulesStart.FileName = $platformRulesExe
$platformRulesStart.Arguments = '"' + $platformRulesOutput + '"'
$platformRulesStart.WorkingDirectory = $platformRulesOutput
$platformRulesStart.UseShellExecute = $false
$platformRulesStart.CreateNoWindow = $true
$platformRulesStart.WindowStyle = 'Hidden'
$platformRulesStart.RedirectStandardOutput = $true
$platformRulesStart.RedirectStandardError = $true
$platformRulesProcess = New-Object Diagnostics.Process
$platformRulesProcess.StartInfo = $platformRulesStart
try {
    $null = $platformRulesProcess.Start()
    $platformRulesStdout = $platformRulesProcess.StandardOutput.ReadToEndAsync()
    $platformRulesStderr = $platformRulesProcess.StandardError.ReadToEndAsync()
    $platformRulesClock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $platformRulesProcess.WaitForExit(500)) {
        if ($platformRulesClock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $platformRulesProcess.Kill(); throw ('Owned platform runtime rules test exceeded ' + $TimeoutSeconds + ' seconds.') }
    }
    $platformRulesProcess.WaitForExit()
    $platformRulesResult = $platformRulesStdout.Result + $platformRulesStderr.Result
    Write-Utf8 (Join-Path $platformRulesOutput 'result.txt') $platformRulesResult
    Write-Output $platformRulesResult
    $platformRulesChanged=@($platformRulesManifest | Where-Object {(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256})
    [pscustomobject]@{FrozenSources=$platformRulesManifest.Count;WorkspaceMatchesSnapshot=($platformRulesChanged.Count -eq 0);ChangedSources=@($platformRulesChanged | ForEach-Object {$_.Path});RunExit=$platformRulesProcess.ExitCode;UiWindows=0;ProductWorkersStarted=$false;NativeProcessMutations=$false;RestoreButtonState='source contract and actual mask cases; no UI rendering'} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $platformRulesOutput 'verification.json') -Encoding UTF8
    if ($platformRulesProcess.ExitCode -ne 0 -or $platformRulesStderr.Result.Length -ne 0 -or $platformRulesStdout.Result -notmatch 'PASS platform runtime rules') { throw 'Platform runtime rules checks failed.' }
    if ($platformRulesChanged.Count -ne 0) { throw 'Sources changed while the frozen platform fixture ran; rerun against the final source.' }
} finally { $platformRulesProcess.Dispose(); Write-Output ('OUTPUT ' + $platformRulesOutput) }
