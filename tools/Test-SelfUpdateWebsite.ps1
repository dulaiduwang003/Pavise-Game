param(
    [Parameter(Mandatory = $true)][string]$WebsiteDirectory,
    [Parameter(Mandatory = $true)][string]$InstallerFixtureDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$siteTestWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$siteTestWebsite = [IO.Path]::GetFullPath($WebsiteDirectory)
$siteTestFixture = [IO.Path]::GetFullPath($InstallerFixtureDirectory)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $siteTestWorkspace ('build/SelfUpdateWebsite-' + [Guid]::NewGuid().ToString('N')) }
$siteTestOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $siteTestOutput) { throw 'Use a fresh isolated output directory.' }
$siteTestCandidate = Join-Path $siteTestFixture 'Pavise.candidate-fixture.exe'
$siteTestFeed = Join-Path $siteTestFixture 'fixture-manifest.json'
$siteTestTrustSource = Join-Path $siteTestFixture 'sources/PaviseApp/SelfUpdateManifest.cs'
$siteTestWorker = Join-Path $siteTestWebsite 'node_modules/wrangler/bin/wrangler.js'
foreach ($siteTestRequired in @($siteTestCandidate, $siteTestFeed, $siteTestTrustSource, $siteTestWorker, (Join-Path $siteTestWebsite 'dist/_headers'))) {
    if (-not (Test-Path -LiteralPath $siteTestRequired -PathType Leaf)) { throw ('Missing test input: ' + $siteTestRequired) }
}
if ((Get-FileHash -LiteralPath (Join-Path $siteTestWebsite 'public/_headers') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $siteTestWebsite 'dist/_headers') -Algorithm SHA256).Hash) { throw 'Build the website first; its response-header configuration is stale.' }
$siteTestEnvelope = Get-Content -LiteralPath $siteTestFeed -Raw | ConvertFrom-Json
$siteTestMetadata = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($siteTestEnvelope.payload)) | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $siteTestCandidate -Algorithm SHA256).Hash.ToLowerInvariant() -ne $siteTestMetadata.sha256) { throw 'Installer fixture candidate does not match its signed feed.' }
$siteTestTrustMatch = [regex]::Match([IO.File]::ReadAllText($siteTestTrustSource), 'TrustedPublicKeyXml = "([^"]+)";')
if (-not $siteTestTrustMatch.Success) { throw 'The isolated installer fixture signing key could not be read.' }
$null = New-Item -ItemType Directory -Path $siteTestOutput
$siteTestAssets = Join-Path $siteTestOutput 'site'
Copy-Item -LiteralPath (Join-Path $siteTestWebsite 'dist') -Destination $siteTestAssets -Recurse
$siteTestPackages = Join-Path $siteTestAssets ('updates/' + $siteTestMetadata.version)
$null = New-Item -ItemType Directory -Path $siteTestPackages -Force
Copy-Item -LiteralPath $siteTestCandidate -Destination (Join-Path $siteTestPackages 'Pavise.exe')
Copy-Item -LiteralPath $siteTestFeed -Destination (Join-Path $siteTestAssets 'updates/stable.json')
$siteTestPublicKey = Join-Path $siteTestOutput 'fixture-public-key.xml'
[IO.File]::WriteAllText($siteTestPublicKey, $siteTestTrustMatch.Groups[1].Value)
$siteTestConfig = Join-Path $siteTestOutput 'wrangler.json'
[IO.File]::WriteAllText($siteTestConfig, '{"name":"pavise-update-fixture","compatibility_date":"2026-09-14","assets":{"directory":"./site","not_found_handling":"404-page"}}')
$siteTestSources = @('src/PaviseApp/SelfUpdateManifest.cs','src/PaviseApp/SelfUpdateDownload.cs','src/PaviseApp/App.cs','tests/SelfUpdateWebsiteChecks.cs') | ForEach-Object { Join-Path $siteTestWorkspace $_ }
$siteTestInputs = @((@($siteTestSources) + @($PSCommandPath, $siteTestCandidate, $siteTestFeed, $siteTestTrustSource, (Join-Path $siteTestWebsite 'public/_headers'))) | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$siteTestInputs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $siteTestOutput 'source-manifest.json') -Encoding UTF8
$siteTestCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$siteTestExe = Join-Path $siteTestOutput 'SelfUpdateWebsiteChecks.exe'
& $siteTestCompiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 '/define:PAVISE_SELFTEST;PAVISE_SELFUPDATE_WEBSITE_CHECKS' /main:PaviseApp.SelfUpdateWebsiteChecks ('/out:' + $siteTestExe) /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll $siteTestSources *> (Join-Path $siteTestOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $siteTestOutput 'compile.log'); throw 'Website updater checker did not compile.' }
$siteTestPortProbe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
$siteTestPortProbe.Start(); $siteTestPort = $siteTestPortProbe.LocalEndpoint.Port; $siteTestPortProbe.Stop()
$siteTestEndpoint = 'http://127.0.0.1:' + $siteTestPort + '/'
$siteTestStart = New-Object Diagnostics.ProcessStartInfo
$siteTestStart.FileName = (Get-Command node.exe).Source
$siteTestStart.Arguments = '"' + $siteTestWorker + '" dev --local --ip 127.0.0.1 --port ' + $siteTestPort + ' --config "' + $siteTestConfig + '" --persist-to "' + (Join-Path $siteTestOutput 'worker-state') + '"'
$siteTestStart.WorkingDirectory = $siteTestOutput
$siteTestStart.UseShellExecute = $false; $siteTestStart.CreateNoWindow = $true; $siteTestStart.WindowStyle = 'Hidden'
$siteTestStart.RedirectStandardOutput = $true; $siteTestStart.RedirectStandardError = $true
$siteTestStart.EnvironmentVariables['WRANGLER_SEND_METRICS'] = 'false'
$siteTestServer = [Diagnostics.Process]::Start($siteTestStart)
$siteTestStdout = $siteTestServer.StandardOutput.ReadToEndAsync(); $siteTestStderr = $siteTestServer.StandardError.ReadToEndAsync()
try {
    $siteTestDeadline = [Diagnostics.Stopwatch]::StartNew(); $siteTestReady = $false
    while ($siteTestDeadline.ElapsedMilliseconds -lt 30000) {
        if ($siteTestServer.HasExited) { throw ('Local website server exited: ' + $siteTestStderr.Result) }
        try { $siteTestRequest = [Net.WebRequest]::Create($siteTestEndpoint + 'updates/stable.json'); $siteTestRequest.Timeout = 500; $siteTestResponse = $siteTestRequest.GetResponse(); $siteTestResponse.Dispose(); $siteTestReady = $true; break } catch { [Threading.Thread]::Sleep(200) }
    }
    if (-not $siteTestReady) { throw 'Local website did not become ready.' }
    $siteTestStaging = Join-Path $siteTestOutput 'download'
    $null = New-Item -ItemType Directory -Path $siteTestStaging
    & $siteTestExe $siteTestEndpoint $siteTestStaging $siteTestPublicKey *> (Join-Path $siteTestOutput 'result.log')
    Get-Content -LiteralPath (Join-Path $siteTestOutput 'result.log')
    if ($LASTEXITCODE -ne 0) { throw 'Website updater integration check failed.' }
    if (@($siteTestInputs | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 }).Count) { throw 'An input changed while the website update check ran.' }
    Write-Output ('OUTPUT ' + $siteTestOutput)
} finally {
    # Only stop the exact child process this invocation started, plus its children.
    # No selection by Pavise name, production path or user task is performed.
    if (-not $siteTestServer.HasExited) { & taskkill.exe /PID $siteTestServer.Id /T /F *> (Join-Path $siteTestOutput 'server-stop.log') }
    $siteTestServer.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $siteTestOutput 'server.log'), $siteTestStdout.Result + $siteTestStderr.Result)
    $siteTestServer.Dispose()
}
