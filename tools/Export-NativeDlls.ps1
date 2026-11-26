param(
    [string]$OutputDirectory = '',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'build' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $output)) { $null = New-Item -ItemType Directory -Path $output }

$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $components = @(
        @{
            Name = 'PaviseCpu.dll';
            Class = 'NativeCpuImage';
            Source = Join-Path $root 'src\Interop\NativeCpuImage.cs'
        },
        @{
            Name = 'PaviseEtw.dll';
            Class = 'NativeEtwImage';
            Source = Join-Path $root 'src\Interop\NativeEtwImage.cs'
        },
        @{
            Name = 'PaviseHid.dll';
            Class = 'NativeHidImage';
            Source = Join-Path $root 'src\Interop\NativeHidImage.cs'
        }
    )

    foreach ($comp in $components) {
        if (-not (Test-Path -LiteralPath $comp.Source)) { throw ('Source file not found: ' + $comp.Source) }
        $text = [IO.File]::ReadAllText($comp.Source)

        $shaMatch = [regex]::Match($text, 'internal const string Sha256 = "([0-9a-f]+)";')
        if (-not $shaMatch.Success) { throw ('Missing Sha256 constant in ' + $comp.Source) }
        $expectedSha = $shaMatch.Groups[1].Value

        $lenMatch = [regex]::Match($text, 'internal const int Length = ([0-9]+);')
        if (-not $lenMatch.Success) { throw ('Missing Length constant in ' + $comp.Source) }
        $expectedLen = [int]::Parse($lenMatch.Groups[1].Value)

        $base64Index = $text.IndexOf('internal const string Base64 =')
        if ($base64Index -lt 0) { throw ('Missing Base64 in ' + $comp.Source) }
        $semicolon = $text.IndexOf(';', $base64Index)
        $rawAssignment = $text.Substring($base64Index, $semicolon - $base64Index)
        $literals = [regex]::Matches($rawAssignment, '"([^"]*)"')
        $sb = New-Object System.Text.StringBuilder
        foreach ($m in $literals) { $null = $sb.Append($m.Groups[1].Value) }
        $base64 = $sb.ToString()

        $bytes = [Convert]::FromBase64String($base64)
        if ($bytes.Length -ne $expectedLen) {
            throw ('Decoded length ' + $bytes.Length + ' does not match expected length ' + $expectedLen + ' for ' + $comp.Name)
        }

        $hash = (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
        if ($hash -ne $expectedSha) {
            throw ('Decoded hash ' + $hash + ' does not match expected hash ' + $expectedSha + ' for ' + $comp.Name)
        }

        $targetPath = Join-Path $output $comp.Name
        if (-not $CheckOnly) {
            [IO.File]::WriteAllBytes($targetPath, $bytes)
            Write-Output ('Exported ' + $comp.Name + ' -> ' + $targetPath + ' (SHA256: ' + $hash + ')')
        } else {
            Write-Output ('Verified ' + $comp.Name + ' (SHA256: ' + $hash + ')')
        }
    }
} finally {
    $sha.Dispose()
}
