$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$navRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$navOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseNavigation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $navOutput
$navSources = @(Get-ChildItem -LiteralPath (Join-Path $navRepo 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$navSources += Join-Path $navRepo 'tests\NavigationUiChecks.cs'
$navSources += Join-Path $navRepo 'tests\TransitionUiChecks.cs'
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
    # 带超时那个重载会在重定向流排空的时候把 ExitCode 留成旧值
    $navProcess.WaitForExit()
    $navProcess.Refresh()
    $navConsole = @(Get-Content -LiteralPath (Join-Path $navOutput 'console.log'))
    $navErrors = @(Get-Content -LiteralPath (Join-Path $navOutput 'stderr.log'))
    $navConsole
    $navErrors
    $navExitCode = $navProcess.ExitCode
    # 有些 PowerShell 宿主重定向跑完之后 返回的 Process 代理里 ExitCode 还是 null
    # runner 要等所有断言都跑完才打这个标记 打完立刻返回 0
    if ($null -eq $navExitCode) {
        $navPassed = @($navConsole | Where-Object { $_ -like 'PASS navigation assertions=*' }).Count -eq 1
        $navExitCode = if ($navPassed -and $navErrors.Count -eq 0) { 0 } else { 1 }
    }
    if ($navExitCode -ne 0) { throw ('Navigation UI tests failed with exit code ' + $navExitCode + '.') }
}
finally {
    if ($null -ne $navProcess) { $navProcess.Dispose() }
    $navChanged = @($navBefore | Where-Object { -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash })
    [pscustomobject]@{ InputsUnchanged = ($navChanged.Count -eq 0); Changed = $navChanged; Settings = 'transient'; RuntimeStarted = $false; Output = $navOutput } | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $navOutput 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $navOutput)
    if ($navChanged.Count -ne 0) { throw 'Inputs changed during navigation tests.' }
}
