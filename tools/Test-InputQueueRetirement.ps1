param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$inputQueueWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-InputQueueRetirement-'+[Guid]::NewGuid().ToString('N'))}
$inputQueueOutput=[IO.Path]::GetFullPath($OutputDirectory)
$inputQueueFrozen=Join-Path $inputQueueOutput 'frozen'
if(Test-Path -LiteralPath $inputQueueFrozen){throw 'Choose a fresh output directory; frozen evidence is retained.'}
$null=New-Item -ItemType Directory -Path $inputQueueFrozen -Force
$inputQueueManifest=New-Object 'Collections.Generic.List[object]'
foreach($inputQueueOriginal in @(& rg --files (Join-Path $inputQueueWorkspace 'src') -g '*.cs')){
    $inputQueueRelative=$inputQueueOriginal.Substring($inputQueueWorkspace.Length+1)
    $inputQueueCopy=Join-Path $inputQueueFrozen $inputQueueRelative
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($inputQueueCopy)) -Force
    [IO.File]::WriteAllBytes($inputQueueCopy,[IO.File]::ReadAllBytes($inputQueueOriginal))
    $inputQueueManifest.Add([pscustomobject]@{Path=$inputQueueRelative;Original=$inputQueueOriginal;SHA256=(Get-FileHash -LiteralPath $inputQueueCopy -Algorithm SHA256).Hash})
}
function Write-Utf8([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text,(New-Object Text.UTF8Encoding($false)))}
$inputQueueProduct=Join-Path $inputQueueFrozen 'src/PaviseApp'
$inputQueueType=[IO.File]::ReadAllText((Join-Path $inputQueueProduct 'InputMythTweak.cs'))
if($inputQueueType -match '\.Apply\s*\(|\bSetValue\s*\('){throw 'The retired input queue type still applies a forward setting.'}
foreach($inputQueueFile in @('PanelForm.cs','SystemAudit.cs','RepairCatalog.cs')){
    $inputQueueSource=[IO.File]::ReadAllText((Join-Path $inputQueueProduct $inputQueueFile))
    if($inputQueueSource -match '\bInputMythTweak\b|"inputmyth"|\bQueueTampered\b'){
        throw ('The retired input queue feature remains exposed by '+$inputQueueFile)
    }
}
$inputQueuePurge=[IO.File]::ReadAllText((Join-Path $inputQueueProduct 'LegacyPurge.cs'))
if($inputQueuePurge -notmatch 'InputMythTweak\.Restore'){throw 'Legacy reset/uninstall recovery no longer invokes the saved originals.'}
# Preserve every policy/restore method body. The input queue type alone uses a
# renamed ReversibleReg copy with a dictionary backend; all other product types
# retain their actual types and are never run. Actual Settings runs its existing
# PAVISE_SELFTEST transient store, whose real registry entry points throw.
$inputQueueOriginalReg=[IO.File]::ReadAllText((Join-Path $inputQueueProduct 'ReversibleReg.cs'))
$inputQueueFakeReg=[regex]::Replace($inputQueueOriginalReg,'\bReversibleReg\b','InputQueueFixtureReversibleReg')
$inputQueueFakeReg=[regex]::Replace($inputQueueFakeReg,'\bRegistryKey\b','InputQueueFixtureRegistryKey')
Write-Utf8 (Join-Path $inputQueueFrozen 'InputQueueFixtureReversibleReg.cs') $inputQueueFakeReg
$inputQueueFakeType=[regex]::Replace($inputQueueType,'\bReversibleReg\b','InputQueueFixtureReversibleReg').Replace('Registry.LocalMachine','InputQueueFixtureRegistry.LocalMachine')
Write-Utf8 (Join-Path $inputQueueProduct 'InputMythTweak.cs') $inputQueueFakeType
$inputQueueFixtureOriginal=Join-Path $inputQueueWorkspace 'tests/Official3106/InputQueueRetirementChecks.cs'
$inputQueueFixture=Join-Path $inputQueueOutput 'InputQueueRetirementChecks.cs'
[IO.File]::WriteAllBytes($inputQueueFixture,[IO.File]::ReadAllBytes($inputQueueFixtureOriginal))
$inputQueueManifest.Add([pscustomobject]@{Path='tests/Official3106/InputQueueRetirementChecks.cs';Original=$inputQueueFixtureOriginal;SHA256=(Get-FileHash -LiteralPath $inputQueueFixture -Algorithm SHA256).Hash})
$inputQueueManifest|Export-Csv -LiteralPath (Join-Path $inputQueueOutput 'source-manifest.tsv') -Delimiter ([char]9) -NoTypeInformation
Write-Utf8 (Join-Path $inputQueueOutput 'test-only-seams.txt') 'Frozen sources only. InputMythTweak references a renamed copy of the actual ReversibleReg with RegistryKey replaced by an in-memory dictionary type. No method body, receipt format, restore algorithm, Settings behavior, AppDataReset classification or RepairCatalog entry is changed. Actual Settings uses PAVISE_SELFTEST transient store; real registry APIs throw. No product UI, runtime, device or actual registry operation. UI/audit removal and LegacyPurge restoration use source contracts, not rendered UI or a real reset.'
$inputQueueSources=@(Get-ChildItem -LiteralPath $inputQueueFrozen -Recurse -File -Filter '*.cs'|ForEach-Object{$_.FullName})
$inputQueueSources+=$inputQueueFixture
$inputQueueRsp=Join-Path $inputQueueOutput 'sources.rsp'
[IO.File]::WriteAllLines($inputQueueRsp,[string[]]@($inputQueueSources|ForEach-Object{'"'+$_+'"'}),(New-Object Text.UTF8Encoding($false)))
$inputQueueCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$inputQueueExe=Join-Path $inputQueueOutput 'InputQueueRetirementChecks.exe'
& $inputQueueCompiler /nologo /nowarn:0649,0414 /target:exe /platform:x64 /unsafe+ /optimize+ /codepage:65001 '/define:PAVISE_SELFTEST;PAVISE_INPUT_QUEUE_RETIREMENT' /main:PaviseApp.InputQueueRetirementChecks ('/out:'+$inputQueueExe) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll /reference:System.Xml.dll /reference:System.Security.dll ('@'+$inputQueueRsp) *> (Join-Path $inputQueueOutput 'compile.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $inputQueueOutput 'compile.log');throw ('Input queue retirement fixture compilation failed: '+$inputQueueOutput)}
$inputQueueStart=New-Object Diagnostics.ProcessStartInfo
$inputQueueStart.FileName=$inputQueueExe;$inputQueueStart.WorkingDirectory=$inputQueueOutput
$inputQueueStart.UseShellExecute=$false;$inputQueueStart.CreateNoWindow=$true;$inputQueueStart.WindowStyle='Hidden'
$inputQueueStart.RedirectStandardOutput=$true;$inputQueueStart.RedirectStandardError=$true
$inputQueueProcess=New-Object Diagnostics.Process;$inputQueueProcess.StartInfo=$inputQueueStart
try{
    $null=$inputQueueProcess.Start();$inputQueueStdout=$inputQueueProcess.StandardOutput.ReadToEndAsync();$inputQueueStderr=$inputQueueProcess.StandardError.ReadToEndAsync()
    if(-not $inputQueueProcess.WaitForExit(15000)){$inputQueueProcess.Kill();throw 'Owned pure input queue fixture exceeded 15 seconds.'}
    $inputQueueProcess.WaitForExit();$inputQueueResult=$inputQueueStdout.Result+$inputQueueStderr.Result
    Write-Utf8 (Join-Path $inputQueueOutput 'result.txt') $inputQueueResult;Write-Output $inputQueueResult
    $inputQueueChanged=@($inputQueueManifest|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256})
    [pscustomobject]@{FrozenSources=$inputQueueManifest.Count;WorkspaceMatchesSnapshot=($inputQueueChanged.Count -eq 0);ChangedSources=@($inputQueueChanged|ForEach-Object{$_.Path});RunExit=$inputQueueProcess.ExitCode;RegistryBackend='in-memory dictionary';SettingsBackend='actual transient self-test store';ActualRegistryWrites=0;UiWindows=0;DeviceOperations=0;UiAuditRemoval='source contracts';LegacyResetRoute='source contract; actual reset not run'}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $inputQueueOutput 'verification.json') -Encoding UTF8
    if($inputQueueProcess.ExitCode -ne 0 -or $inputQueueStderr.Result.Length -ne 0 -or $inputQueueStdout.Result -notmatch 'PASS input queue retirement'){throw 'Input queue retirement fixture failed.'}
    if($inputQueueChanged.Count -ne 0){throw 'Sources changed during frozen validation; rerun against the final source.'}
}finally{$inputQueueProcess.Dispose();Write-Output ('OUTPUT '+$inputQueueOutput)}
