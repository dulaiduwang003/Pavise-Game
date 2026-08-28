$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$navRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$navOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseNavigation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $navOutput
$navSources = @(Get-ChildItem -LiteralPath (Join-Path $navRepo 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$navSources += Join-Path $navRepo 'tests\NavigationUiChecks.cs'
$navBefore = @($navSources | ForEach-Object { [pscustomobject]@{ Path = $_; Hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$navCompiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$navExe = Join-Path $navOutput 'NavigationUiChecks.exe'
$navArgs = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/optimize+', '/codepage:65001', '/nowarn:0649',
    '/define:PAVISE_SELFTEST;PAVISE_NAV_BENCH', '/main:PaviseApp.NavigationUiChecks', ('/out:' + $navExe),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll')
& $navCompiler @navArgs @navSources 2>&1 | Tee-Object -FilePath (Join-Path $navOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { throw ('Navigation compilation failed: ' + $navOutput) }
$navProcess = $null
try {
    $navProcess = Start-Process -FilePath $navExe -ArgumentList ('"' + $navOutput + '"') -WorkingDirectory $navOutput -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $navOutput 'console.log') -RedirectStandardError (Join-Path $navOutput 'stderr.log')
    if (-not $navProcess.WaitForExit(55000)) { $navProcess.Kill(); throw 'Owned navigation UI bench exceeded 55 seconds.' }
    Get-Content -LiteralPath (Join-Path $navOutput 'console.log')
    Get-Content -LiteralPath (Join-Path $navOutput 'stderr.log')
    if ($navProcess.ExitCode -ne 0) { throw 'Navigation UI tests failed.' }
}
finally {
    if ($null -ne $navProcess) { $navProcess.Dispose() }
    $navChanged = @($navBefore | Where-Object { -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash })
    [pscustomobject]@{ InputsUnchanged = ($navChanged.Count -eq 0); Changed = $navChanged; Settings = 'transient'; RuntimeStarted = $false; Output = $navOutput } | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $navOutput 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $navOutput)
    if ($navChanged.Count -ne 0) { throw 'Inputs changed during navigation tests.' }
}
