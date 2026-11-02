param([switch]$Formal)
$ErrorActionPreference = 'Stop'
$comparisonWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$comparisonVersionMatch = [regex]::Match([IO.File]::ReadAllText((Join-Path $comparisonWorkspace 'src\Program.cs')), 'public const string Version = "(\d+\.\d+\.\d+\.\d+)";')
if (-not $comparisonVersionMatch.Success) { throw 'The application version was not found.' }
$comparisonAppVersion = $comparisonVersionMatch.Groups[1].Value
$comparisonPackageKind = if ($Formal) { 'ReleaseStable' } else { 'StabilityComparison' }
$comparisonOutput = Join-Path $comparisonWorkspace ('build\' + $comparisonPackageKind + '-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $comparisonOutput
$comparisonSources = @(Get-ChildItem -LiteralPath (Join-Path $comparisonWorkspace 'src') -Recurse -File -Filter '*.cs')
$comparisonBefore = @($comparisonSources | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$comparisonBefore | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $comparisonOutput 'source-hashes.json') -Encoding UTF8
$comparisonCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$comparisonIcon = Join-Path $comparisonWorkspace 'build\Pavise.ico'
if (-not (Test-Path -LiteralPath $comparisonCompiler)) { throw 'The .NET Framework compiler is unavailable.' }
if (-not (Test-Path -LiteralPath $comparisonIcon)) { throw 'Build the normal application first to generate its icon.' }
$comparisonManifest = Join-Path $comparisonOutput 'Pavise.manifest'
@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security><requestedPrivileges><requestedExecutionLevel level="requireAdministrator" uiAccess="false"/></requestedPrivileges></security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
'@ | Set-Content -LiteralPath $comparisonManifest -Encoding UTF8
$comparisonFileName = if ($Formal) { 'Pavise.exe' } else { 'Pavise.Auto.' + $comparisonAppVersion + '.stability-comparison.exe' }
$comparisonExe = Join-Path $comparisonOutput $comparisonFileName
$comparisonResponse = Join-Path $comparisonOutput 'sources.rsp'
# 响应文件避免深目录路径超出命令行长度限制。
[IO.File]::WriteAllLines($comparisonResponse, [string[]]@($comparisonSources | ForEach-Object { '"' + $_.FullName + '"' }), (New-Object Text.UTF8Encoding($true)))
& $comparisonCompiler -unsafe+ -nologo -target:winexe -optimize+ -codepage:65001 `
    -define:PAVISE_STABILITY_COMPARISON ('-out:' + $comparisonExe) `
    ('-win32icon:' + $comparisonIcon) ('-win32manifest:' + $comparisonManifest) `
    -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
    -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
    ('@' + $comparisonResponse) *> (Join-Path $comparisonOutput 'compile.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath (Join-Path $comparisonOutput 'compile.log')
    throw 'Stability comparison compilation failed.'
}
$comparisonChanged = @($comparisonBefore | Where-Object {
    -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256
})
if ($comparisonChanged.Count -ne 0) { throw 'Sources changed during compilation; rebuild after sources are frozen.' }
$comparisonAssembly = [Reflection.Assembly]::LoadFile($comparisonExe)
$comparisonPolicy = $comparisonAssembly.GetType('PaviseApp.OvernightPerformancePolicy', $true)
$comparisonBinding = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$comparisonEnabled = $comparisonPolicy.GetProperty('Enabled', $comparisonBinding).GetValue($null, $null)
if ($comparisonEnabled) { throw 'Comparison build unexpectedly enables the overnight additions.' }
$comparisonRogueType = $comparisonAssembly.GetType('PaviseApp.RogueProcessWatch', $false)
$comparisonGameMode = $comparisonAssembly.GetType('PaviseApp.GameMode', $true)
$comparisonEventBinding = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
$comparisonRogueEvent = $comparisonGameMode.GetEvent('RogueProcessSuspected', $comparisonEventBinding)
if ($comparisonRogueType -or $comparisonRogueEvent) { throw 'The removed suspected-malware monitor remains in the executable.' }
$comparisonVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($comparisonExe)
if ($comparisonVersion.FileVersion -ne $comparisonAppVersion -or $comparisonVersion.ProductVersion -ne $comparisonAppVersion) { throw 'Unexpected application version; review the packaging filename.' }
foreach ($comparisonNotice in @('LICENSE', 'NOTICE')) {
    Copy-Item -LiteralPath (Join-Path $comparisonWorkspace $comparisonNotice) -Destination (Join-Path $comparisonOutput $comparisonNotice)
}
[pscustomobject]@{
    Executable = $comparisonExe
    Sha256 = (Get-FileHash -LiteralPath $comparisonExe -Algorithm SHA256).Hash
    Length = (Get-Item -LiteralPath $comparisonExe).Length
    FileVersion = $comparisonVersion.FileVersion
    ProductVersion = $comparisonVersion.ProductVersion
    Define = 'PAVISE_STABILITY_COMPARISON'
    PackageKind = $comparisonPackageKind
    OvernightAdditionsEnabled = $comparisonEnabled
    SuspectedMalwareMonitorPresent = $false
    SourceUnchanged = $true
    ProtectedInputs = $comparisonBefore.Count
    IconSha256 = (Get-FileHash -LiteralPath $comparisonIcon -Algorithm SHA256).Hash
    ManifestSha256 = (Get-FileHash -LiteralPath $comparisonManifest -Algorithm SHA256).Hash
    RuntimeStarted = $false
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $comparisonOutput 'verification.json') -Encoding UTF8
Write-Output ('OUTPUT ' + $comparisonOutput)
Write-Output ('EXECUTABLE ' + $comparisonExe)
