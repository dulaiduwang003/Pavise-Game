$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$libraryRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$libraryOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseLibraryUi-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $libraryOutput
$librarySources = @(Get-ChildItem -LiteralPath (Join-Path $libraryRepo 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$librarySources += Join-Path $libraryRepo 'tests\LibraryFamilyUiChecks.cs'
$libraryBefore = @($librarySources | ForEach-Object { [pscustomobject]@{ Path = $_; Hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$libraryCompiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$libraryExe = Join-Path $libraryOutput 'LibraryFamilyUiChecks.exe'
$libraryArgs = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/optimize+', '/codepage:65001', '/nowarn:0649',
    '/define:PAVISE_SELFTEST;PAVISE_UI_TEST;PAVISE_LIBRARY_BENCH', '/main:PaviseApp.LibraryFamilyUiChecks', ('/out:' + $libraryExe),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll')
& $libraryCompiler @libraryArgs @librarySources 2>&1 | Tee-Object -FilePath (Join-Path $libraryOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { throw ('Library UI compilation failed: ' + $libraryOutput) }
$libraryProcess = $null
try {
    $libraryProcess = Start-Process -FilePath $libraryExe -ArgumentList ('"' + $libraryOutput + '"') -WorkingDirectory $libraryOutput -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $libraryOutput 'console.log') -RedirectStandardError (Join-Path $libraryOutput 'stderr.log')
    if (-not $libraryProcess.WaitForExit(55000)) { $libraryProcess.Kill(); throw 'Owned library UI bench exceeded 55 seconds.' }
    Get-Content -LiteralPath (Join-Path $libraryOutput 'console.log')
    Get-Content -LiteralPath (Join-Path $libraryOutput 'stderr.log')
    if ($libraryProcess.ExitCode -ne 0) { throw 'Library UI tests failed.' }
}
finally {
    if ($null -ne $libraryProcess) { $libraryProcess.Dispose() }
    $libraryChanged = @($libraryBefore | Where-Object { -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash })
    [pscustomobject]@{ InputsUnchanged = ($libraryChanged.Count -eq 0); Changed = $libraryChanged; Settings = 'transient'; RuntimeStarted = $false; WindowsShown = $false; ScansStarted = $false; Snapshots = 0; Output = $libraryOutput } | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $libraryOutput 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $libraryOutput)
    if ($libraryChanged.Count -ne 0) { throw 'Inputs changed during library UI tests.' }
}
