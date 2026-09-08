# @author bdth 2074055628@qq.com
# 文件用途 把 version.json 发到阿里云 OSS 的更新地址
# 程序查的是 https://paivse.oss-cn-shanghai.aliyuncs.com/version/version.json 另有 oss-accelerate 镜像
# 这个脚本先校验本地清单 再上传 最后从公网地址读回来确认
#
# 凭据依次查找
#   1 当前 Windows 用户的加密凭据 %LOCALAPPDATA%\Pavise\Publishing\oss-paivse.credential.xml
#     使用 Export-Clixml 保存的 PSCredential 密钥由 Windows 加密 仅原用户在原机器可解密
#   2 PATH 上有配好 profile 的 ossutil 或 ossutil64
#   3 环境变量 OSS_ACCESS_KEY_ID 和 OSS_ACCESS_KEY_SECRET
#     一个对 paivse/version/* 有 PutObject 权限的 RAM 用户就够
#
# Usage:
#   powershell -File tools\\Publish-VersionManifest.ps1            发布
#   powershell -File tools\\Publish-VersionManifest.ps1 -DryRun    只校验
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

# 发之前先让清单跟 App.Version 对上
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

$credentialPath = Join-Path $env:LOCALAPPDATA "Pavise\Publishing\oss-paivse.credential.xml"
$storedCredential = $null
if (Test-Path -LiteralPath $credentialPath) {
    try { $storedCredential = Import-Clixml -LiteralPath $credentialPath }
    catch { throw "Cannot decrypt the saved OSS credential for this Windows user." }
    if ($storedCredential -isnot [Management.Automation.PSCredential]) {
        throw "The saved OSS credential is not a PSCredential."
    }
    Write-Host "Using the saved Windows-encrypted OSS credential."
}

function Invoke-SignedPut {
    param([byte[]]$Body)
    $ak = if ($storedCredential) { $storedCredential.UserName } else { $env:OSS_ACCESS_KEY_ID }
    $sk = if ($storedCredential) { $storedCredential.GetNetworkCredential().Password } else { $env:OSS_ACCESS_KEY_SECRET }
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

$uploaded = if ($storedCredential) { Invoke-SignedPut -Body $bytes } else { Invoke-OssUtil -Path $manifestPath }
if (-not $uploaded) { $uploaded = Invoke-SignedPut -Body $bytes }
if (-not $uploaded) {
    throw "No credentials found. Save a Windows-encrypted PSCredential at $credentialPath, configure ossutil, or set OSS_ACCESS_KEY_ID and OSS_ACCESS_KEY_SECRET."
}

# 从公网地址读回来 对不上就说明写入过期或者失败了
$client = New-Object Net.WebClient
try { $served = $client.DownloadData($publicUrl) } finally { $client.Dispose() }
if ([Convert]::ToBase64String($served) -ne [Convert]::ToBase64String($bytes)) {
    throw "Read-back from $publicUrl does not match the uploaded manifest; check bucket cache/CDN settings."
}
Write-Host "Verified: the public endpoint now serves version $($manifest.version)."
Write-Host "Note: the oss-accelerate mirror may take a short time to converge."
