# @author bdth 2074055628@qq.com
# file: undo every side effect the overhead测试 left on this machine
# ASCII ONLY - PowerShell decodes an unsigned .ps1 with the ANSI codepage.

param([string] $OriginalAutostartExe = 'C:\Users\Star\Desktop\Pavise.exe')

$scratch = 'C:\Users\Star\AppData\Local\Temp\claude\c--Code-Aegis\47ed3f21-d1b4-4678-96c5-8ab818071ea2\scratchpad'
$regBackup = Join-Path $scratch 'pavise-settings-backup.reg'

Write-Output '=== 1. stop any test build still running ==='
try { [System.Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit').Set() | Out-Null } catch { }
for ($i = 0; $i -lt 25; $i++) {
    if (-not (Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 700
}
Get-Process -Name 'FrameBench' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$leftover = Get-Process -Name 'Pavise*' -ErrorAction SilentlyContinue
if ($leftover) { Write-Output "  WARNING: still running: $($leftover.ProcessName -join ', ')" }
else { Write-Output '  all test builds stopped' }

Write-Output '=== 2. restore game profiles ==='
& (Join-Path $PSScriptRoot 'RegisterProbeGame.ps1') -Action remove

Write-Output '=== 3. restore registry settings ==='
if (Test-Path $regBackup) {
    reg delete "HKCU\Software\Pavise" /f | Out-Null
    reg import $regBackup 2>&1 | Out-Null
    $k = Get-ItemProperty 'HKCU:\Software\Pavise'
    Write-Output "  GameModeOn  = $($k.GameModeOn)"
    Write-Output "  TameOn      = $($k.TameOn)"
    Write-Output "  PowerPlanOn = $($k.PowerPlanOn)"
    Write-Output "  AutostartExe= $($k.AutostartExe)"
}
else { Write-Output '  NO BACKUP FOUND - registry left as is' }

Write-Output '=== 4. restore autostart task target ==='
$task = schtasks /query /tn "Pavise" /xml 2>$null
if ($LASTEXITCODE -eq 0) {
    $current = ($task | Select-String -Pattern '<Command>').ToString().Trim()
    Write-Output "  current: $current"
    if ($current -notmatch 'Pavise\.(base|fix|selftest|dev|wl)') {
        Write-Output '  task does not point at a test build, left untouched'
    }
    elseif (-not (Test-Path $OriginalAutostartExe)) {
        schtasks /delete /tn "Pavise" /f | Out-Null
        Write-Output "  original exe missing; removed the task pointing at a test build"
    }
    else {
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $start = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
        $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><URI>\Pavise</URI></RegistrationInfo>
  <Principals><Principal id="Author"><UserId>$user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy></Settings>
  <Triggers><LogonTrigger><StartBoundary>$start</StartBoundary></LogonTrigger></Triggers>
  <Actions Context="Author"><Exec><Command>"$OriginalAutostartExe"</Command><Arguments>--autostart</Arguments></Exec></Actions>
</Task>
"@
        $tmp = Join-Path $env:TEMP 'pavise-restore-task.xml'
        [IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
        schtasks /create /tn "Pavise" /xml $tmp /f | Out-Null
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        Set-ItemProperty -Path 'HKCU:\Software\Pavise' -Name 'AutostartExe' -Value $OriginalAutostartExe
        $after = (schtasks /query /tn "Pavise" /xml 2>$null | Select-String -Pattern '<Command>').ToString().Trim()
        Write-Output "  restored: $after"
    }
}
else { Write-Output '  no autostart task registered' }

Write-Output '=== 5. remove test executables ==='
foreach ($exe in @('C:\Code\Aegis\Pavise.base.exe', 'C:\Code\Aegis\Pavise.fix.exe')) {
    if (Test-Path $exe) { Remove-Item $exe -Force; Write-Output "  deleted $exe" }
}

Write-Output '=== done ==='
