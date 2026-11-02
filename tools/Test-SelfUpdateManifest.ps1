param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$updateTestRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $updateTestRoot ('build/SelfUpdateManifest-' + [Guid]::NewGuid().ToString('N')) }
$updateTestOutput = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($updateTestOutput)
$updateTestSources = @('src/PaviseApp/SelfUpdateManifest.cs', 'src/PaviseApp/SelfUpdateDownload.cs', 'src/PaviseApp/App.cs',
    'tests/SelfUpdateManifestChecks.cs') | ForEach-Object { Join-Path $updateTestRoot $_ }
$updateTestInputs = @((@($updateTestSources) + @($PSCommandPath)) | ForEach-Object { [pscustomobject]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$updateTestInputs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $updateTestOutput 'source-manifest.json') -Encoding UTF8
$updateTestCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$updateProductionStub = Join-Path $updateTestOutput 'ProductionLangStub.cs'
[IO.File]::WriteAllText($updateProductionStub,'namespace PaviseApp { internal static class Lang { internal const int Cur = 0; } }')
$updateProductionAssembly = Join-Path $updateTestOutput 'SelfUpdateProduction.dll'
$updateProductionSources = @($updateTestSources | Where-Object { $_ -notlike '*SelfUpdateManifestChecks.cs' }) + @($updateProductionStub)
& $updateTestCompiler /nologo /preferreduilang:en-US /target:library /platform:x64 /optimize+ /codepage:65001 `
    ('/out:' + $updateProductionAssembly) /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll `
    $updateProductionSources *> (Join-Path $updateTestOutput 'production-compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $updateTestOutput 'production-compile.log'); throw 'Production self-update core did not compile without fixture symbols' }
$updateProductionReflection = [Reflection.Assembly]::ReflectionOnlyLoadFrom($updateProductionAssembly)
$updateFixtureFields = @($updateProductionReflection.GetTypes() | ForEach-Object {
    $_.GetFields([Reflection.BindingFlags]'Static,Public,NonPublic') | Where-Object { $_.Name -like 'Fixture*' }
})
if ($updateFixtureFields.Count) { throw 'A fixture injection hook leaked into the production assembly' }
$updatePublicVerifier = @($updateProductionReflection.GetType('PaviseApp.SelfUpdateManifest').GetMethods([Reflection.BindingFlags]'Static,Public,NonPublic') | Where-Object {
    $_.Name -eq 'ParseAndVerify' -and $_.GetParameters().Count -gt 1 -and -not $_.IsPrivate
})
if ($updatePublicVerifier.Count) { throw 'An alternate signing-key verifier leaked into the production API' }
Write-Output 'PASS Production core: no fixture transport, timeout or signing-key injection hooks'
$updateTestExe = Join-Path $updateTestOutput 'SelfUpdateManifestChecks.exe'
$updateVersionMatch = [regex]::Match([IO.File]::ReadAllText((Join-Path $updateTestRoot 'src/PaviseApp/App.cs')), 'public const string Version = "([0-9.]+)";')
if (-not $updateVersionMatch.Success) { throw 'The current application version could not be read.' }
$updateCurrentVersion = [Version]$updateVersionMatch.Groups[1].Value
$updateCandidateVersion = [Version]::new($updateCurrentVersion.Major, $updateCurrentVersion.Minor, $updateCurrentVersion.Build, $updateCurrentVersion.Revision + 1)
$updateMismatchVersion = [Version]::new($updateCandidateVersion.Major, $updateCandidateVersion.Minor, $updateCandidateVersion.Build, $updateCandidateVersion.Revision + 1)
$updateVersionSource = Join-Path $updateTestOutput 'UpdateFixtureVersion.cs'
$updateVersionCode = 'namespace PaviseApp { internal static class UpdateFixtureVersion { internal const string Candidate = "' + $updateCandidateVersion.ToString(4) + '"; internal const string Mismatch = "' + $updateMismatchVersion.ToString(4) + '"; } }'
[IO.File]::WriteAllText($updateVersionSource, $updateVersionCode, (New-Object Text.UTF8Encoding($false)))
Write-Output ('TEST ONLY current=' + $updateCurrentVersion.ToString(4) + ' candidate=' + $updateCandidateVersion.ToString(4) + '; production version unchanged')
& $updateTestCompiler /nologo /preferreduilang:en-US /target:exe /platform:x64 /optimize+ /codepage:65001 `
    '/define:PAVISE_SELFTEST;PAVISE_SELFUPDATE_MANIFEST_CHECKS' /main:PaviseApp.SelfUpdateManifestChecks ('/out:' + $updateTestExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll $updateTestSources $updateVersionSource *> (Join-Path $updateTestOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $updateTestOutput 'compile.log'); throw 'Self-update manifest checks did not compile' }
$updateTestStart = New-Object Diagnostics.ProcessStartInfo
$updateTestStart.FileName=$updateTestExe; $updateTestStart.UseShellExecute=$false; $updateTestStart.CreateNoWindow=$true; $updateTestStart.WindowStyle='Hidden'
$updateTestStart.RedirectStandardOutput=$true; $updateTestStart.RedirectStandardError=$true
$updateTestProcess = [Diagnostics.Process]::Start($updateTestStart)
try {
    $updateTestOut=$updateTestProcess.StandardOutput.ReadToEndAsync(); $updateTestErr=$updateTestProcess.StandardError.ReadToEndAsync()
    if (-not $updateTestProcess.WaitForExit(60000)) { $updateTestProcess.Kill(); throw 'Isolated self-update checks timed out' }
    $updateTestProcess.WaitForExit(); $updateTestResult=$updateTestOut.Result+$updateTestErr.Result
    [IO.File]::WriteAllText((Join-Path $updateTestOutput 'result.log'),$updateTestResult)
    Write-Output $updateTestResult
    if ($updateTestProcess.ExitCode -ne 0) { throw 'Self-update manifest regression failed' }
} finally { $updateTestProcess.Dispose() }
if (@($updateTestInputs | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 }).Count) { throw 'Self-update inputs changed during verification' }
Write-Output ('OUTPUT ' + $updateTestOutput)
