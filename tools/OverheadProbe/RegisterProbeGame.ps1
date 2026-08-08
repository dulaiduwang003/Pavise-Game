# @author bdth 2074055628@qq.com
# file: register the frame probe as a Pavise game profile for activation testing

param(
    [Parameter(Mandatory = $true)][ValidateSet('add', 'remove')] [string] $Action,
    [string] $ProbeExe = 'C:\Code\Aegis\tools\OverheadProbe\PaviseFrameProbe.exe'
)

$dataDir = Join-Path $env:APPDATA 'Pavise'
$profilePath = Join-Path $dataDir 'Pavise.profiles.dat'
$backupPath = "$profilePath.perftest.bak"

function ToB64([string] $s) {
    if ($null -eq $s) { $s = '' }
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))
}

if ($Action -eq 'add') {
    if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
    if ((Test-Path $profilePath) -and -not (Test-Path $backupPath)) {
        Copy-Item $profilePath $backupPath -Force
        Write-Output "backed up existing profiles -> $backupPath"
    }
    $root = Split-Path $ProbeExe -Parent
    $entry = [IO.Path]::GetFileNameWithoutExtension($ProbeExe)
    $probeLine = ('P|' + (ToB64 'perftest-frameprobe') + '|' + (ToB64 'Pavise Frame Probe') + '|' +
        (ToB64 $root) + '|' + (ToB64 $ProbeExe) + '|' + (ToB64 $entry))

    $existing = @()
    if (Test-Path $profilePath) {
        $existing = @(Get-Content $profilePath | Where-Object { $_ -ne '' })
    }
    if ($existing.Count -gt 0 -and $existing[0] -eq 'PAVISE_PROFILES_V4') {
        $kept = @($existing | Select-Object -Skip 1 | Where-Object { $_ -notlike ('P|' + (ToB64 'perftest-frameprobe') + '|*') })
        $lines = @('PAVISE_PROFILES_V4') + $kept + $probeLine
        Write-Output "appended probe profile, kept $($kept.Count) existing line(s)"
    }
    else {
        $lines = @('PAVISE_PROFILES_V4', $probeLine)
        Write-Output "created fresh profile file"
    }
    $noBom = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllLines($profilePath, [string[]]$lines, $noBom)
    Write-Output "registered probe game: $ProbeExe"
    Write-Output "  root  = $root"
    Write-Output "  entry = $entry"
}
else {
    if (Test-Path $backupPath) {
        Move-Item $backupPath $profilePath -Force
        Write-Output "restored original profiles from backup"
    }
    elseif (Test-Path $profilePath) {
        Remove-Item $profilePath -Force
        Write-Output "removed test profile file (no prior backup existed)"
    }
    else { Write-Output "nothing to restore" }
}
