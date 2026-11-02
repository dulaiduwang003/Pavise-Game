param([string]$WorkspaceRoot)
$ErrorActionPreference = 'Stop'
if (-not $WorkspaceRoot) { $WorkspaceRoot = Split-Path $PSScriptRoot -Parent }
$output = Join-Path $env:TEMP ('Pavise-LaboratoryAudio-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $output
$paths = @('src/Core/LaboratoryAudio.cs', 'src/Platform/LowLatencyAudioNative.cs', 'tests/LaboratoryAudioChecks.cs')
$frozen = @()
foreach ($relative in $paths) {
    $source = Join-Path $WorkspaceRoot $relative
    $target = Join-Path $output ([IO.Path]::GetFileName($source))
    [IO.File]::Copy($source, $target)
    if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw 'Audio source changed while freezing' }
    $frozen += $target
}
$exe = Join-Path $output 'LaboratoryAudioChecks.exe'
$rsp = Join-Path $output 'check.rsp'
$args = @('/nologo', '/target:exe', '/platform:x64', '/codepage:65001', '/utf8output', '/define:PAVISE_LAB_AUDIO_CHECKS',
    '/main:PaviseApp.LaboratoryAudioChecks', ('/out:"' + $exe + '"'), '/reference:System.dll')
$args += @($frozen | ForEach-Object { '"' + $_ + '"' })
[IO.File]::WriteAllLines($rsp, $args, (New-Object Text.UTF8Encoding($false)))
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') ('@' + $rsp) *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'compile.log'); throw 'Laboratory audio check compile failed' }
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = 'Hidden'
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$child = New-Object Diagnostics.Process
$child.StartInfo = $info
try {
    $null = $child.Start()
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(15000)) { $child.Kill(); $child.WaitForExit(); throw 'Laboratory audio fixture timed out' }
    $child.WaitForExit()
    $text = $stdout.Result + $stderr.Result
    [IO.File]::WriteAllText((Join-Path $output 'checks.log'), $text)
    Write-Output $text
    if ($child.ExitCode -ne 0) { throw ('Laboratory audio checks failed; output=' + $output) }
} finally { $child.Dispose() }
Write-Output ('OUTPUT ' + $output)
