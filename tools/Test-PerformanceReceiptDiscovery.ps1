param()
$ErrorActionPreference = 'Stop'
$receiptDiscoveryWorkspace = Split-Path $PSScriptRoot -Parent
$receiptDiscoveryOutput = Join-Path $env:TEMP ('Pavise-ReceiptDiscovery-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $receiptDiscoveryOutput
Push-Location $receiptDiscoveryWorkspace
try {
    $receiptDiscoveryCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $receiptDiscoveryInputs = @(Get-ChildItem -LiteralPath (Join-Path $receiptDiscoveryWorkspace 'src') -Recurse -File -Filter '*.cs')
    $receiptDiscoveryInputs += Get-Item -LiteralPath (Join-Path $receiptDiscoveryWorkspace 'tests/PerformanceReceiptDiscoveryRunner.cs')
    $receiptDiscoveryInputs += Get-Item -LiteralPath (Join-Path $receiptDiscoveryWorkspace 'tests/PerformanceIndexOwnershipRunner.cs')
    $receiptDiscoveryBefore = @($receiptDiscoveryInputs | ForEach-Object { [pscustomobject]@{
        Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    $receiptDiscoveryChecks = @(
        [pscustomobject]@{ Name = 'discovery'; Defines = 'PAVISE_SELFTEST;PAVISE_RECEIPT_DISCOVERY_RUNNER';
            Entry = 'PaviseApp.PerformanceReceiptDiscoveryRunner'; Sources = @('-recurse:src\*.cs', 'tests\PerformanceReceiptDiscoveryRunner.cs') },
        [pscustomobject]@{ Name = 'cross-writer'; Defines = 'PAVISE_INDEX_OWNERSHIP_RUNNER';
            Entry = 'PaviseApp.PerformanceIndexOwnershipAudit'; Sources = @('src\Core\Tweaks\PerformanceActionStore.cs', 'tests\PerformanceIndexOwnershipRunner.cs') }
    )
    foreach ($receiptDiscoveryCheck in $receiptDiscoveryChecks) {
        $receiptDiscoveryExe = Join-Path $receiptDiscoveryOutput ($receiptDiscoveryCheck.Name + '.exe')
        & $receiptDiscoveryCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
            ('-define:' + $receiptDiscoveryCheck.Defines) ('-main:' + $receiptDiscoveryCheck.Entry) ('-out:' + $receiptDiscoveryExe) `
            -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
            -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
            -reference:System.Web.Extensions.dll @($receiptDiscoveryCheck.Sources)
        if ($LASTEXITCODE -ne 0) { throw ('Isolated receipt fixture compilation failed: ' + $receiptDiscoveryCheck.Name) }
        $receiptDiscoveryStart = New-Object Diagnostics.ProcessStartInfo
        $receiptDiscoveryStart.FileName = $receiptDiscoveryExe
        $receiptDiscoveryStart.WorkingDirectory = $receiptDiscoveryOutput
        $receiptDiscoveryStart.UseShellExecute = $false
        $receiptDiscoveryStart.CreateNoWindow = $true
        $receiptDiscoveryStart.RedirectStandardOutput = $true
        $receiptDiscoveryStart.RedirectStandardError = $true
        $receiptDiscoveryProcess = New-Object Diagnostics.Process
        $receiptDiscoveryProcess.StartInfo = $receiptDiscoveryStart
        try {
            $null = $receiptDiscoveryProcess.Start()
            $receiptDiscoveryStdout = $receiptDiscoveryProcess.StandardOutput.ReadToEndAsync()
            $receiptDiscoveryStderr = $receiptDiscoveryProcess.StandardError.ReadToEndAsync()
            if (-not $receiptDiscoveryProcess.WaitForExit(30000)) {
                $receiptDiscoveryProcess.Kill()
                throw ('Owned receipt fixture timed out: ' + $receiptDiscoveryCheck.Name)
            }
            $receiptDiscoveryProcess.WaitForExit()
            $receiptDiscoveryResult = $receiptDiscoveryStdout.Result + $receiptDiscoveryStderr.Result
            [IO.File]::WriteAllText((Join-Path $receiptDiscoveryOutput ($receiptDiscoveryCheck.Name + '-result.txt')), $receiptDiscoveryResult)
            Write-Output $receiptDiscoveryResult
            if ($receiptDiscoveryProcess.ExitCode -ne 0) { throw ('Receipt fixture failed: ' + $receiptDiscoveryCheck.Name) }
        } finally { $receiptDiscoveryProcess.Dispose() }
    }
    $receiptDiscoveryChanged = @($receiptDiscoveryBefore | Where-Object {
        -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256 })
    [pscustomobject]@{ SourceUnchanged = $receiptDiscoveryChanged.Count -eq 0; ChangedInputs = $receiptDiscoveryChanged;
        RuntimeStarted = $false; RealRegistryAccess = $false; SystemWrites = $false;
        SettingsStores = 'Transient dictionary and cross-AppDomain shared mock dictionary'; OutputDirectory = $receiptDiscoveryOutput } |
        ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $receiptDiscoveryOutput 'verification.json') -Encoding utf8
    Write-Output ('OUTPUT ' + $receiptDiscoveryOutput)
    if ($receiptDiscoveryChanged.Count -ne 0) { throw 'Inputs changed; rerun after source freeze.' }
} finally { Pop-Location }
