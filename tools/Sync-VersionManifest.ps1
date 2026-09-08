# @author bdth 2074055628@qq.com
# 文件用途 从 App.Version 推出外部更新清单的版本号
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$programPath = Join-Path $repo "src\Program.cs"
$manifestPath = Join-Path $repo "version.json"

$programText = [IO.File]::ReadAllText($programPath)
$versionPattern = 'public\s+const\s+string\s+Version\s*=\s*"(\d+\.\d+\.\d+\.\d+)"\s*;'
$versionMatches = [Text.RegularExpressions.Regex]::Matches($programText, $versionPattern)
if ($versionMatches.Count -ne 1) {
    throw "Expected exactly one four-part App.Version constant in $programPath"
}
$version = $versionMatches[0].Groups[1].Value
try {
    $parsed = New-Object Version $version
    if ($parsed.ToString(4) -ne $version) { throw "non-canonical version" }
}
catch {
    throw "App.Version is not a canonical four-part version: $version"
}

$manifestText = [IO.File]::ReadAllText($manifestPath)
$manifestPattern = '(?m)^(\s*"version"\s*:\s*")([^"]+)("\s*,?\s*)$'
$manifestMatches = [Text.RegularExpressions.Regex]::Matches($manifestText, $manifestPattern)
if ($manifestMatches.Count -ne 1) {
    throw "Expected exactly one version field in $manifestPath"
}
$value = $manifestMatches[0].Groups[2]
if ($value.Value -ne $version) {
    $updated = $manifestText.Substring(0, $value.Index) + $version +
        $manifestText.Substring($value.Index + $value.Length)
    [IO.File]::WriteAllText($manifestPath, $updated, (New-Object Text.UTF8Encoding($false)))
}

Write-Host "Version manifest synchronized: $version"
