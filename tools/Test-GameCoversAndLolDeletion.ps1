param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$libraryChecksWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $env:TEMP ('Pavise-GameCovers-LolDeletion-' + [Guid]::NewGuid().ToString('N'))
}
$libraryChecksOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $libraryChecksOutput -Force
$libraryChecksCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $libraryChecksCompiler)) { throw '.NET Framework x64 compiler is unavailable' }

# Compile a retained snapshot so concurrent UI work cannot alter the code under test.
$libraryChecksSources = @(Get-ChildItem -LiteralPath (Join-Path $libraryChecksWorkspace 'src') -Filter '*.cs' -Recurse -File | ForEach-Object { $_.FullName })
$libraryChecksSources += Join-Path $libraryChecksWorkspace 'tests/GameCoverStoreChecks.cs'
$libraryChecksSources += Join-Path $libraryChecksWorkspace 'tests/LolDeletionUiChecks.cs'
$libraryChecksInputs = @($libraryChecksSources | ForEach-Object {
    $original = [IO.Path]::GetFullPath($_)
    $relative = $original.Substring($libraryChecksWorkspace.Length + 1)
    $frozen = Join-Path (Join-Path $libraryChecksOutput 'frozen-inputs') $relative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
    [IO.File]::WriteAllBytes($frozen, [IO.File]::ReadAllBytes($original))
    [pscustomobject]@{ Relative = $relative; Original = $original; Frozen = $frozen; SHA256 = (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$libraryChecksInputs | Export-Csv -LiteralPath (Join-Path $libraryChecksOutput 'input-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $libraryChecksOutput 'Test-GameCoversAndLolDeletion.ps1'), [IO.File]::ReadAllBytes($PSCommandPath))

function Invoke-LibraryCheckHidden([string]$File, [string]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File; $start.Arguments = $Arguments; $start.WorkingDirectory = $libraryChecksOutput
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $start.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    $child = New-Object Diagnostics.Process; $child.StartInfo = $start
    try {
        $null = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(60000)) {
            # Only this script's compiler or isolated test child may be stopped on timeout.
            $child.Kill(); $child.WaitForExit(); throw ('Isolated check timed out: ' + $File)
        }
        $child.WaitForExit()
        [pscustomobject]@{ Exit = $child.ExitCode; Text = $stdout.Result + $stderr.Result }
    } finally { $child.Dispose() }
}

$libraryChecksCases = @(
    [pscustomobject]@{ Name = 'GameCoverStoreChecks'; Define = 'PAVISE_COVER_STORE_CHECKS'; Expected = 'PASS GameCoverStore assertions=' },
    [pscustomobject]@{ Name = 'LolDeletionUiChecks'; Define = 'PAVISE_LOL_DELETION_UI_CHECKS'; Expected = 'PASS LolDeletionUiChecks assertions=' }
)
foreach ($case in $libraryChecksCases) {
    $caseExe = Join-Path $libraryChecksOutput ($case.Name + '.exe')
    $caseRsp = Join-Path $libraryChecksOutput ($case.Name + '.rsp')
    $caseArgs = @('/nologo', '/unsafe+', '/target:exe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', '/nowarn:0162,0649',
        ('/define:PAVISE_SELFTEST;' + $case.Define), ('/main:PaviseApp.' + $case.Name), ('/out:"' + $caseExe + '"'),
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        '/reference:System.Management.dll', '/reference:System.Xml.dll', '/reference:System.Security.dll')
    $caseArgs += @($libraryChecksInputs | ForEach-Object { '"' + $_.Frozen + '"' })
    [IO.File]::WriteAllLines($caseRsp, $caseArgs, (New-Object Text.UTF8Encoding($false)))
    $compile = Invoke-LibraryCheckHidden $libraryChecksCompiler ('@"' + $caseRsp + '"')
    [IO.File]::WriteAllText((Join-Path $libraryChecksOutput ($case.Name + '-compile.log')), $compile.Text)
    if ($compile.Exit -ne 0) { throw ($case.Name + ' did not compile: ' + $compile.Text) }
    $run = Invoke-LibraryCheckHidden $caseExe ''
    [IO.File]::WriteAllText((Join-Path $libraryChecksOutput ($case.Name + '-result.txt')), $run.Text)
    Write-Output $run.Text
    if ($run.Exit -ne 0 -or -not $run.Text.Contains($case.Expected)) { throw ($case.Name + ' failed') }
}
$changed = @($libraryChecksInputs | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256 })
[pscustomobject]@{ TestedSourceCount = $libraryChecksInputs.Count; WorkspaceMatchesSnapshot = ($changed.Count -eq 0); ChangedSources = @($changed | ForEach-Object { $_.Relative }); ProductStarted = $false; WindowsShown = $false } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $libraryChecksOutput 'verification.json') -Encoding UTF8
Write-Output ('OUTPUT ' + $libraryChecksOutput)
if ($changed.Count -ne 0) { Write-Warning 'Workspace changed during checks; results apply to retained frozen-inputs only.' }
