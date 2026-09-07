param([ValidateSet('all','priority','power-inputs','read-cost','auto-gpu-filter')][string]$Arm = 'all')
$ErrorActionPreference = 'Stop'
$reviewRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$reviewRun = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-NegativeReview-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $reviewRun
$reviewExe = Join-Path $reviewRun 'Pavise.ReviewBench.exe'
$reviewSources = @(Get-ChildItem -LiteralPath (Join-Path $reviewRoot 'src') -Recurse -File -Filter '*.cs' | Sort-Object FullName)
$reviewSources += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'ReviewBench.cs')
$reviewProtected = @($reviewSources) + @(Get-ChildItem -LiteralPath (Join-Path $reviewRoot 'tests') -File -Filter '*.cs')
if (Test-Path -LiteralPath (Join-Path $reviewRoot 'Pavise.exe')) { $reviewProtected += Get-Item -LiteralPath (Join-Path $reviewRoot 'Pavise.exe') }
$reviewBefore = @($reviewProtected | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
$reviewBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reviewRun 'source-hashes.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReviewBench.cs') -Destination (Join-Path $reviewRun 'ReviewBench.source.cs')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $reviewRun 'Run-Review.source.ps1')

function Get-ReviewSettingsHash {
    $reviewKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Pavise')
    try {
        if (-not $reviewKey) { return 'absent' }
        $reviewValues = foreach ($reviewName in @($reviewKey.GetValueNames() | Sort-Object)) {
            [pscustomobject]@{Name=$reviewName;Kind=[string]$reviewKey.GetValueKind($reviewName);Value=$reviewKey.GetValue($reviewName)}
        }
        $reviewBytes = [Text.Encoding]::UTF8.GetBytes(($reviewValues | ConvertTo-Json -Depth 5 -Compress))
        $reviewHasher = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($reviewHasher.ComputeHash($reviewBytes)).Replace('-','') }
        finally { $reviewHasher.Dispose() }
    } finally { if ($reviewKey) { $reviewKey.Dispose() } }
}

$reviewSettings = Get-ReviewSettingsHash
$reviewPlan = (& powercfg /getactivescheme | Out-String).Trim()
$reviewMetadata = [ordered]@{
    StartedUtc=[DateTime]::UtcNow.ToString('o'); Arm=$Arm
    Scope='Owned-process scheduling; synthetic production decisions; read-only process/PDH probes. No game FPS or system tuning.'
    CPU=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    OS=Get-CimInstance Win32_OperatingSystem | Select-Object Caption,BuildNumber,TotalVisibleMemorySize,FreePhysicalMemory
    ActivePlan=$reviewPlan; SettingsFingerprint=$reviewSettings
    GPU=(& nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu,power.limit --format=csv,noheader | Out-String).Trim()
}
$reviewMetadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reviewRun 'metadata.json') -Encoding UTF8
Write-Output "OUTPUT $reviewRun"
$reviewCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$reviewArguments = @('-nologo','-target:exe','-platform:x64','-optimize+','-codepage:65001','-define:PAVISE_PERFLAB',
    '-main:PaviseApp.OptimizationReviewBench',"-out:$reviewExe",'-reference:System.dll','-reference:System.Core.dll',
    '-reference:System.Drawing.dll','-reference:System.Windows.Forms.dll','-reference:System.Management.dll','-reference:System.Xml.dll')
& $reviewCompiler @reviewArguments @($reviewSources.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Review bench compilation failed' }
$reviewOutcomes = @()
try {
    $reviewArms = if ($Arm -eq 'all') { @('power-inputs','auto-gpu-filter','read-cost','priority') } else { @($Arm) }
    foreach ($reviewLabel in $reviewArms) {
        $reviewCsv = Join-Path $reviewRun ($reviewLabel + '.csv')
        $reviewErr = Join-Path $reviewRun ($reviewLabel + '.stderr.txt')
        $reviewProcess = Start-Process -FilePath $reviewExe -ArgumentList @($reviewLabel,('"' + $reviewRun + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $reviewCsv -RedirectStandardError $reviewErr
        try {
            if (-not $reviewProcess.WaitForExit(170000)) {
                $reviewProcess.Kill(); $null = $reviewProcess.WaitForExit(2000)
                throw "Review arm $reviewLabel timed out"
            }
            $reviewOutcomes += [pscustomobject]@{Arm=$reviewLabel;ExitCode=$reviewProcess.ExitCode}
            if ($reviewProcess.ExitCode -ne 0) { throw "Review arm failed: $(Get-Content -LiteralPath $reviewErr -Raw)" }
            Get-Content -LiteralPath $reviewCsv
        } finally { $reviewProcess.Dispose() }
    }
} finally {
    $reviewAfter = @($reviewProtected | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    $reviewVerification = [ordered]@{
        EndedUtc=[DateTime]::UtcNow.ToString('o')
        SettingsUnchanged=($reviewSettings -eq (Get-ReviewSettingsHash))
        ActivePlanUnchanged=($reviewPlan -eq ((& powercfg /getactivescheme | Out-String).Trim()))
        SourcesTestsProductionUnchanged=(($reviewBefore | ConvertTo-Json -Compress) -eq ($reviewAfter | ConvertTo-Json -Compress))
        Outcomes=$reviewOutcomes
        GPU=(& nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu,power.limit --format=csv,noheader | Out-String).Trim()
        RemainingBenchProcessIds=@(Get-Process | Where-Object { try { $_.Path -eq $reviewExe } catch { $false } } | Select-Object -ExpandProperty Id)
    }
    $reviewVerification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reviewRun 'verification.json') -Encoding UTF8
    $reviewVerification | ConvertTo-Json -Depth 5 -Compress
    if (-not $reviewVerification.SettingsUnchanged -or -not $reviewVerification.ActivePlanUnchanged -or -not $reviewVerification.SourcesTestsProductionUnchanged -or $reviewVerification.RemainingBenchProcessIds.Count -gt 0) {
        throw 'Isolation verification failed; inspect retained artifacts'
    }
}
