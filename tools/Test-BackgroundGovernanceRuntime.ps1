param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build/BackgroundGovernanceRuntime-' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh output directory.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName) + @((Join-Path $workspace 'tests/BackgroundGovernanceRuntimeChecks.cs'))
$manifest = @($sources | ForEach-Object { [pscustomobject]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath $_).Hash } })
$frozen = foreach($source in $sources) {
    $dest = Join-Path $OutputDirectory ('source/' + $source.Substring($workspace.Length+1))
    $null = New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent)
    Copy-Item -LiteralPath $source -Destination $dest
    $dest
}
$rsp = Join-Path $OutputDirectory 'sources.rsp'
[IO.File]::WriteAllLines($rsp,[string[]]@($frozen | ForEach-Object { '"' + $_ + '"' }))
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-manifest.json') -Encoding UTF8
$exe = Join-Path $OutputDirectory 'BackgroundGovernanceRuntimeChecks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler -nologo -unsafe+ -warn:0 -target:exe -platform:x64 -codepage:65001 '-main:PaviseApp.BackgroundGovernanceRuntimeChecks' -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('-out:'+$exe) ('@'+$rsp) *> (Join-Path $OutputDirectory 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $OutputDirectory 'compile.log'); throw 'Background governance runtime compilation failed' }
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName=$exe; $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
$info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
$child=[Diagnostics.Process]::Start($info)
try {
    $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(60000)) { $child.Kill(); throw 'Owned background governance fixture timed out' }
    $result=$stdout.Result+$stderr.Result
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.txt'),$result)
    Write-Output $result
    if ($child.ExitCode -ne 0) { throw 'Background governance runtime failed; no automatic rerun' }
} finally { $child.Dispose() }
$drift=@($manifest | Where-Object { (Get-FileHash -LiteralPath $_.Path).Hash -ne $_.SHA256 })
if($drift.Count -ne 0) { throw 'Sources changed during checks; retain evidence and verify final source identity' }
Write-Output ('OUTPUT '+$OutputDirectory)
