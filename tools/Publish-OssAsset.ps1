# @author bdth 2074055628@qq.com
# 文件用途 把单个文件签名 PUT 到阿里云 OSS 的 version/ 目录 捐赠二维码这类随清单拉取的小资源用
# 凭据查找顺序与 Publish-VersionManifest.ps1 相同
#   1 %LOCALAPPDATA%\Pavise\Publishing\oss-paivse.credential.xml
#   2 环境变量 OSS_ACCESS_KEY_ID 与 OSS_ACCESS_KEY_SECRET
#
# Usage:
#   powershell -File tools\Publish-OssAsset.ps1 -File docs\wechat.png -Key version/donate.png -ContentType image/png
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [Parameter(Mandatory = $true)][string]$Key,
    [string]$ContentType = "application/octet-stream",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$bucket = "paivse"
$endpoint = "oss-cn-shanghai.aliyuncs.com"
if ($Key -notmatch "^version/[A-Za-z0-9._-]+`$") { throw "Key must live under version/ and use plain characters: $Key" }
$publicUrl = "https://$bucket.$endpoint/$Key"

$path = [IO.Path]::GetFullPath($File)
if (-not (Test-Path -LiteralPath $path)) { throw "File not found: $path" }
$bytes = [IO.File]::ReadAllBytes($path)
if ($bytes.Length -eq 0) { throw "File is empty: $path" }
if ($bytes.Length -gt 2097152) { throw "File is $($bytes.Length) bytes; the client refuses anything over 2 MiB." }

Write-Host "Asset:  $path ($($bytes.Length) bytes, $ContentType)"
Write-Host "Target: $publicUrl"
if ($DryRun) { Write-Host "Dry run; nothing uploaded."; exit 0 }

$credentialPath = Join-Path $env:LOCALAPPDATA "Pavise\Publishing\oss-paivse.credential.xml"
$ak = $null; $sk = $null
if (Test-Path -LiteralPath $credentialPath) {
    $stored = Import-Clixml -LiteralPath $credentialPath
    if ($stored -isnot [Management.Automation.PSCredential]) { throw "The saved OSS credential is not a PSCredential." }
    $ak = $stored.UserName; $sk = $stored.GetNetworkCredential().Password
    Write-Host "Using the saved Windows-encrypted OSS credential."
} else {
    $ak = $env:OSS_ACCESS_KEY_ID; $sk = $env:OSS_ACCESS_KEY_SECRET
}
if (-not $ak -or -not $sk) { throw "No credentials found. Save a PSCredential at $credentialPath or set OSS_ACCESS_KEY_ID and OSS_ACCESS_KEY_SECRET." }

$md5 = [Convert]::ToBase64String(([Security.Cryptography.MD5]::Create()).ComputeHash($bytes))
$date = [DateTime]::UtcNow.ToString("R", [Globalization.CultureInfo]::InvariantCulture)
$stringToSign = "PUT`n$md5`n$ContentType`n$date`n/$bucket/$Key"
$hmac = New-Object Security.Cryptography.HMACSHA1 (, [Text.Encoding]::UTF8.GetBytes($sk))
$signature = [Convert]::ToBase64String($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($stringToSign)))
$request = [Net.HttpWebRequest]::Create($publicUrl)
$request.Method = "PUT"
$request.Date = [DateTime]::UtcNow
$request.ContentType = $ContentType
$request.ContentLength = $bytes.Length
$request.Headers.Add("Content-MD5", $md5)
$request.Headers.Add("Authorization", "OSS ${ak}:$signature")
$stream = $request.GetRequestStream()
try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Close() }
$response = $null
try { $response = $request.GetResponse() }
catch [Net.WebException] {
    # 阿里云把原因写在响应体的 XML 里 403 光看状态码分不清是签名错还是没权限
    $body = ""
    if ($_.Exception.Response) { $r = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $body = $r.ReadToEnd(); $r.Close() }
    throw "OSS PUT failed: $($_.Exception.Message)`n$body"
}
try { if ([int]$response.StatusCode -ne 200) { throw "OSS PUT returned HTTP $([int]$response.StatusCode)" } }
finally { $response.Close() }
Write-Host "Uploaded via signed PUT."

$client = New-Object Net.WebClient
try { $served = $client.DownloadData($publicUrl) } finally { $client.Dispose() }
if ([Convert]::ToBase64String($served) -ne [Convert]::ToBase64String($bytes)) {
    throw "Read-back from $publicUrl does not match the uploaded file."
}
Write-Host "Verified: the public endpoint serves the uploaded bytes."
