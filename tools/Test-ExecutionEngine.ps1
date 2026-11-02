param(
    [string]$OutputDirectory,
    [switch]$Mutations,
    [string]$WorkspaceRoot,
    [string]$TestSourceRoot,
    [hashtable]$SourceOverrides = @{}
)
$ErrorActionPreference = 'Stop'
if (-not $WorkspaceRoot) { $WorkspaceRoot = Join-Path $PSScriptRoot '..' }
$engineWorkspace = [IO.Path]::GetFullPath($WorkspaceRoot)
if (-not $TestSourceRoot) { $TestSourceRoot = Join-Path $engineWorkspace 'tests' }
$engineTestRoot = [IO.Path]::GetFullPath($TestSourceRoot)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $engineWorkspace ('build\ExecutionEngineChecks\' + [Guid]::NewGuid().ToString('N')) }
$engineOutput = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $engineOutput) -and @(Get-ChildItem -LiteralPath $engineOutput -Force).Count -ne 0) {
    throw 'Use a new or empty execution-engine output directory; previous evidence is never overwritten.'
}
$null = New-Item -ItemType Directory -Path $engineOutput -Force
function Get-EngineSourceMap {
    $engineMap = @{}
    Get-ChildItem -LiteralPath (Join-Path $engineWorkspace 'src') -Recurse -File -Filter '*.cs' | ForEach-Object {
        $engineMap[$_.FullName.Substring($engineWorkspace.Length + 1)] = $_.FullName
    }
    foreach ($engineKey in $SourceOverrides.Keys) {
        $engineTarget = ([string]$engineKey).Replace('/', '\')
        if (-not $engineTarget.StartsWith('src\', [StringComparison]::OrdinalIgnoreCase) -or ($engineTarget.Split('\') -contains '..') -or
            [IO.Path]::GetExtension($engineTarget) -ne '.cs') { throw 'Engine source override requires a relative src C sharp target' }
        $engineMap[$engineTarget] = [IO.Path]::GetFullPath([string]$SourceOverrides[$engineKey])
    }
    foreach ($engineTest in @('ExecutionEngineChecks.cs', 'ExecutionEngineSlimChecks.cs')) {
        $engineMap['tests\' + $engineTest] = Join-Path $engineTestRoot $engineTest
    }
    return $engineMap
}
$engineMap = Get-EngineSourceMap
$engineHashes = @($engineMap.Keys | Sort-Object | ForEach-Object {
    $engineOriginal = $engineMap[$_]
    $engineFrozen = Join-Path (Join-Path $engineOutput 'frozen-inputs') $_
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($engineFrozen)) -Force
    $engineHash = (Get-FileHash -LiteralPath $engineOriginal -Algorithm SHA256).Hash
    [IO.File]::WriteAllBytes($engineFrozen, [IO.File]::ReadAllBytes($engineOriginal))
    if ((Get-FileHash -LiteralPath $engineFrozen).Hash -ne $engineHash) { throw 'Engine source changed while freezing' }
    [pscustomobject]@{Target=$_;Source=$engineOriginal;Path=$engineFrozen;Sha256=$engineHash}
})
$engineHashes | Export-Csv -LiteralPath (Join-Path $engineOutput 'sources.sha256.csv') -NoTypeInformation -Encoding UTF8
$engineToolHash = (Get-FileHash -LiteralPath $PSCommandPath).Hash
[IO.File]::WriteAllBytes((Join-Path $engineOutput 'Test-ExecutionEngine.ps1'), [IO.File]::ReadAllBytes($PSCommandPath))
if ((Get-FileHash -LiteralPath (Join-Path $engineOutput 'Test-ExecutionEngine.ps1')).Hash -ne $engineToolHash) { throw 'Engine tool changed while freezing' }
$engineSources = @($engineHashes | ForEach-Object { $_.Path })
$engineCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function Invoke-EngineHidden([string]$File, [string]$Arguments) {
    $engineInfo = New-Object Diagnostics.ProcessStartInfo
    $engineInfo.FileName=$File; $engineInfo.Arguments=$Arguments; $engineInfo.UseShellExecute=$false; $engineInfo.CreateNoWindow=$true; $engineInfo.WindowStyle='Hidden'
    $engineInfo.RedirectStandardOutput=$true; $engineInfo.RedirectStandardError=$true
    $engineInfo.StandardOutputEncoding=[Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage)
    $engineInfo.StandardErrorEncoding=$engineInfo.StandardOutputEncoding
    $engineChild = New-Object Diagnostics.Process
    $engineChild.StartInfo = $engineInfo
    try {
        $null=$engineChild.Start(); $engineStdout=$engineChild.StandardOutput.ReadToEndAsync(); $engineStderr=$engineChild.StandardError.ReadToEndAsync()
        if (-not $engineChild.WaitForExit(50000)) { $engineChild.Kill(); $engineChild.WaitForExit(); throw 'Isolated execution engine checks timed out' }
        $engineChild.WaitForExit()
        return [pscustomobject]@{ExitCode=$engineChild.ExitCode;Text=$engineStdout.Result+$engineStderr.Result}
    } finally { $engineChild.Dispose() }
}
function Compile-Engine([string[]]$Sources, [string]$Folder) {
    $null=New-Item -ItemType Directory -Path $Folder -Force
    $engineRsp=Join-Path $Folder 'sources.rsp'
    [IO.File]::WriteAllLines($engineRsp, [string[]]@($Sources | ForEach-Object { '"'+$_+'"' }), (New-Object Text.UTF8Encoding($true)))
    $engineBinary=Join-Path $Folder 'ExecutionEngineChecks.exe'
    $engineCompile=Invoke-EngineHidden $engineCompiler ('-unsafe+ -nologo -nowarn:0162,0169,0414,0649 -target:exe -platform:x64 -optimize+ -codepage:65001 "-define:PAVISE_SELFTEST;PAVISE_EXECUTION_ENGINE_CHECKS" -main:PaviseApp.ExecutionEngineChecks "-out:'+$engineBinary+'" -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll "@'+$engineRsp+'"')
    [IO.File]::WriteAllText((Join-Path $Folder 'compile.log'), $engineCompile.Text)
    if ($engineCompile.ExitCode -ne 0) { throw ('Execution engine checks did not compile '+$engineCompile.Text) }
    return $engineBinary
}
function Save-EngineVerification([int]$RunExit) {
    $engineChanged=@($engineHashes | Where-Object { (Get-FileHash -LiteralPath $_.Path).Hash -ne $_.Sha256 })
    $engineWorkspaceChanged=@($engineHashes | Where-Object { (Get-FileHash -LiteralPath $_.Source).Hash -ne $_.Sha256 })
    $engineCurrent=Get-EngineSourceMap
    $engineSetChanged=$engineCurrent.Count -ne $engineMap.Count
    foreach ($engineKey in $engineMap.Keys) { if ($engineCurrent[$engineKey] -ne $engineMap[$engineKey]) { $engineSetChanged=$true } }
    $engineToolChanged=(Get-FileHash -LiteralPath $PSCommandPath).Hash -ne $engineToolHash
    $engineStable=$engineChanged.Count -eq 0 -and $engineWorkspaceChanged.Count -eq 0 -and -not $engineSetChanged -and -not $engineToolChanged
    [pscustomobject]@{ExitCode=$RunExit;CompileExit=0;SourceCount=$engineHashes.Count;OverrideCount=$SourceOverrides.Count;DefaultSourceGraph=$SourceOverrides.Count -eq 0;FrozenSourceSnapshot=$true;InputsUnchanged=$engineChanged.Count -eq 0;WorkspaceUnchanged=$engineWorkspaceChanged.Count -eq 0;SourceSetUnchanged=(-not $engineSetChanged);ToolUnchanged=(-not $engineToolChanged);Pure=$true;Native=$false;UserSampling=$false;ProductExecuted=$false;WindowsShown=$false;Restart=$false;SourceChanged=$engineWorkspaceChanged} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $engineOutput 'verification.json') -Encoding UTF8
    if (-not $engineStable) { throw 'Tested engine inputs or source map changed' }
}
$engineExe=Compile-Engine $engineSources $engineOutput
$engineResult=Invoke-EngineHidden $engineExe ''
[IO.File]::WriteAllText((Join-Path $engineOutput 'result.txt'), $engineResult.Text)
Write-Output $engineResult.Text
if ($engineResult.ExitCode -ne 0) { Save-EngineVerification $engineResult.ExitCode; throw 'Execution engine checks failed' }
if ($Mutations) {
    $engineGate="if (guard && !match && (PerformanceTuning.ActionSelected(PerformanceActionCatalog.Mitigations)`n                    || PerformanceTuning.LegacyActionSelected(PerformanceActionCatalog.OpenGl)`n                    || PerformanceTuning.ActionSelected(PerformanceActionCatalog.ShortNames)))"
    $engineMutants=@(
        @{Name='disabled-library-gate-bypass';Module='src\Core\GameMode.PerformanceActions.cs';Find=$engineGate;Replace='if (guard && !match)';Failure='all disabled persistent actions still enumerated the library'},
        @{Name='automatic-opengl-legacy-gate-bypass';Module='src\Core\GameMode.PerformanceActions.cs';Find='|| PerformanceTuning.LegacyActionSelected(PerformanceActionCatalog.OpenGl)';Replace='|| PerformanceTuning.ActionSelected(PerformanceActionCatalog.OpenGl)';Failure='automatic OpenGL approval enumerated unused legacy library'},
        @{Name='selected-library-gate-closed';Module='src\Core\GameMode.PerformanceActions.cs';Find=$engineGate;Replace='if (false)';Failure='a selected persistent action stopped collecting its current library'},
        @{Name='disabled-coordinator-skipped';Module='src\Core\GameMode.PerformanceActions.cs';Find='performanceActions.Queue(guard, match, gameSnapshot, visualMatch, true);';Replace='if (gameSnapshot != null || match) performanceActions.Queue(guard, match, gameSnapshot, visualMatch, true);';Failure='an all disabled request skipped the original recovery coordinator'},
        @{Name='match-enumerates-library';Module='src\Core\GameMode.PerformanceActions.cs';Find=$engineGate;Replace=$engineGate.Replace('guard && !match &&','guard &&');Failure='actual match path enumerated its unused library'},
        @{Name='match-retains-unused-games';Module='src\Core\PerformanceTuning.cs';Find='games = match || games == null ?';Replace='games = games == null ?';Failure='match requests retained unused game library snapshots'},
        @{Name='repeat-middle-admission';Module='src\Core\PerformanceTuning.cs';Find='if (mitigations)';Replace='if (!GamePerformanceTargetPolicy.AllowsGameTarget(path)) continue; if (mitigations)';Failure='target admission repeated preparation checks or lost the execution boundary'}
    )
    $engineMutationResults=@()
    foreach ($engineMutant in $engineMutants) {
        $engineModule=@($engineHashes | Where-Object { $_.Target -eq $engineMutant.Module })[0].Path
        $engineText=[IO.File]::ReadAllText($engineModule).Replace("`r`n","`n")
        if (-not $engineText.Contains($engineMutant.Find)) { throw ('Missing engine mutation anchor '+$engineMutant.Name) }
        $engineFolder=Join-Path $engineOutput $engineMutant.Name; $null=New-Item -ItemType Directory -Path $engineFolder
        $engineShadow=Join-Path $engineFolder ([IO.Path]::GetFileName($engineModule))
        [IO.File]::WriteAllText($engineShadow, $engineText.Replace($engineMutant.Find,$engineMutant.Replace), (New-Object Text.UTF8Encoding($false)))
        $engineMutantExe=Compile-Engine (@($engineSources | Where-Object { $_ -ne $engineModule })+@($engineShadow)) $engineFolder
        $engineMutantResult=Invoke-EngineHidden $engineMutantExe ''
        [IO.File]::WriteAllText((Join-Path $engineFolder 'result.txt'), $engineMutantResult.Text)
        $engineRejected=$engineMutantResult.ExitCode -eq 1 -and $engineMutantResult.Text.Contains($engineMutant.Failure)
        $engineMutationResults+=[pscustomobject]@{Name=$engineMutant.Name;CompileExit=0;RunExit=$engineMutantResult.ExitCode;NamedFailure=$engineRejected}
        if (-not $engineRejected) { throw ('Engine semantic mutation was not rejected '+$engineMutant.Name+' '+$engineMutantResult.Text) }
    }
    $engineMutationResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $engineOutput 'mutations.json') -Encoding UTF8
    Write-Output ('PASS engine semantic mutants='+$engineMutationResults.Count)
}
Save-EngineVerification $engineResult.ExitCode
Write-Output ('OUTPUT '+$engineOutput)
