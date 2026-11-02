param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$rtssTestWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$rtssTestScript = Join-Path $PSScriptRoot 'Uninstall-Rtss.ps1'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $env:TEMP ('Pavise-RtssUninstallChecks-' + [Guid]::NewGuid().ToString('N'))
}
$rtssTestOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $rtssTestOutput) { throw 'Use a new output directory for isolated RTSS uninstall checks' }
$null = New-Item -ItemType Directory -Path $rtssTestOutput
$script:rtssTestChecks = 0
$script:rtssTestLog = New-Object 'Collections.Generic.List[string]'
function Assert-RtssCheck([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:rtssTestChecks++
    $script:rtssTestLog.Add('PASS ' + $Message)
}
function Assert-RtssThrows([scriptblock]$Action, [string]$Message) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-RtssCheck $threw $Message
}
function Get-RtssFixtureSha256([string]$Path) {
    $inputFile = [IO.File]::OpenRead($Path); $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($inputFile)).Replace('-', '') }
    finally { $hash.Dispose(); $inputFile.Dispose() }
}

# These guards stay installed throughout the checks. Environmental operations are
# mocked at the script's RTSS-specific hooks, so a missing mock fails closed.
function Get-Process { throw 'Fixture cannot inspect real processes' }
function Start-Process { throw 'Fixture cannot launch real processes' }
function Stop-Process { throw 'Fixture cannot stop real processes' }
function Get-CimInstance { throw 'Fixture cannot inspect real CIM state' }
function Get-ItemProperty { throw 'Fixture cannot inspect real registry state' }
function Add-Type { throw 'Fixture cannot load the native uninstall bridge' }

function New-RtssTestRegistration([string]$Directory) {
    return [pscustomobject]@{
        KeyName = 'RTSS'; DisplayName = 'RivaTuner Statistics Server 7.3.7'; Publisher = 'Unwinder'
        InstallLocation = $Directory; UninstallString = '"' + [IO.Path]::Combine($Directory, 'uninstall.exe') + '"'
        DisplayVersion = '7.3.7'
    }
}
function New-RtssTestInstallation([string]$Name) {
    $directory = [IO.Path]::Combine($rtssTestOutput, $Name)
    [void][IO.Directory]::CreateDirectory($directory)
    $main = [IO.Path]::Combine($directory, 'RTSS.exe')
    $uninstaller = [IO.Path]::Combine($directory, 'uninstall.exe')
    [IO.File]::WriteAllBytes($main, [Text.Encoding]::ASCII.GetBytes('isolated RTSS fixture; never executable'))
    $bytes = New-Object byte[] 1024
    $bytes[0] = 0x4D; $bytes[1] = 0x5A; $bytes[0x3C] = 0x80
    $bytes[0x80] = 0x50; $bytes[0x81] = 0x45
    $magic = [byte[]]@(0xEF,0xBE,0xAD,0xDE,0x4E,0x75,0x6C,0x6C,0x73,0x6F,0x66,0x74,0x49,0x6E,0x73,0x74)
    [Array]::Copy($magic, 0, $bytes, 256, $magic.Length)
    [IO.File]::WriteAllBytes($uninstaller, $bytes)
    return New-RtssTestRegistration $directory
}
function Reset-RtssTestEnvironment([string]$Name) {
    $script:rtssTestRegistrations = @(New-RtssTestInstallation $Name)
    $script:rtssTestAllowed = @($script:rtssTestRegistrations[0].InstallLocation)
    $script:rtssTestEvents = New-Object 'Collections.Generic.List[string]'
    $script:rtssTestAdmin = $true; $script:rtssTestPending = $false
    $script:rtssTestClose = $true; $script:rtssTestRunning = $false
    $script:rtssTestMutateOnClose = $false; $script:rtssTestDeleteFiles = $false
    $script:rtssTestResult = [pscustomobject]@{ Pending = $false; Error = ''; ExitCode = 0; RestartRequired = $false }
    $script:rtssTestLastStart = $null
    $ownedExecutable = [IO.Path]::Combine($script:rtssTestRegistrations[0].InstallLocation, 'RTSS.exe')
    $script:rtssTestOwnership = $ownedExecutable + "`n" + (Get-RtssFileIdentity $ownedExecutable)
    $script:rtssTestLoseOwnershipOnClose = $false
}
function Assert-RtssFixturePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($rtssTestOutput.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture refused an operation outside its isolated output directory'
    }
}

