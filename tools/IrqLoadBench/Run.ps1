$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$irqRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$irqOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseIrqLoad-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $irqOutput
$irqTests = @('SelfTests.IrqCoreLoad.cs', 'SelfTests.IrqObservation.cs', 'SelfTests.IrqVerdict.cs', 'SelfTests.IrqStatus.cs', 'SelfTests.IrqDisplay.cs', 'IrqCoreLoadUiChecks.cs')
$irqInputs = @(Get-ChildItem -LiteralPath (Join-Path $irqRoot 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$irqInputs += @($irqTests | ForEach-Object { Join-Path $irqRoot ('tests\' + $_) })
$irqInputs += Join-Path $PSScriptRoot 'IrqLoadBench.cs'
$irqProtected = @($irqInputs) + @($PSCommandPath)
if (Test-Path -LiteralPath (Join-Path $irqRoot 'Pavise.exe')) { $irqProtected += Join-Path $irqRoot 'Pavise.exe' }
$irqBefore = @($irqProtected | ForEach-Object { [pscustomobject]@{Path=$_; Hash=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$irqCompiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$irqExe = Join-Path $irqOutput 'IrqLoadBench.exe'
$irqArgs = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/optimize+', '/codepage:65001', '/nowarn:0649',
    '/define:PAVISE_SELFTEST;PAVISE_IRQ_BENCH;PAVISE_UI_TEST', '/main:PaviseApp.IrqLoadBench', ('/out:' + $irqExe),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll', '/reference:System.Xml.dll', '/reference:System.Web.Extensions.dll')
& $irqCompiler @irqArgs @irqInputs 2>&1 | Tee-Object -FilePath (Join-Path $irqOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { throw ('IRQ bench compilation failed. ' + $irqOutput) }
$irqProcess = $null
try {
    $irqProcess = Start-Process -FilePath $irqExe -ArgumentList ('"' + $irqOutput + '"') -WorkingDirectory $irqOutput -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $irqOutput 'console.log') -RedirectStandardError (Join-Path $irqOutput 'stderr.log')
    $irqOwnedHandle = $irqProcess.Handle
    if (-not $irqProcess.WaitForExit(45000)) {
        if ($irqProcess.Handle -eq $irqOwnedHandle -and -not $irqProcess.HasExited) { $irqProcess.Kill() }
        throw 'The isolated IRQ bench exceeded 45 seconds.'
    }
    Get-Content -LiteralPath (Join-Path $irqOutput 'console.log')
    Get-Content -LiteralPath (Join-Path $irqOutput 'stderr.log')
    if ($irqProcess.ExitCode -ne 0) { throw 'IRQ load tests failed.' }
}
finally {
    if ($null -ne $irqProcess) { $irqProcess.Dispose() }
    $irqChanged = @($irqBefore | Where-Object { -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash })
    [pscustomobject]@{ InputsUnchanged=($irqChanged.Count -eq 0); Inputs=$irqBefore; Changed=$irqChanged; ApplicationRun=$false; ActualEtw=$false; ActualPdh=$false; Settings='transient'; Output=$irqOutput } | ConvertTo-Json -Depth 5 | Out-File -LiteralPath (Join-Path $irqOutput 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $irqOutput)
    if ($irqChanged.Count -ne 0) { throw 'Inputs changed during IRQ tests; rerun with frozen inputs.' }
}
