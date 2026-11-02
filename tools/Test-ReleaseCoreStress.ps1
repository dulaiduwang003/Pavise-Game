param([switch]$Soak, [string]$Executable = 'build/Pavise.exe')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
function Fingerprint-Settings {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PaviseAuto')
    if ($null -eq $key) { return 'missing' }
    try {
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{ Name = $_; Kind = $key.GetValueKind($_).ToString(); Value = $key.GetValue($_) }
        })
        $bytes = [Text.Encoding]::UTF8.GetBytes(($values | ConvertTo-Json -Compress -Depth 8))
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') } finally { $sha.Dispose() }
    } finally { $key.Dispose() }
}
function Fingerprint-UserFiles {
    $data = Join-Path $env:APPDATA 'PaviseAuto'
    return (@('Pavise.profiles.dat', 'Pavise.whitelist.txt', 'Pavise.autoignore.txt', 'Pavise.renderer-observations.dat') | ForEach-Object {
        $file = Join-Path $data $_
        if (Test-Path -LiteralPath $file) { $_ + ':' + (Get-FileHash -LiteralPath $file).Hash } else { $_ + ':missing' }
    }) -join '|'
}
Push-Location $workspace
try {
    $exe = if ([IO.Path]::IsPathRooted($Executable)) { $Executable } else { Join-Path $workspace $Executable }
    if (!(Test-Path -LiteralPath $exe)) { throw 'Build the production executable first.' }
    $beforeSettings = Fingerprint-Settings
    $beforeFiles = Fingerprint-UserFiles
    $beforePower = (& powercfg /getactivescheme | Out-String).Trim()
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler -nologo -target:exe -platform:x64 -optimize+ -out:build\Pavise.release-core-check.exe tests\ReleaseCorePlacementChecks.cs tests\ReleaseCoreLifecycleChecks.cs
    if ($LASTEXITCODE -ne 0) { throw 'Release runner compilation failed.' }
    $runs = @()
    $cases = @(101, 202, 303, 404, 505 | ForEach-Object { [pscustomobject]@{ Seed = $_; Mode = '--stress'; Timeout = 60 } })
    if ($Soak) { $cases += [pscustomobject]@{ Seed = 7301; Mode = '--soak'; Timeout = 180 } }
    foreach ($case in $cases) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = Join-Path $workspace 'build/Pavise.release-core-check.exe'
        $info.Arguments = '"' + $exe + '" ' + $case.Mode + ' ' + $case.Seed
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $process = New-Object Diagnostics.Process; $process.StartInfo = $info
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            $null = $process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit($case.Timeout * 1000)) {
                $process.Kill(); $process.WaitForExit(); throw ('Test timed out: ' + $case.Seed)
            }
            $process.WaitForExit(); $timer.Stop()
            $log = 'build/release-suite-' + $case.Seed + '.txt'
            [IO.File]::WriteAllText((Join-Path $workspace $log), $stdout.Result + $stderr.Result)
            if ($process.ExitCode -ne 0) { throw ('Release test failed; inspect ' + $log) }
            $runs += [pscustomobject]@{ Seed = $case.Seed; Mode = $case.Mode; Seconds = $timer.Elapsed.TotalSeconds; Log = $log; Passed = $true }
            Write-Output ('PASS release seed=' + $case.Seed + ' ' + $case.Mode + ' elapsed=' + $timer.Elapsed.TotalSeconds.ToString('F2') + 's')
        } finally { $process.Dispose() }
    }
    $afterPower = (& powercfg /getactivescheme | Out-String).Trim()
    $summary = [pscustomobject]@{
        ExecutableSha256 = (Get-FileHash -LiteralPath $exe).Hash
        SettingsUnchanged = ((Fingerprint-Settings) -eq $beforeSettings)
        UserFilesUnchanged = ((Fingerprint-UserFiles) -eq $beforeFiles)
        PowerPlanUnchanged = ($afterPower -eq $beforePower)
        Runs = $runs
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath 'build/release-stress-suite.json' -Encoding utf8
    if (!$summary.SettingsUnchanged -or !$summary.UserFilesUnchanged -or !$summary.PowerPlanUnchanged) { throw 'User state changed during tests; inspect the summary before making any restoration decision.' }
    Write-Output 'PASS production settings, user data files and active power scheme unchanged'
} finally { Pop-Location }