$rtssTestOriginalHash = Get-RtssFixtureSha256 $rtssTestScript
try {
    $tokens = $null; $parseErrors = $null
    $rtssTestAst = [Management.Automation.Language.Parser]::ParseFile($rtssTestScript, [ref]$tokens, [ref]$parseErrors)
    Assert-RtssCheck ($parseErrors.Count -eq 0) 'standalone uninstall script parses'
    $nativeSources = @($rtssTestAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value.Contains('namespace PaviseRtssManual')
    }, $true))
    Assert-RtssCheck ($nativeSources.Count -eq 1) 'exactly one embedded native runner source is available for compile-only validation'
    $nativeSource = $nativeSources[0].Value
    $nativeSourcePath = [IO.Path]::Combine($rtssTestOutput, 'NativeRunner.cs')
    $nativeLibraryPath = [IO.Path]::Combine($rtssTestOutput, 'NativeRunner.compile-only.dll')
    [IO.File]::WriteAllText($nativeSourcePath, $nativeSource, (New-Object Text.UTF8Encoding($false)))
    $compilerStart = [Diagnostics.ProcessStartInfo]::new()
    $compilerStart.FileName = [IO.Path]::Combine($env:WINDIR, 'Microsoft.NET\Framework64\v4.0.30319\csc.exe')
    $compilerStart.Arguments = '/nologo /target:library /platform:x64 /optimize+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /out:"' + $nativeLibraryPath + '" "' + $nativeSourcePath + '"'
    $compilerStart.UseShellExecute = $false; $compilerStart.CreateNoWindow = $true; $compilerStart.WindowStyle = 'Hidden'
    $compilerStart.RedirectStandardOutput = $true; $compilerStart.RedirectStandardError = $true
    $compiler = [Diagnostics.Process]::Start($compilerStart)
    try {
        $compilerOut = $compiler.StandardOutput.ReadToEndAsync(); $compilerError = $compiler.StandardError.ReadToEndAsync()
        if (-not $compiler.WaitForExit(60000)) { $compiler.Kill(); $compiler.WaitForExit(); throw 'Compile-only native runner validation timed out' }
        $compiler.WaitForExit()
        [IO.File]::WriteAllText([IO.Path]::Combine($rtssTestOutput, 'native-compile.log'), $compilerOut.Result + $compilerError.Result)
        Assert-RtssCheck ($compiler.ExitCode -eq 0 -and [IO.File]::Exists($nativeLibraryPath)) 'embedded C# runner compiles to a temporary library without loading or invocation'
    } finally { $compiler.Dispose() }
    Assert-RtssCheck ($nativeSource -match 'desktop\s*=\s*CreateSetupDesktop\(' -and
        $nativeSource -match 'if\s*\(desktop\s*==\s*IntPtr.Zero\)\s*throw' -and
        $nativeSource -match 'process\s*=\s*StartHiddenSetup\(start,\s*name\)') 'native runner fails closed when private desktop creation fails'
    Assert-RtssCheck ($nativeSource -match 'Desktop\s*=\s*desktopName' -and
        $nativeSource -match 'Flags\s*=\s*1,\s*ShowWindow\s*=\s*0' -and
        $nativeSource -match 'HiddenSetupCreationFlags\s*=\s*0x08000000') 'native startup information selects a private desktop and hides setup windows'
    Assert-RtssCheck ($nativeSource -notmatch 'TerminateProcess|Process\.Start\(|SwitchDesktop|ExitWindowsEx|InitiateSystemShutdown') 'native runner has no shell fallback, forced termination, desktop switching, or reboot API'
    . $rtssTestScript
    Assert-RtssCheck $true 'dot-sourcing performs no process, registry, native bridge, or uninstall actions'
    function Get-PaviseRtssOwnershipReceipt { return $script:rtssTestOwnership }
    function Clear-PaviseRtssOwnershipReceipt([string]$Expected) {
        if ($script:rtssTestOwnership -ceq $Expected) {
            $script:rtssTestOwnership = ''
            $script:rtssTestEvents.Add('clear-receipt')
        }
    }

    $rtssTestProductSources = @(& rg --files (Join-Path $rtssTestWorkspace 'src') -g '*.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate product sources' }
    $rtssTestStartupReferences = @($rtssTestProductSources | Select-String -SimpleMatch -Pattern 'Uninstall-Rtss.ps1', 'RetiredRtssCleanup')
    Assert-RtssCheck ($rtssTestStartupReferences.Count -eq 0) 'product has no broad or unguarded legacy uninstall entry point'
    $cleanupSource = [IO.File]::ReadAllText((Join-Path $rtssTestWorkspace 'src/PaviseApp/LegacyMonitoringCleanup.cs'))
    Assert-RtssCheck ($cleanupSource.Contains('Settings.LoadStr(OwnershipKey, "")') -and
        $cleanupSource.Contains('Pavise.LegacyCleanup.RtssUninstall.ps1')) 'upgrade cleanup requires a saved ownership receipt and its embedded uninstall-only helper'

    foreach ($path in @('RTSS.exe', '.\RTSS.exe', '\\server\RTSS\RTSS.exe', 'C:\RTSS.exe',
        'C:\', 'C:\Tools\other.exe', "C:\Tools\RTSS.exe`nignored", 'C:\Tools\"RTSS.exe')) {
        Assert-RtssCheck ((ConvertTo-RtssExecutablePath $path) -eq '') ('unsafe installation path rejected: ' + $path.Replace("`n", '<newline>'))
    }
    Assert-RtssCheck ((ConvertTo-RtssExecutablePath 'C:\Tools\RTSS') -ceq 'C:\Tools\RTSS\RTSS.exe') 'installation directory normalizes to RTSS.exe'
    Assert-RtssCheck ((ConvertTo-RtssExecutablePath '"C:\Program Files (x86)\RTSS\RTSS.exe"') -ceq 'C:\Program Files (x86)\RTSS\RTSS.exe') 'quoted local installation path normalizes'

    $validRegistration = New-RtssTestRegistration 'C:\Program Files (x86)\RTSS'
    Assert-RtssCheck ((Get-RtssRegisteredUninstaller $validRegistration).Uninstaller -ceq 'C:\Program Files (x86)\RTSS\uninstall.exe') 'official RTSS registration resolves its sibling uninstaller'
    foreach ($entry in @(
        @('KeyName', 'Other'), @('Publisher', 'Other'), @('DisplayName', 'RTSS Helper'),
        @('DisplayVersion', 'not-a-version'), @('InstallLocation', 'C:\Other\RTSS'),
        @('UninstallString', '"C:\Other\remove.exe" /S'),
        @('UninstallString', '"\\server\RTSS\uninstall.exe" /S'),
        @('UninstallString', '"C:\uninstall.exe" /S'),
        @('UninstallString', 'cmd.exe /c "C:\Program Files (x86)\RTSS\uninstall.exe"')
    )) {
        $invalid = New-RtssTestRegistration 'C:\Program Files (x86)\RTSS'
        $invalid.($entry[0]) = $entry[1]
        Assert-RtssCheck ($null -eq (Get-RtssRegisteredUninstaller $invalid)) ('mismatched registration rejected: ' + $entry[0] + '=' + $entry[1])
    }
    $start = New-RtssUninstallerStartInfo -Copy 'C:\Fixture\PaviseRtssUninstall.exe' -InstallationDirectory 'C:\Program Files (x86)\RTSS'
    Assert-RtssCheck ($start.Arguments -ceq '/S _?=C:\Program Files (x86)\RTSS') 'silent NSIS switch and final unquoted installation path are exact'
    Assert-RtssCheck (-not $start.UseShellExecute -and $start.CreateNoWindow -and -not $start.ErrorDialog -and $start.WindowStyle -eq 'Hidden') 'launch plan suppresses shell, console, error dialogs, and visible windows'
    Assert-RtssCheck ($start.WorkingDirectory -ceq 'C:\Program Files (x86)\RTSS') 'uninstaller working directory matches selected installation'
    Assert-RtssThrows { New-RtssUninstallerStartInfo -Copy 'relative.exe' -InstallationDirectory 'C:\Tools\RTSS' } 'relative copied-uninstaller path rejected'
    Assert-RtssThrows { New-RtssUninstallerStartInfo -Copy 'C:\Fixture\uninstall.exe' -InstallationDirectory 'C:\' } 'drive-root uninstall destination rejected'

    Reset-RtssTestEnvironment 'file-validation'
    $fixtureUninstaller = [IO.Path]::Combine($script:rtssTestRegistrations[0].InstallLocation, 'uninstall.exe')
    Assert-RtssCheck (Test-RtssUninstallerFile $fixtureUninstaller) 'isolated PE and NSIS signature fixture validates without execution'
    $identity = Get-RtssFileIdentity $fixtureUninstaller
    $expectedHash = Get-RtssFixtureSha256 $fixtureUninstaller
    Assert-RtssCheck ($identity -cmatch ('^[0-9]+\n' + $expectedHash + '$')) 'file identity records creation time and exact SHA256'
    $damaged = [IO.File]::ReadAllBytes($fixtureUninstaller); $damaged[0] = 0
    [IO.File]::WriteAllBytes($fixtureUninstaller, $damaged)
    Assert-RtssCheck (-not (Test-RtssUninstallerFile $fixtureUninstaller)) 'non-PE uninstaller rejected'
    Assert-RtssCheck ((Get-RtssFileIdentity $fixtureUninstaller) -cne $identity) 'changed file content changes uninstall identity'
    Assert-RtssCheck (-not (Test-RtssProductFile ([IO.Path]::Combine($script:rtssTestRegistrations[0].InstallLocation, 'RTSS.exe')))) 'unidentified executable is not treated as the RTSS product'
    Assert-RtssCheck (-not (Test-RtssMissingFile $fixtureUninstaller)) 'existing uninstaller is not mistaken for confirmed removal'
    Assert-RtssCheck (Test-RtssMissingFile ([IO.Path]::Combine($rtssTestOutput, 'missing', 'RTSS.exe'))) 'verified missing file and parent directory count as absent'
    Assert-RtssThrows { Test-RtssMissingFile '' } 'invalid filesystem state does not count as confirmed removal'

    # All discovery/process/launch hooks below are in-memory fixtures. Files can
    # only be copied/deleted inside this invocation's newly created directory.
    function Get-RtssUninstallRegistrations { return $script:rtssTestRegistrations }
    function Test-RtssProductFile([string]$Path) {
        Assert-RtssFixturePath $Path
        return [IO.File]::Exists($Path) -and $script:rtssTestAllowed -contains [IO.Path]::GetDirectoryName($Path)
    }
    function Get-RtssActiveProcesses([string]$Executable) {
        Assert-RtssFixturePath $Executable
        if ($script:rtssTestRunning) { [pscustomobject]@{ ProcessId = 987654; Path = $Executable } }
    }
    function Test-RtssAdministrator { return $script:rtssTestAdmin }
    function Test-RtssPendingOperation { return $script:rtssTestPending }
    function Initialize-RtssNativeRunner { $script:rtssTestEvents.Add('initialize') }
    function Close-RtssSelectedProcess([string]$Executable) {
        Assert-RtssFixturePath $Executable; $script:rtssTestEvents.Add('close')
        if ($script:rtssTestMutateOnClose) { [IO.File]::WriteAllText($Executable, 'changed-after-close') }
        if ($script:rtssTestLoseOwnershipOnClose) { $script:rtssTestOwnership = '' }
        if ($script:rtssTestClose) { $script:rtssTestRunning = $false }
        return $script:rtssTestClose
    }
    function Run-RtssPrivateUninstaller([Diagnostics.ProcessStartInfo]$Start) {
        Assert-RtssFixturePath $Start.FileName; $script:rtssTestEvents.Add('run')
        $script:rtssTestLastStart = $Start
        $copyWritable = $false
        try {
            $writeProbe = [IO.File]::Open($Start.FileName, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
            try { $copyWritable = $true } finally { $writeProbe.Dispose() }
        } catch [IO.IOException] { }
        Assert-RtssCheck (-not $copyWritable) 'verified uninstaller copy remains locked against modification through launch'
        if ($script:rtssTestDeleteFiles) {
            foreach ($name in @('RTSS.exe', 'uninstall.exe')) {
                $fixture = [IO.Path]::Combine($script:rtssTestRegistrations[0].InstallLocation, $name)
                Assert-RtssFixturePath $fixture; [IO.File]::Delete($fixture)
            }
        }
        return $script:rtssTestResult
    }

    $rtssTestOldTemp = $env:TEMP; $rtssTestOldTmp = $env:TMP
    $env:TEMP = [IO.Path]::Combine($rtssTestOutput, 'temporary'); $env:TMP = $env:TEMP
    [void][IO.Directory]::CreateDirectory($env:TEMP)
    try {
        Reset-RtssTestEnvironment 'user-installed'
        $ownedPlan = @(Get-RtssUninstallPlan)[0]
        $savedOwnership = $script:rtssTestOwnership
        $script:rtssTestOwnership = ''
        Assert-RtssCheck (@(Get-RtssUninstallPlan).Count -eq 0) 'user installation without a Pavise receipt is never selected'
        Invoke-RtssUninstallCommand -Uninstall
        Assert-RtssThrows { Invoke-RtssUninstall $ownedPlan } 'a caller cannot bypass ownership by passing a previously valid plan'
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'user installation is not closed, modified or uninstalled'
        foreach ($invalidReceipt in @('malformed', ($savedOwnership -replace '[A-F0-9]$', 'Z'),
            ($savedOwnership -replace "`n[0-9]+`n", "`n1`n"),
            ($savedOwnership -replace 'RTSS.exe', 'other.exe'))) {
            $script:rtssTestOwnership = $invalidReceipt
            Assert-RtssCheck (@(Get-RtssUninstallPlan).Count -eq 0) 'malformed, changed-hash, changed-creation or wrong-path receipt preserves installation'
        }

        Reset-RtssTestEnvironment 'preview'
        $script:rtssTestRunning = $true
        $plans = @(Get-RtssUninstallPlan)
        Assert-RtssCheck ($plans.Count -eq 1 -and $plans[0].Running -eq 1) 'preview returns one verified selected installation and mocked running count'
        Invoke-RtssUninstallCommand
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'default command previews without native initialization, process stop, or removal'
        Assert-RtssThrows { Get-RtssUninstallPlan -InstallPath '\\server\RTSS' } 'unsafe explicit selection fails before discovery'
        $script:rtssTestRegistrations += $script:rtssTestRegistrations[0]
        Assert-RtssCheck (@(Get-RtssUninstallPlan).Count -eq 1) 'duplicate registry views do not duplicate the selected installation'
        $second = New-RtssTestInstallation 'other-installed-RTSS'
        $script:rtssTestRegistrations += $second; $script:rtssTestAllowed += $second.InstallLocation
        Assert-RtssCheck (@(Get-RtssUninstallPlan).Count -eq 1) 'a second user installation is excluded even beside a Pavise installation'
        Invoke-RtssUninstallCommand -InstallPath $second.InstallLocation -Uninstall
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'explicitly selecting a user installation still performs no uninstall actions'
        Assert-RtssCheck (@(Get-RtssUninstallPlan -InstallPath $second.InstallLocation).Count -eq 0) 'explicit selection cannot override installation ownership'

        Reset-RtssTestEnvironment 'ownership-race'
        $plan = @(Get-RtssUninstallPlan)[0]
        $script:rtssTestLoseOwnershipOnClose = $true
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'lost ownership during graceful shutdown prevents uninstall launch'
        Assert-RtssCheck (-not $script:rtssTestEvents.Contains('run')) 'ownership is checked again immediately before destructive execution'

        Reset-RtssTestEnvironment 'not-elevated'; $script:rtssTestAdmin = $false
        $plan = @(Get-RtssUninstallPlan)[0]
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'non-elevated removal fails closed'
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'non-elevated removal performs no process/native actions'
        Reset-RtssTestEnvironment 'already-pending'; $script:rtssTestPending = $true
        $plan = @(Get-RtssUninstallPlan)[0]
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'existing installer or uninstaller prevents duplicate removal'
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'pending operation prevents native initialization and process stop'
        Reset-RtssTestEnvironment 'identity-race'; $plan = @(Get-RtssUninstallPlan)[0]
        [IO.File]::WriteAllText($plan.Executable, 'changed-before-action')
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'file identity change after preview prevents removal'
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'identity mismatch prevents native initialization and process stop'
        Reset-RtssTestEnvironment 'registration-race'; $plan = @(Get-RtssUninstallPlan)[0]
        $script:rtssTestRegistrations[0].Publisher = 'Changed publisher'
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'registration change after preview prevents removal'
        Assert-RtssCheck ($script:rtssTestEvents.Count -eq 0) 'changed registration prevents native initialization and process stop'
        Reset-RtssTestEnvironment 'close-failed'; $script:rtssTestClose = $false
        $plan = @(Get-RtssUninstallPlan)[0]
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'failed normal process exit prevents uninstall'
        Assert-RtssCheck (-not $script:rtssTestEvents.Contains('run')) 'failed normal process exit never falls back to launching uninstaller'
        Reset-RtssTestEnvironment 'close-race'; $script:rtssTestMutateOnClose = $true
        $plan = @(Get-RtssUninstallPlan)[0]
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'identity change while closing selected process prevents uninstall'
        Assert-RtssCheck (-not $script:rtssTestEvents.Contains('run')) 'process-exit race fails before uninstall launch'

        foreach ($code in @(0, 3010, 1641)) {
            Reset-RtssTestEnvironment ('remaining-files-' + $code); $script:rtssTestResult.ExitCode = $code
            $plan = @(Get-RtssUninstallPlan)[0]
            Assert-RtssThrows { Invoke-RtssUninstall $plan } ('exit code ' + $code + ' alone does not confirm removal while files remain')
            Assert-RtssCheck ($script:rtssTestEvents.Contains('run')) ('exit code ' + $code + ' fixture reaches only the mocked uninstaller')
            Assert-RtssCheck (@(Get-ChildItem -LiteralPath $env:TEMP -Directory).Count -eq 0) 'completed invocation cleans only its generated temporary copy'
        }
        Reset-RtssTestEnvironment 'failed-exit'; $script:rtssTestResult.ExitCode = 5
        $plan = @(Get-RtssUninstallPlan)[0]
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'nonzero uninstaller exit reports failure'
        Assert-RtssCheck ([IO.File]::Exists($plan.Executable) -and [IO.File]::Exists($plan.Uninstaller)) 'failed removal preserves installation fixtures for retry'
        Assert-RtssCheck (-not [string]::IsNullOrEmpty($script:rtssTestOwnership)) 'failed uninstall retains installation ownership for a later retry'
        Reset-RtssTestEnvironment 'verified-success'; $script:rtssTestDeleteFiles = $true
        Invoke-RtssUninstallCommand -Uninstall
        Assert-RtssCheck (($script:rtssTestEvents -join ',') -ceq 'initialize,close,run,clear-receipt') 'verified removal initializes, closes only the owned selection, uninstalls and then clears ownership'
        Assert-RtssCheck ([string]::IsNullOrEmpty($script:rtssTestOwnership)) 'ownership receipt is cleared only after file and process removal is verified'
        Assert-RtssCheck ($script:rtssTestLastStart.Arguments -ceq ('/S _?=' + $script:rtssTestRegistrations[0].InstallLocation)) 'actual invocation passes exact silent NSIS arguments to mock'
        Assert-RtssCheck (@(Get-ChildItem -LiteralPath $env:TEMP -Directory).Count -eq 0) 'verified removal cleans generated copy without recursive installation deletion'
        Reset-RtssTestEnvironment 'pending-result'; $script:rtssTestResult.Pending = $true
        $plan = @(Get-RtssUninstallPlan)[0]
        Assert-RtssThrows { Invoke-RtssUninstall $plan } 'pending uninstaller reports incomplete removal'
        Assert-RtssCheck (@(Get-ChildItem -LiteralPath $env:TEMP -Directory).Count -eq 1) 'pending uninstaller retains its generated copy'
    } finally { $env:TEMP = $rtssTestOldTemp; $env:TMP = $rtssTestOldTmp }

    Assert-RtssCheck ((Get-RtssFixtureSha256 $rtssTestScript) -eq $rtssTestOriginalHash) 'validation leaves uninstall script unchanged'
    $summary = 'PASS standalone RTSS uninstall checks=' + $script:rtssTestChecks + '; all process/registry actions mocked; no product or uninstaller executed'
    $script:rtssTestLog.Add($summary)
    [IO.File]::WriteAllLines((Join-Path $rtssTestOutput 'checks.log'), $script:rtssTestLog.ToArray(), (New-Object Text.UTF8Encoding($false)))
    Write-Output $summary
    Write-Output ('OUTPUT ' + $rtssTestOutput)
} catch {
    $script:rtssTestLog.Add($_.ToString())
    [IO.File]::WriteAllLines((Join-Path $rtssTestOutput 'checks.log'), $script:rtssTestLog.ToArray(), (New-Object Text.UTF8Encoding($false)))
    throw
}
