param([switch]$SpreadControlOnly, [switch]$ExtrasOnly, [switch]$StoreOnly)
$ErrorActionPreference = 'Stop'
$riskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$riskRun = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-RiskBench-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $riskRun | Out-Null
$riskExe = Join-Path $riskRun 'Pavise.OptimizationRiskBench.exe'
$riskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$riskSources = @(Get-ChildItem -LiteralPath (Join-Path $riskRoot 'src') -Filter '*.cs' -Recurse -File | Sort-Object FullName)
$riskSources += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'RiskBench.cs')
$riskSources += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'ExtraBench.cs')
$riskSourceBefore = @($riskSources | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
$riskSourceBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $riskRun 'source-hashes.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RiskBench.cs') -Destination (Join-Path $riskRun 'RiskBench.source.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ExtraBench.cs') -Destination (Join-Path $riskRun 'ExtraBench.source.cs')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $riskRun 'Run.source.ps1')

function Get-RiskSettingsFingerprint {
    $riskRegistry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Pavise')
    try {
        if (-not $riskRegistry) { return 'absent' }
        # Hashes serve only as evidence, actual values including paths and session history are never written to disk
        $riskParts = foreach ($riskValueName in @($riskRegistry.GetValueNames() | Sort-Object)) {
            [pscustomobject]@{Name=$riskValueName;Kind=[string]$riskRegistry.GetValueKind($riskValueName);Value=$riskRegistry.GetValue($riskValueName)}
        }
        $riskBytes = [Text.Encoding]::UTF8.GetBytes(($riskParts | ConvertTo-Json -Depth 5 -Compress))
        $riskHasher = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($riskHasher.ComputeHash($riskBytes)).Replace('-','') }
        finally { $riskHasher.Dispose() }
    } finally { if ($riskRegistry) { $riskRegistry.Dispose() } }
}

function Invoke-RiskArm([string]$Label, [string]$Arguments) {
    Write-Output "RUN $Label"
    $riskOut = Join-Path $riskRun ($Label + '.csv')
    $riskErr = Join-Path $riskRun ($Label + '.stderr.txt')
    $riskProcess = Start-Process -FilePath $riskExe -ArgumentList $Arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $riskOut -RedirectStandardError $riskErr
    try {
        if (-not $riskProcess.WaitForExit(50000)) {
            # Only the Process object created above is eligible for emergency termination
            $riskProcess.Kill()
            $riskProcess.WaitForExit()
            throw "$Label exceeded its bounded lifetime"
        }
        if ($riskProcess.ExitCode -ne 0) {
            throw "$Label failed ($($riskProcess.ExitCode)): $(Get-Content -LiteralPath $riskErr -Raw)"
        }
        Get-Content -LiteralPath $riskOut
    } finally { $riskProcess.Dispose() }
}

Write-Output "OUTPUT $riskRun"
$riskSettingsBefore = Get-RiskSettingsFingerprint
$riskSchemeBefore = (& powercfg /getactivescheme | Out-String).Trim()
$riskGpuBefore = (& nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu,power.draw,power.limit,power.default_limit,power.max_limit --format=csv,noheader | Out-String).Trim()
$riskOs = Get-CimInstance Win32_OperatingSystem
$riskMeta = [ordered]@{
    StartedUtc=[DateTime]::UtcNow.ToString('o')
    OS=$riskOs.Caption; Build=$riskOs.BuildNumber
    TotalMemoryGiB=[math]::Round($riskOs.TotalVisibleMemorySize/1MB,3)
    AvailableMemoryGiB=[math]::Round($riskOs.FreePhysicalMemory/1MB,3)
    CPU=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    BatteryCount=@(Get-CimInstance Win32_Battery).Count
    Disks=@(Get-PhysicalDisk | Select-Object FriendlyName,BusType,MediaType)
    GpuBefore=$riskGpuBefore; ActivePlanBefore=$riskSchemeBefore
    SettingsFingerprintBefore=$riskSettingsBefore
    Scope='Current-source pure functions and owned-process counterexamples; no game FPS test; no system tuning'
    SpreadControlOnly=[bool]$SpreadControlOnly
    ExtrasOnly=[bool]$ExtrasOnly
    StoreOnly=[bool]$StoreOnly
}
$riskMeta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $riskRun 'metadata.json') -Encoding UTF8
$riskArgs = @('-nologo','-target:exe','-platform:x64','-optimize+','-codepage:65001','-define:PAVISE_PERFLAB',
    '-main:PaviseApp.OptimizationRiskBench',"-out:$riskExe",'-reference:System.dll','-reference:System.Core.dll',
    '-reference:System.Drawing.dll','-reference:System.Windows.Forms.dll','-reference:System.Management.dll','-reference:System.Xml.dll')
