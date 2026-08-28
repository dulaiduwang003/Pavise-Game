$ErrorActionPreference = 'Stop'
$warningRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$warningOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseFamilyWarning-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $warningOutput
$warningSources = @(Get-ChildItem -LiteralPath (Join-Path $warningRepo 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$warningSources += Join-Path $warningRepo 'tests\FamilyWarningUiChecks.cs'
$warningCompiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$warningExe = Join-Path $warningOutput 'FamilyWarningUiChecks.exe'
$warningArgs = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/optimize+', '/codepage:65001', '/nowarn:0649', '/define:PAVISE_SELFTEST;PAVISE_UI_TEST;PAVISE_FAMILY_WARNING_BENCH', '/main:PaviseApp.FamilyWarningUiChecks', ('/out:' + $warningExe), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll')
& $warningCompiler @warningArgs @warningSources
if ($LASTEXITCODE -ne 0) { throw 'Family warning bench compilation failed' }
$warningProcess = Start-Process -FilePath $warningExe -ArgumentList ('"' + $warningOutput + '"') -WorkingDirectory $warningOutput -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $warningOutput 'stdout.log') -RedirectStandardError (Join-Path $warningOutput 'stderr.log')
try {
    if (-not $warningProcess.WaitForExit(55000)) { $warningProcess.Kill(); throw 'Owned family warning bench exceeded 55 seconds' }
    Get-Content -LiteralPath (Join-Path $warningOutput 'stdout.log')
    Get-Content -LiteralPath (Join-Path $warningOutput 'stderr.log')
    if ($warningProcess.ExitCode -ne 0) { throw 'Family warning UI checks failed' }
} finally { $warningProcess.Dispose(); Write-Output ('OUTPUT ' + $warningOutput) }
