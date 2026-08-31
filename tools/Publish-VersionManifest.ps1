# @author bdth 2074055628@qq.com
# File purpose: publish version.json to the Aliyun OSS update endpoint.
# The app checks https://paivse.oss-cn-shanghai.aliyuncs.com/version/version.json
# (with an oss-accelerate mirror). This script validates the local manifest,
# uploads it, and reads it back from the public URL to confirm.
#
# Credentials (either one):
#   1) ossutil/ossutil64 on PATH with a configured profile, or
#   2) environment variables OSS_ACCESS_KEY_ID / OSS_ACCESS_KEY_SECRET
#      (a RAM user with PutObject on paivse/version/* is enough).
#
# Usage:
#   powershell -File tools\Publish-VersionManifest.ps1            # publish
#   powershell -File tools\Publish-VersionManifest.ps1 -DryRun    # validate only
[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$bucket = "paivse"
$endpoint = "oss-cn-shanghai.aliyuncs.com"
$objectKey = "version/version.json"
$publicUrl = "https://$bucket.$endpoint/$objectKey"

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$manifestPath = Join-Path $repo "version.json"

# Keep the manifest derived from App.Version before publishing anything.
& (Join-Path $PSScriptRoot "Sync-VersionManifest.ps1")

$bytes = [IO.File]::ReadAllBytes($manifestPath)
$text = [Text.Encoding]::UTF8.GetString($bytes)
$manifest = $text | ConvertFrom-Json
$parsed = $null
if (-not [Version]::TryParse([string]$manifest.version, [ref]$parsed) -or $parsed.ToString(4) -ne [string]$manifest.version) {
    throw "version.json 'version' is not a canonical four-part version: $($manifest.version)"
}
foreach ($field in "url", "mirror") {
    $value = [string]$manifest.$field
    if (-not $value.StartsWith("https://", [StringComparison]::Ordinal)) {
        throw "version.json '$field' must be an https URL: $value"
    }
}
if ($bytes.Length -gt 65536) { throw "version.json is unexpectedly large ($($bytes.Length) bytes); refusing to publish." }

Write-Host "Manifest OK: version $($manifest.version), $($bytes.Length) bytes"
Write-Host "Target:      $publicUrl"
if ($DryRun) { Write-Host "Dry run; nothing uploaded."; exit 0 }

function Invoke-SignedPut {
    param([byte[]]$Body)
    $ak = $env:OSS_ACCESS_KEY_ID
    $sk = $env:OSS_ACCESS_KEY_SECRET
    if (-not $ak -or -not $sk) { return $false }
    $md5 = [Convert]::ToBase64String(([Security.Cryptography.MD5]::Create()).ComputeHash($Body))
    $contentType = "application/json"
    $date = [DateTime]::UtcNow.ToString("R", [Globalization.CultureInfo]::InvariantCulture)
    $stringToSign = "PUT`n$md5`n$contentType`n$date`n/$bucket/$objectKey"
    $hmac = New-Object Security.Cryptography.HMACSHA1 (, [Text.Encoding]::UTF8.GetBytes($sk))
    $signature = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($stringToSign)))
    $request = [Net.HttpWebRequest]::Create($publicUrl)
    $request.Method = "PUT"
    $request.Date = [DateTime]::UtcNow
    $request.ContentType = $contentType
    $request.ContentLength = $Body.Length
    $request.Headers.Add("Content-MD5", $md5)
    $request.Headers.Add("Authorization", "OSS ${ak}:$signature")
    $stream = $request.GetRequestStream()
    try { $stream.Write($Body, 0, $Body.Length) } finally { $stream.Close() }
    $response = $request.GetResponse()
    try {
        if ([int]$response.StatusCode -ne 200) { throw "OSS PUT returned HTTP $([int]$response.StatusCode)" }
    }
    finally { $response.Close() }
    Write-Host "Uploaded via signed PUT."
    return $true
}

function Invoke-OssUtil {
    param([string]$Path)
    $tool = $null
    foreach ($name in "ossutil64", "ossutil") {
        $found = Get-Command $name -ErrorAction SilentlyContinue
        if ($found) { $tool = $found.Source; break }
    }
    if (-not $tool) { return $false }
    & $tool cp -f $Path "oss://$bucket/$objectKey" --meta "Content-Type:application/json"
    if ($LASTEXITCODE -ne 0) { throw "ossutil upload failed with exit code $LASTEXITCODE" }
    Write-Host "Uploaded via ossutil."
    return $true
}

$uploaded = Invoke-OssUtil -Path $manifestPath
if (-not $uploaded) { $uploaded = Invoke-SignedPut -Body $bytes }
if (-not $uploaded) {
    throw "No credentials found. Install/configure ossutil, or set OSS_ACCESS_KEY_ID and OSS_ACCESS_KEY_SECRET."
}

# Read back from the public endpoint; a mismatch means a stale or failed write.
$client = New-Object Net.WebClient
try { $served = $client.DownloadData($publicUrl) } finally { $client.Dispose() }
if ([Convert]::ToBase64String($served) -ne [Convert]::ToBase64String($bytes)) {
    throw "Read-back from $publicUrl does not match the uploaded manifest; check bucket cache/CDN settings."
}
Write-Host "Verified: the public endpoint now serves version $($manifest.version)."
Write-Host "Note: the oss-accelerate mirror may take a short time to converge."