& $riskCompiler @riskArgs @($riskSources.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Bench compilation failed' }
try {
    Invoke-RiskArm 'baseline-before' 'baseline'
    if (-not $SpreadControlOnly -and -not $ExtrasOnly -and -not $StoreOnly) {
        Invoke-RiskArm 'policy' 'policy'
        Invoke-RiskArm 'trim' 'trim'
    }
    $riskOrder = if ($ExtrasOnly -or $StoreOnly) { @() }
        elseif ($SpreadControlOnly) { @('baseline','boost','boost','baseline') }
        else { @('baseline','boost','boost','baseline','baseline','boost','boost','baseline') }
    for ($riskIndex=0; $riskIndex -lt $riskOrder.Count; $riskIndex++) {
        $riskSuffix = if ($SpreadControlOnly) { ' spread' } else { '' }
        Invoke-RiskArm ('lane-{0:D2}-{1}' -f $riskIndex,$riskOrder[$riskIndex]) ('lane ' + $riskOrder[$riskIndex] + $riskSuffix)
    }
    if ($ExtrasOnly) {
        foreach ($riskMode in 'audio') {
            $riskExtraOrder = @('baseline','active','active','baseline')
            for ($riskIndex=0; $riskIndex -lt $riskExtraOrder.Count; $riskIndex++) {
                Invoke-RiskArm ('{0}-{1:D2}-{2}' -f $riskMode,$riskIndex,$riskExtraOrder[$riskIndex]) ($riskMode + ' ' + $riskExtraOrder[$riskIndex])
            }
        }
    }
    if ($StoreOnly) { Invoke-RiskArm 'store-lock' 'store-lock' }
    Invoke-RiskArm 'baseline-after' 'baseline'
} finally {
    $riskSettingsAfter = Get-RiskSettingsFingerprint
    $riskSchemeAfter = (& powercfg /getactivescheme | Out-String).Trim()
    $riskSourceAfter = @($riskSources | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash)
    $riskVerification = [ordered]@{
        EndedUtc=[DateTime]::UtcNow.ToString('o')
        SettingsUnchanged=($riskSettingsBefore -eq $riskSettingsAfter)
        SettingsFingerprintAfter=$riskSettingsAfter
        ActivePlanUnchanged=($riskSchemeBefore -eq $riskSchemeAfter)
        ActivePlanAfter=$riskSchemeAfter
        SourcesUnchanged=(($riskSourceBefore | ConvertTo-Json -Compress) -eq ($riskSourceAfter | ConvertTo-Json -Compress))
        GpuAfter=(& nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu,power.draw,power.limit,power.default_limit,power.max_limit --format=csv,noheader | Out-String).Trim()
        RemainingBenchProcessIds=@(Get-Process | Where-Object { try { $_.Path -eq $riskExe } catch { $false } } | Select-Object -ExpandProperty Id)
    }
    $riskVerification | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $riskRun 'verification.json') -Encoding UTF8
    $riskVerification | ConvertTo-Json -Depth 4 -Compress
}
