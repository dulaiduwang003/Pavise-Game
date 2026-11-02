param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$OutputDirectory = '',
    [string]$PrivateKeyPath = '',
    [ValidateRange(1, 365)][int]$ValidityDays = 90
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseExecutable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $releaseExecutable -PathType Leaf)) { throw 'Build the release executable first.' }
if (-not $PrivateKeyPath) { $PrivateKeyPath = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex/pavise-release-signing/update-private.xml' }
$releaseKeyPath = [IO.Path]::GetFullPath($PrivateKeyPath)
if (-not (Test-Path -LiteralPath $releaseKeyPath -PathType Leaf)) { throw 'The release signing key is missing. Restore the existing private key; do not generate a different key for an already shipped updater.' }
if ($releaseKeyPath.StartsWith($releaseWorkspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The private signing key must remain outside the repository.' }

& (Join-Path $PSScriptRoot 'Verify-BuildIdentity.ps1') -Executable $releaseExecutable
if (-not $?) { throw 'Build identity verification failed.' }
$releaseVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($releaseExecutable).FileVersion
$releaseTrustSource = [IO.File]::ReadAllText((Join-Path $releaseWorkspace 'src/PaviseApp/SelfUpdateManifest.cs'))
$releaseTrustMatch = [regex]::Match($releaseTrustSource, 'TrustedPublicKeyXml\s*=\s*"([^"]+)"')
if (-not $releaseTrustMatch.Success) { throw 'The built-in update verification key could not be read.' }
$releaseSigner = New-Object Security.Cryptography.RSACryptoServiceProvider
$releaseSigner.PersistKeyInCsp = $false
try {
    $releaseSigner.FromXmlString([IO.File]::ReadAllText($releaseKeyPath))
    if ($releaseSigner.KeySize -ne 3072 -or $releaseSigner.PublicOnly) { throw 'A 3072-bit RSA private key is required.' }
    if ($releaseSigner.ToXmlString($false) -cne $releaseTrustMatch.Groups[1].Value) { throw 'The signing key does not match the updater public key embedded in the current source.' }
    $releaseHash = (Get-FileHash -LiteralPath $releaseExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    $releasePayload = [ordered]@{
        schema = 1
        version = $releaseVersion
        size = (Get-Item -LiteralPath $releaseExecutable).Length
        sha256 = $releaseHash
        minUpdater = 1
        expiresUtc = [DateTime]::UtcNow.AddDays($ValidityDays).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
        urls = @(
            "https://pavise.club/updates/$releaseVersion/Pavise.exe",
            "https://pavise-website.2074055628.workers.dev/updates/$releaseVersion/Pavise.exe",
            "https://github.com/dulaiduwang003/Pavise-Game/releases/download/v$releaseVersion/Pavise.exe"
        )
    }
    $releaseEncoding = New-Object Text.UTF8Encoding($false)
    $releasePayloadBytes = $releaseEncoding.GetBytes(($releasePayload | ConvertTo-Json -Depth 5 -Compress))
    $releaseSignature = $releaseSigner.SignData($releasePayloadBytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    [string]$releaseManifest = [ordered]@{ payload = [Convert]::ToBase64String($releasePayloadBytes); signature = [Convert]::ToBase64String($releaseSignature) } | ConvertTo-Json -Compress
} finally { $releaseSigner.Dispose() }

# Exercise the production verifier without entering Program.Main or starting Pavise.
$releaseAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($releaseExecutable))
$releaseManifestType = $releaseAssembly.GetType('PaviseApp.SelfUpdateManifest', $true)
$releaseFlags = [Reflection.BindingFlags]'Static,NonPublic'
$releaseParser = $releaseManifestType.GetMethod('ParseAndVerify', $releaseFlags, $null, [type[]]@([string]), $null)
if ($null -eq $releaseParser) { throw 'This executable does not contain the production update verifier.' }
try { $null = $releaseParser.Invoke($null, [object[]]@([string]$releaseManifest)) }
catch { throw ('The release manifest did not pass the built executable verifier: ' + $_.Exception.GetBaseException().Message) }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $releaseWorkspace ('build/update-release-' + $releaseVersion + '-' + [Guid]::NewGuid().ToString('N')) }
$releaseOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $releaseOutput) { throw 'Use a fresh output directory; published versioned binaries must not be overwritten.' }
$releaseVersionDirectory = Join-Path $releaseOutput "updates/$releaseVersion"
$null = New-Item -ItemType Directory -Path $releaseVersionDirectory
[IO.File]::Copy($releaseExecutable, (Join-Path $releaseVersionDirectory 'Pavise.exe'))
[IO.File]::WriteAllText((Join-Path $releaseOutput 'updates/stable.json'), $releaseManifest, $releaseEncoding)
[IO.File]::WriteAllText((Join-Path $releaseOutput 'release-metadata.json'), ($releasePayload | ConvertTo-Json -Depth 5), $releaseEncoding)
$releaseManualDirectory = Join-Path $releaseOutput 'manual/Pavise'
$null = New-Item -ItemType Directory -Path $releaseManualDirectory
[IO.File]::Copy($releaseExecutable, (Join-Path $releaseManualDirectory 'Pavise.exe'))
foreach ($releaseCompanion in @('Pavise.exe.config', 'LICENSE', 'NOTICE', 'Uninstall-RTSS.cmd')) {
    $releaseCompanionPath = Join-Path $releaseWorkspace $releaseCompanion
    if (Test-Path -LiteralPath $releaseCompanionPath) { [IO.File]::Copy($releaseCompanionPath, (Join-Path $releaseManualDirectory $releaseCompanion)) }
}
[IO.File]::Copy((Join-Path $releaseWorkspace 'tools/Uninstall-Rtss.ps1'), (Join-Path $releaseManualDirectory 'Uninstall-Rtss.ps1'))
Compress-Archive -LiteralPath $releaseManualDirectory -DestinationPath (Join-Path $releaseOutput "Pavise-$releaseVersion.zip") -CompressionLevel Optimal
[pscustomobject]@{
    Version = $releaseVersion
    SHA256 = $releaseHash
    OutputDirectory = $releaseOutput
    SignedManifest = (Join-Path $releaseOutput 'updates/stable.json')
    Published = $false
} | ConvertTo-Json
