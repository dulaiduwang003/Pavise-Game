param([string]$OutputDirectory, [switch]$CoreOnly, [string]$DependencySnapshot, [ValidateRange(1000,300000)][int]$TimeoutMilliseconds = 60000, [string]$SourceRoot, [switch]$OverlayCurrentInstaller, [switch]$CleanupProbe)
$ErrorActionPreference = 'Stop'
$installerWorkspace = Split-Path $PSScriptRoot -Parent
if ($SourceRoot -and $DependencySnapshot) { throw 'Choose a complete SourceRoot snapshot or a dependency snapshot, never both.' }
if ($OverlayCurrentInstaller -and -not $SourceRoot) { throw 'OverlayCurrentInstaller requires an explicit complete SourceRoot snapshot.' }
$installerSourceInput = if ($SourceRoot) { [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\','/') } else { Join-Path $installerWorkspace 'src' }
if (-not (Test-Path -LiteralPath (Join-Path $installerSourceInput 'PaviseApp\App.cs') -PathType Leaf)) { throw 'SourceRoot must contain the complete src tree, including PaviseApp\App.cs.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $installerWorkspace ('build\self-update-installer-checks-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$installerOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $installerOutput) { throw 'Use a new, isolated test output directory.' }
$null = New-Item -ItemType Directory -Path $installerOutput
$installerSourceRoot = Join-Path $installerOutput 'sources'
$null = New-Item -ItemType Directory -Path $installerSourceRoot
$installerFiles = @(Get-ChildItem -LiteralPath $installerSourceInput -Recurse -Filter '*.cs' -File | Sort-Object FullName)
$installerFrozen = @()
foreach ($installerFile in $installerFiles) {
    $installerRelative = $installerFile.FullName.Substring($installerSourceInput.Length + 1)
    $installerCopy = Join-Path $installerSourceRoot $installerRelative
    $null = New-Item -ItemType Directory -Path (Split-Path $installerCopy -Parent) -Force
    [IO.File]::Copy($installerFile.FullName, $installerCopy)
    $installerFrozen += $installerCopy
}
if ($SourceRoot) {
    $installerOverlayFiles = @()
    if ($OverlayCurrentInstaller) {
        foreach ($installerOverlayName in @('SelfUpdateInstaller.cs','SelfUpdateInstallerWindow.cs')) {
            $installerOverlaySource = Join-Path $installerWorkspace ('src\PaviseApp\' + $installerOverlayName)
            [IO.File]::Copy($installerOverlaySource, (Join-Path $installerSourceRoot ('PaviseApp\' + $installerOverlayName)), $true)
            $installerOverlayFiles += $installerOverlayName + ' SHA256=' + (Get-FileHash -LiteralPath $installerOverlaySource -Algorithm SHA256).Hash
        }
    }
    $installerSourceNote = 'TEST ONLY: complete source snapshot from ' + $installerSourceInput + '; current installer overlays=' + ($installerOverlayFiles -join ', ') + '; current test runner is copied separately; never publish these fixtures as product builds.'
    [IO.File]::WriteAllText((Join-Path $installerOutput 'source-snapshot.txt'), $installerSourceNote)
    Write-Output $installerSourceNote
}
if ($DependencySnapshot) {
    $installerDependency = [IO.Path]::GetFullPath($DependencySnapshot)
    if (-not (Test-Path -LiteralPath $installerDependency -PathType Leaf) -or [IO.Path]::GetFileName($installerDependency) -ne 'PowerPlan.cs') { throw 'DependencySnapshot must explicitly name a real PowerPlan.cs source snapshot.' }
    [IO.File]::Copy($installerDependency, (Join-Path $installerSourceRoot 'PaviseApp\PowerPlan.cs'), $true)
    $installerDependencyNote = 'TEST ONLY: mixed dependency snapshot; PowerPlan.cs comes from ' + $installerDependency + '; SHA256=' + (Get-FileHash -LiteralPath $installerDependency -Algorithm SHA256).Hash + '; do not publish this fixture as a product build.'
    [IO.File]::WriteAllText((Join-Path $installerOutput 'dependency-snapshot.txt'), $installerDependencyNote)
    Write-Output $installerDependencyNote
}
$installerTest = Join-Path $installerOutput 'SelfUpdateInstallerChecks.cs'
[IO.File]::Copy((Join-Path $installerWorkspace 'tests\SelfUpdateInstallerChecks.cs'), $installerTest)
$installerAppPath = Join-Path $installerSourceRoot 'PaviseApp\App.cs'
$installerApp = [IO.File]::ReadAllText($installerAppPath)
$installerVersionMatch = [regex]::Match($installerApp, 'public const string Version = "([0-9.]+)";')
if (-not $installerVersionMatch.Success) { throw 'The source version could not be read.' }
$installerVersion = [Version]$installerVersionMatch.Groups[1].Value
$installerNext = [Version]::new($installerVersion.Major, $installerVersion.Minor, $installerVersion.Build, $installerVersion.Revision + 1)
$installerCandidateApp = Join-Path $installerOutput 'CandidateApp.cs'
[IO.File]::WriteAllText($installerCandidateApp, $installerApp.Replace($installerVersionMatch.Value, ('public const string Version = "' + $installerNext + '";')), (New-Object Text.UTF8Encoding($false)))
# Only the frozen test build trusts an ephemeral key. The production public key
# and release private key are neither changed nor read by this test.
$installerRsa = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
$installerRsa.PersistKeyInCsp = $false
function Stop-OwnedInstallerFixtures([Diagnostics.Process]$RootProcess, [DateTime]$NotBeforeUtc) {
    $fixturePrefix = $installerOutput.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $fixtureStopped = New-Object 'Collections.Generic.List[object]'
    $fixtureErrors = New-Object 'Collections.Generic.List[string]'
    try {
        if ($RootProcess -and $RootProcess.StartInfo.FileName -eq $installerOld -and -not $RootProcess.HasExited) {
            $fixtureRootId = $RootProcess.Id
            $RootProcess.Kill()
            if (-not $RootProcess.WaitForExit(2500)) { throw 'The known fixture root did not exit after termination.' }
            $fixtureStopped.Add([ordered]@{ pid=$fixtureRootId; image=$installerOld; kind='known fixture root' })
        }
    } catch { $fixtureErrors.Add('Known fixture root: ' + $_.Exception.Message) }
    for ($fixturePass = 0; $fixturePass -lt 3; $fixturePass++) {
        $fixtureOwned = New-Object 'Collections.Generic.List[object]'
        foreach ($fixtureProcess in @(Get-Process)) {
            $fixtureKeepHandle = $false
            try {
                if ($fixtureProcess.HasExited) { continue }
                $fixtureImage = $fixtureProcess.MainModule.FileName
                if (-not $fixtureImage -or -not $fixtureImage.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
                $fixtureBirth = $fixtureProcess.StartTime.ToUniversalTime()
                if ($fixtureBirth -lt $NotBeforeUtc.AddSeconds(-1)) { continue }
                $null = $fixtureProcess.Handle
                if ($fixtureProcess.HasExited) { continue }
                $fixtureOwned.Add([pscustomobject]@{ Process=$fixtureProcess; Image=$fixtureImage; Birth=$fixtureBirth; Helper=([IO.Path]::GetFileName($fixtureImage) -eq 'Pavise.Update.exe') })
                $fixtureKeepHandle = $true
            } catch { }
            finally { if (-not $fixtureKeepHandle) { $fixtureProcess.Dispose() } }
        }
        if ($fixtureOwned.Count -eq 0) { break }
        # Stop the exact owned helper first so it cannot start a rollback child
        # while other fixture processes are being collected. Name alone never
        # selects a process: the absolute per-run path and birth were checked.
        foreach ($fixtureInfo in @($fixtureOwned | Sort-Object Helper -Descending)) {
            try {
                $fixtureProcess = $fixtureInfo.Process
                if (-not $fixtureProcess.HasExited -and $fixtureProcess.StartTime.ToUniversalTime() -eq $fixtureInfo.Birth -and [string]::Equals($fixtureProcess.MainModule.FileName, $fixtureInfo.Image, [StringComparison]::OrdinalIgnoreCase)) {
                    $fixtureId = $fixtureProcess.Id
                    $fixtureProcess.Kill()
                    if (-not $fixtureProcess.WaitForExit(2500)) { throw 'The exact owned fixture did not exit after termination.' }
                    $fixtureStopped.Add([ordered]@{ pid=$fixtureId; image=$fixtureInfo.Image; kind='exact per-run fixture image' })
                }
            } catch { $fixtureErrors.Add($fixtureInfo.Image + ': ' + $_.Exception.Message) }
            finally { $fixtureInfo.Process.Dispose() }
        }
        [Threading.Thread]::Sleep(100)
    }
    $fixtureReport = [ordered]@{ outputDirectory=$installerOutput; timeoutMilliseconds=$TimeoutMilliseconds; stopped=$fixtureStopped.ToArray(); errors=$fixtureErrors.ToArray(); scope='known root Process plus exact images inside this newly created output directory; no production process selected' }
    [IO.File]::WriteAllText((Join-Path $installerOutput 'fixture-cleanup.json'), ($fixtureReport | ConvertTo-Json -Depth 5))
    if ($fixtureErrors.Count -gt 0) { throw 'One or more owned fixture processes could not be cleaned. Inspect fixture-cleanup.json.' }
}
try {
    $installerManifestSource = Join-Path $installerSourceRoot 'PaviseApp\SelfUpdateManifest.cs'
    $installerManifestCode = [IO.File]::ReadAllText($installerManifestSource)
    $installerManifestCode = [regex]::Replace($installerManifestCode, 'internal const string TrustedPublicKeyXml = "[^"]+";', ('internal const string TrustedPublicKeyXml = "' + $installerRsa.ToXmlString($false) + '";'))
    [IO.File]::WriteAllText($installerManifestSource, $installerManifestCode, (New-Object Text.UTF8Encoding($false)))
    $installerCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $installerArguments = @('-nologo','-warn:0','-target:exe','-platform:x64','-unsafe+','-optimize+','-codepage:65001','-define:PAVISE_SELFTEST;PAVISE_UPDATE_INSTALLER_CHECKS;PAVISE_STABILITY_COMPARISON','-main:PaviseApp.SelfUpdateInstallerChecks','-reference:System.dll','-reference:System.Core.dll','-reference:System.Drawing.dll','-reference:System.Windows.Forms.dll','-reference:System.Management.dll','-reference:System.Xml.dll','-reference:System.Security.dll')
    $installerOld = Join-Path $installerOutput 'Pavise.installer-checks.exe'
    $installerResponse = Join-Path $installerOutput 'old.rsp'
    [IO.File]::WriteAllLines($installerResponse, [string[]]@(($installerFrozen + $installerTest | ForEach-Object { '"' + $_ + '"' })), (New-Object Text.UTF8Encoding($true)))
    & $installerCompiler @installerArguments ('-out:' + $installerOld) ('@' + $installerResponse)
    if ($LASTEXITCODE -ne 0) { throw 'The old-version fixture did not compile.' }
    $installerCandidate = Join-Path $installerOutput 'Pavise.candidate-fixture.exe'
    $installerCandidateFiles = @($installerFrozen | Where-Object { $_ -ne $installerAppPath }) + @($installerCandidateApp, $installerTest)
    $installerCandidateResponse = Join-Path $installerOutput 'candidate.rsp'
    [IO.File]::WriteAllLines($installerCandidateResponse, [string[]]@($installerCandidateFiles | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
    & $installerCompiler @installerArguments ('-out:' + $installerCandidate) ('@' + $installerCandidateResponse)
    if ($LASTEXITCODE -ne 0) { throw 'The new-version fixture did not compile.' }
    $installerHash = (Get-FileHash -LiteralPath $installerCandidate -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerPayload = [ordered]@{ schema=1; version=$installerNext.ToString(4); size=(Get-Item -LiteralPath $installerCandidate).Length; sha256=$installerHash; minUpdater=1; expiresUtc=[DateTime]::UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); urls=@('https://pavise.club/updates/' + $installerNext.ToString(4) + '/Pavise.exe') }
    $installerPayloadBytes = [Text.Encoding]::UTF8.GetBytes(($installerPayload | ConvertTo-Json -Compress))
    $installerSignature = $installerRsa.SignData($installerPayloadBytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $installerEnvelope = [ordered]@{ payload=[Convert]::ToBase64String($installerPayloadBytes); signature=[Convert]::ToBase64String($installerSignature) }
    $installerFeed = Join-Path $installerOutput 'fixture-manifest.json'
    [IO.File]::WriteAllText($installerFeed, ($installerEnvelope | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
    $installerStart = New-Object Diagnostics.ProcessStartInfo
    $installerStart.FileName = $installerOld
    $installerStart.Arguments = '"' + $installerOutput + '"'
    if ($CleanupProbe) { $installerStart.Arguments = '--fixture-cleanup-probe "' + $installerOutput + '"' }
    elseif (-not $CoreOnly) { $installerStart.Arguments += ' "' + $installerCandidate + '" "' + $installerFeed + '"' }
    $installerStart.UseShellExecute = $false; $installerStart.CreateNoWindow = $true; $installerStart.RedirectStandardOutput = $true; $installerStart.RedirectStandardError = $true
    $installerProcess = New-Object Diagnostics.Process; $installerProcess.StartInfo = $installerStart
    $installerStartedUtc = [DateTime]::UtcNow
    try {
        $null = $installerProcess.Start(); $installerStdout = $installerProcess.StandardOutput.ReadToEndAsync(); $installerStderr = $installerProcess.StandardError.ReadToEndAsync()
        if (-not $installerProcess.WaitForExit($TimeoutMilliseconds)) { throw 'The isolated installer checks timed out; owned fixture processes will be cleaned, with no production process involved.' }
        $installerProcess.WaitForExit(); $installerResult = $installerStdout.Result + $installerStderr.Result
        [IO.File]::WriteAllText((Join-Path $installerOutput 'result.txt'), $installerResult)
        Write-Output $installerResult; Write-Output ('OUTPUT ' + $installerOutput)
        if ($installerProcess.ExitCode -ne 0) { throw 'Installer checks failed; inspect result.txt.' }
    } finally { try { Stop-OwnedInstallerFixtures $installerProcess $installerStartedUtc } finally { $installerProcess.Dispose() } }
} finally { $installerRsa.Dispose() }
