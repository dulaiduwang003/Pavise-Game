@echo off
rem @author bdth 2074055628@qq.com
rem file rescue for a machine that hard-crashes MCE after deep system writes
rem ASCII ONLY above the marker line and CRLF line endings only cmd
rem decodes this file with the console startup codepage 936 or 65001
rem any non-ASCII byte up here shifts the parser and bare-LF endings
rem break batch parsing outright All logic and Chinese text live in the
rem PowerShell block below read back as UTF-8 and never parsed by cmd
set "PAVISE_UNINSTALL_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$m=[IO.File]::ReadAllText($env:PAVISE_UNINSTALL_SELF,(New-Object Text.UTF8Encoding($false)));$i=$m.IndexOf([string][char]10+'#PSBEGIN');Invoke-Expression $m.Substring($i)"
exit /b

#PSBEGIN
$ErrorActionPreference = 'SilentlyContinue'
$self = $env:PAVISE_UNINSTALL_SELF

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Host '需要管理员权限，正在请求提升...'
    Start-Process cmd -Verb RunAs -ArgumentList '/c', ('"' + $self + '"')
    exit
}

Write-Host '============================================================'
Write-Host ' Pavise 急救（系统环境改动后死机重启的机器专用）'
Write-Host '============================================================'
Write-Host ''
Write-Host ' 顺序：先把日志、蓝屏记录、电源与启动配置导出到桌面；'
Write-Host ' 再退出 Pavise，按收据还原系统改动；收据缺失的项按 Windows'
Write-Host ' 默认值复位；删除托管电源方案并把全部电源方案恢复出厂；'
Write-Host ' 复位显卡频率锁定与功耗墙；最后删除 Pavise 的设置与数据。'
Write-Host ''
Write-Host ' 跑完必须重启一次。之后请把桌面上生成的 Pavise-Rescue 文件夹' -ForegroundColor Cyan
Write-Host ' 整个打包发给作者。' -ForegroundColor Cyan
Write-Host ''
Write-Host ' 本脚本会删除 Pavise 的游戏库、白名单和全部设置。' -ForegroundColor Yellow
Write-Host ' 不会改动 HAGS、VBS 与 hypervisor 的现状，除非收据里有它们。' -ForegroundColor Yellow
Write-Host ''
$answer = Read-Host '输入 Y 并回车开始，其它任意输入取消'
if ($answer -ne 'Y' -and $answer -ne 'y') { Write-Host '已取消。'; Start-Sleep 1; exit }

# ---- receipts live in HKCU\Software\Pavise strings are REG_SZ flags are DWORD ----
$hive = 'HKCU:\Software\Pavise'
$Absent = '__pavise_absent__'
$US = [string][char]31
$script:restored = 0
$script:failed = @()

function Rc([string]$name) {
    $v = (Get-ItemProperty -Path $hive -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -eq $v) { return '' }
    return [string]$v
}

function Note([string]$what, [bool]$ok) {
    if ($ok) { $script:restored++; Write-Host ('       还原 ' + $what) }
    else { $script:failed += $what; Write-Host ('       失败 ' + $what) -ForegroundColor Yellow }
}

# ReversibleReg slot original [US applied] original is __pavise_absent__ b<base64> or =<value>
function Restore-Slot([string]$slot, [string]$path, [string]$name, [string]$kind, [string]$what) {
    $raw = Rc $slot
    if ($raw -eq '') { return }
    $orig = $raw.Split([char]31)[0]
    $ok = $true
    try {
        if ($orig -eq $Absent) {
            if (Test-Path $path) { Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue }
        } else {
            if (-not (Test-Path $path)) { New-Item -Path $path -Force -ErrorAction Stop | Out-Null }
            if ($orig.StartsWith('b')) {
                $bytes = [Convert]::FromBase64String($orig.Substring(1))
                Set-ItemProperty -Path $path -Name $name -Value $bytes -Type Binary -ErrorAction Stop
            } elseif ($orig.StartsWith('=')) {
                $v = $orig.Substring(1)
                if ($kind -eq 'DWord') {
                    $n = [int64]$v
                    if ($n -gt 2147483647) { $n -= 4294967296 }
                    Set-ItemProperty -Path $path -Name $name -Value ([int32]$n) -Type DWord -ErrorAction Stop
                } else {
                    Set-ItemProperty -Path $path -Name $name -Value $v -Type String -ErrorAction Stop
                }
            } else { $ok = $false }
        }
    } catch { $ok = $false }
    Note $what $ok
}

# one field inside HKCU DirectX UserGpuPreferences the slot holds the whole original string
function Restore-DxField([string]$slot, [string]$field, [string]$what) {
    $raw = Rc $slot
    if ($raw -eq '') { return }
    $path = 'HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences'
    $ok = $true
    try {
        $cur = (Get-ItemProperty -Path $path -Name DirectXUserGlobalSettings -ErrorAction SilentlyContinue).DirectXUserGlobalSettings
        if ($null -eq $cur) { $cur = '' }
        $orig = ''
        if ($raw -ne $Absent) { $orig = $raw }
        $m = [regex]::Match($orig, '(?:^|;)' + $field + '=([^;]*)')
        $parts = @($cur -split ';' | Where-Object { $_ -ne '' -and -not $_.StartsWith($field + '=') })
        if ($m.Success) { $parts += ($field + '=' + $m.Groups[1].Value) }
        $next = ($parts -join ';')
        if ($next -ne '') { $next += ';' }
        if ($next -eq '') {
            if (Test-Path $path) { Remove-ItemProperty -Path $path -Name DirectXUserGlobalSettings -ErrorAction SilentlyContinue }
        } else {
            if (-not (Test-Path $path)) { New-Item -Path $path -Force -ErrorAction Stop | Out-Null }
            Set-ItemProperty -Path $path -Name DirectXUserGlobalSettings -Value $next -Type String -ErrorAction Stop
        }
    } catch { $ok = $false }
    Note $what $ok
}

# AppCompat layer token per exe empty remainder deletes the value
function Remove-LayerToken([string]$listSlot, [string]$token, [string]$what) {
    $raw = Rc $listSlot
    if ($raw -eq '') { return }
    $path = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
    $ok = $true
    foreach ($exe in ($raw -split $US)) {
        if ($exe -eq '') { continue }
        try {
            $cur = (Get-ItemProperty -Path $path -Name $exe -ErrorAction SilentlyContinue).$exe
            if ($null -eq $cur) { continue }
            $parts = @(([string]$cur) -split ' ' | Where-Object { $_ -ne '' -and $_ -ne $token })
            if ($parts.Count -le 1) { Remove-ItemProperty -Path $path -Name $exe -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -Path $path -Name $exe -Value ($parts -join ' ') -Type String -ErrorAction Stop }
        } catch { $ok = $false }
    }
    Note $what $ok
}

# service ledger 2\n<phase>\t<name>\t<flag> lines or legacy 1 / a|b start what we stopped
function Resume-Services([string]$flag, [string[]]$names, [string]$what) {
    $raw = Rc $flag
    if ($raw -eq '') { return }
    $targets = @()
    if ($raw.StartsWith("2`n")) {
        foreach ($line in ($raw -split "`n" | Select-Object -Skip 1)) {
            $f = $line -split "`t"
            if ($f.Count -eq 3 -and ($f[0] -eq 'O' -or $f[0] -eq 'R')) { $targets += $f[1] }
        }
    } elseif ($raw -eq '1') { $targets = $names }
    else { $targets = $raw -split '\|' }
    $ok = $true
    foreach ($n in $targets) {
        $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
        if ($null -eq $svc) { continue }
        try {
            if ($svc.StartType -eq 'Disabled') { Set-Service -Name $n -StartupType Manual -ErrorAction Stop }
            if ($svc.Status -ne 'Running') { Start-Service -Name $n -ErrorAction Stop }
        } catch { $ok = $false }
    }
    Note $what $ok
}

function Bcd([string]$bcdArgs) {
    $p = Start-Process bcdedit -ArgumentList $bcdArgs -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $false }
    return $p.ExitCode -eq 0
}


function Bcd([string]$bcdArgs) {
    $p = Start-Process bcdedit -ArgumentList $bcdArgs -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $false }
    return $p.ExitCode -eq 0
}

# 没有收据时按 Windows 默认值复位 有收据的项已经在前一步按收据还原 这里跳过
function Default-Reg([string]$slot, [string]$path, [string]$name, $value, [string]$type, [string]$what) {
    if ((Rc $slot) -ne '') { return }
    $ok = $true
    try {
        if ($null -eq $value) {
            if (Test-Path $path) { Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue }
        } else {
            if (-not (Test-Path $path)) { New-Item -Path $path -Force -ErrorAction Stop | Out-Null }
            Set-ItemProperty -Path $path -Name $name -Value $value -Type $type -ErrorAction Stop
        }
    } catch { $ok = $false }
    Note ('默认值 ' + $what) $ok
}

Write-Host ''
Write-Host '[1/8] 导出证据到桌面...'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) ('Pavise-Rescue-' + $stamp)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$data = Join-Path $env:APPDATA 'Pavise'
foreach ($n in @('Pavise.log', 'Pavise.log.old', 'crash.log', 'Pavise.reports.log')) {
    $p = Join-Path $data $n
    if (Test-Path $p) { Copy-Item $p $out -Force -ErrorAction SilentlyContinue }
}
Get-ChildItem $data -Filter 'crash.*.log' -File -ErrorAction SilentlyContinue | Copy-Item -Destination $out -Force -ErrorAction SilentlyContinue
if (Test-Path $hive) { reg export HKCU\Software\Pavise (Join-Path $out 'Pavise-receipts.reg') /y | Out-Null }
wevtutil qe System '/q:*[System[Provider[@Name=''Microsoft-Windows-WHEA-Logger'']]]' /c:40 /rd:true /f:text 2>$null | Out-File (Join-Path $out 'WHEA.txt') -Encoding utf8
wevtutil qe System '/q:*[System[(EventID=41 or EventID=1001 or EventID=6008)]]' /c:40 /rd:true /f:text 2>$null | Out-File (Join-Path $out 'BugCheck.txt') -Encoding utf8
(powercfg /list) + '' + (powercfg /getactivescheme) | Out-File (Join-Path $out 'powercfg-list.txt') -Encoding utf8
powercfg /q | Out-File (Join-Path $out 'powercfg-active.txt') -Encoding utf8
bcdedit /enum | Out-File (Join-Path $out 'bcdedit.txt') -Encoding utf8
Get-CimInstance Win32_Processor | Select-Object Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors | Format-List | Out-File (Join-Path $out 'cpu.txt') -Encoding utf8
Get-CimInstance Win32_BIOS | Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate | Format-List | Out-File (Join-Path $out 'bios.txt') -Encoding utf8
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product | Format-List | Out-File (Join-Path $out 'board.txt') -Encoding utf8 -Append
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion | Format-List | Out-File (Join-Path $out 'gpu.txt') -Encoding utf8
$dumps = Join-Path $env:SystemRoot 'Minidump'
if (Test-Path $dumps) {
    Get-ChildItem $dumps -Filter '*.dmp' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 5 |
        Copy-Item -Destination $out -Force -ErrorAction SilentlyContinue
}
Write-Host ('       已导出到 ' + $out)

Write-Host '[2/8] 退出运行中的 Pavise 并删除自启任务...'
try { [System.Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit').Set() | Out-Null } catch { }
$waited = 0
while ((Get-Process 'Pavise*' -ErrorAction SilentlyContinue) -and $waited -lt 8) {
    Start-Sleep 1; $waited++
}
Get-Process 'Pavise*' -ErrorAction SilentlyContinue | Stop-Process -Force
schtasks /Delete /F /TN Pavise 2>$null | Out-Null
Write-Host '       完成'

$plans = @{}
$prevPlan = ''
$managedGuid = ''
Write-Host '[3/8] 按收据还原系统改动...'
if (-not (Test-Path $hive)) {
    Write-Host '       没有收据'
} else {
    $HKLM = 'HKLM:\'
    $HKCU = 'HKCU:\'
    $sysProfile = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
    $kernel = 'SYSTEM\CurrentControlSet\Control\Session Manager\kernel'
    $mm = 'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
    $enum = 'SYSTEM\CurrentControlSet\Enum'
    $table = @(
        @('PrevHwSch',          $HKLM + 'SYSTEM\CurrentControlSet\Control\GraphicsDrivers', 'HwSchMode', 'DWord', 'HAGS'),
        @('PrevVbsEnable',      $HKLM + 'SYSTEM\CurrentControlSet\Control\DeviceGuard', 'EnableVirtualizationBasedSecurity', 'DWord', 'VBS 开关'),
        @('PrevVbsHvci',        $HKLM + 'SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity', 'Enabled', 'DWord', 'VBS 内存完整性'),
        @('PrevSpecOverride',   $HKLM + $mm, 'FeatureSettingsOverride', 'DWord', '推测执行缓解 Override'),
        @('PrevSpecMask',       $HKLM + $mm, 'FeatureSettingsOverrideMask', 'DWord', '推测执行缓解 Mask'),
        @('PrevGlobalTimerRes', $HKLM + $kernel, 'GlobalTimerResolutionRequests', 'DWord', '全局计时器分辨率'),
        @('Mmcss_Resp',         $HKLM + $sysProfile, 'SystemResponsiveness', 'DWord', 'MMCSS 系统响应度'),
        @('Mmcss_Pri',          $HKLM + $sysProfile + '\Tasks\Games', 'Priority', 'DWord', 'MMCSS 游戏优先级'),
        @('Mmcss_Sched',        $HKLM + $sysProfile + '\Tasks\Games', 'Scheduling Category', 'String', 'MMCSS 调度类别'),
        @('Mmcss_Sfio',         $HKLM + $sysProfile + '\Tasks\Games', 'SFIO Priority', 'String', 'MMCSS IO 优先级'),
        @('Mmcss_NoLazy',       $HKLM + $sysProfile, 'NoLazyMode', 'DWord', 'MMCSS NoLazyMode'),
        @('PrevNetThrottle',    $HKLM + $sysProfile, 'NetworkThrottlingIndex', 'DWord', '网络限流'),
        @('PrevPresenceQos',    $HKLM + 'SYSTEM\CurrentControlSet\Control\Power\PowerThrottling', 'DisableUserPresenceQos', 'DWord', '在场感知 QoS'),
        @('PrevWin32PriSep',    $HKLM + 'SYSTEM\CurrentControlSet\Control\PriorityControl', 'Win32PrioritySeparation', 'DWord', '前台优先级分离'),
        @('PrevPrioritySep',    $HKLM + 'SYSTEM\CurrentControlSet\Control\PriorityControl', 'Win32PrioritySeparation', 'DWord', '时间片分离'),
        @('PrevDoBgBw',         $HKLM + 'SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization', 'DOMaxBackgroundDownloadBandwidth', 'DWord', '传递优化带宽'),
        @('PrevMaintDisabled',  $HKLM + 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance', 'MaintenanceDisabled', 'DWord', '系统维护'),
        @('PrevMpoOverlay',     $HKLM + 'SOFTWARE\Microsoft\Windows\Dwm', 'OverlayTestMode', 'DWord', '多平面叠加'),
        @('PrevMouseQueue',     $HKLM + 'SYSTEM\CurrentControlSet\Services\mouclass\Parameters', 'MouseDataQueueSize', 'DWord', '鼠标队列'),
        @('PrevKbdQueue',       $HKLM + 'SYSTEM\CurrentControlSet\Services\kbdclass\Parameters', 'KeyboardDataQueueSize', 'DWord', '键盘队列'),
        @('PrevGameDvr',        $HKCU + 'System\GameConfigStore', 'GameDVR_Enabled', 'DWord', 'Game DVR'),
        @('PrevGameDvrCap',     $HKCU + 'SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR', 'AppCaptureEnabled', 'DWord', 'Game DVR 捕获'),
        @('PrevAutoGameMode',   $HKCU + 'Software\Microsoft\GameBar', 'AutoGameModeEnabled', 'DWord', '游戏模式守护'),
        @('PrevToast',          $HKCU + 'Software\Microsoft\Windows\CurrentVersion\PushNotifications', 'ToastEnabled', 'DWord', '通知横幅'),
        @('PrevFilterKeys',     $HKCU + 'Control Panel\Accessibility\Keyboard Response', 'Flags', 'String', '筛选键'),
        @('PrevStickyKeys',     $HKCU + 'Control Panel\Accessibility\StickyKeys', 'Flags', 'String', '粘滞键'),
        @('PrevToggleKeys',     $HKCU + 'Control Panel\Accessibility\ToggleKeys', 'Flags', 'String', '切换键')
    )
    foreach ($row in $table) { Restore-Slot $row[0] $row[1] $row[2] $row[3] $row[4] }
    Restore-DxField 'PrevSwapEffectUpgrade' 'SwapEffectUpgradeEnable' '窗口化优化'
    Restore-DxField 'PrevVrrOptimize' 'VRROptimizeEnable' '可变刷新率优化'
    $netClass = $HKLM + 'SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
    foreach ($idx in ((Rc 'DevPowerList') -split ';')) {
        if ($idx -eq '') { continue }
        Restore-Slot ('DevPower_' + $idx) ($netClass + '\' + $idx) 'PnPCapabilities' 'DWord' ('网卡设备电源 ' + $idx)
    }
    foreach ($id in ((Rc 'HidPowerList') -split ';')) {
        if ($id -eq '') { continue }
        $slot = $id.Replace('\', '_').Replace('&', '-')
        $devPath = $HKLM + $enum + '\' + $id + '\Device Parameters'
        Restore-Slot ('HidEpm_' + $slot) $devPath 'EnhancedPowerManagementEnabled' 'DWord' ('键鼠增强电源 ' + $id)
        Restore-Slot ('HidSs_' + $slot) $devPath 'SelectiveSuspendEnabled' 'DWord' ('键鼠选择性暂停 ' + $id)
    }
    foreach ($id in ((Rc 'MsiList') -split ';')) {
        if ($id -eq '') { continue }
        Restore-Slot ('Msi_' + $id.Replace('\', '_')) ($HKLM + $enum + '\' + $id + '\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties') 'MSISupported' 'DWord' ('MSI ' + $id)
    }
    Remove-LayerToken 'FsoExeList' 'DISABLEDXMAXIMIZEDWINDOWEDMODE' '全屏优化兼容层'
    Remove-LayerToken 'DpiExeList' 'HIGHDPIAWARE' 'DPI 兼容层'

    Write-Host '   · 还原启动项与内核项...'
    $tick = Rc 'PrevTimerTick'
    if ($tick -ne '') {
        $names = @('useplatformclock', 'useplatformtick', 'disabledynamictick')
        $back = $tick -split '\|'
        $ok = $true
        for ($i = 0; $i -lt $names.Count; $i++) {
            $want = $Absent
            if ($i -lt $back.Count) { $want = $back[$i] }
            if ($want -eq $Absent -or $want -eq '') { Bcd ('/deletevalue ' + $names[$i]) | Out-Null }
            elseif (-not (Bcd ('/set ' + $names[$i] + ' ' + $want))) { $ok = $false }
        }
        Note '计时器节拍（重启后生效）' $ok
    }
    $hv = Rc 'PrevHvLaunch'
    if ($hv -ne '') {
        if ($hv -eq $Absent) { $ok = Bcd '/deletevalue hypervisorlaunchtype' }
        else { $ok = Bcd ('/set hypervisorlaunchtype ' + $hv) }
        Note '虚拟机监控程序启动类型（重启后生效）' $ok
    }
    $rs = Rc 'PrevReservedCpuSets'
    if ($rs -ne '') {
        $ok = $true
        try {
            $kpath = $HKLM + $kernel
            if ($rs -eq $Absent) { Remove-ItemProperty -Path $kpath -Name ReservedCpuSets -ErrorAction SilentlyContinue }
            else {
                $bytes = [byte[]]($rs -split '-' | ForEach-Object { [Convert]::ToByte($_, 16) })
                Set-ItemProperty -Path $kpath -Name ReservedCpuSets -Value $bytes -Type Binary -ErrorAction Stop
            }
        } catch { $ok = $false }
        Note '内核保留核（重启后生效）' $ok
    }
    $mma = Rc 'PrevMMAgent'
    if ($mma -ne '') {
        $f = $mma -split ','
        $ok = $true
        try {
            if ($f.Count -ge 1 -and $f[0] -eq '1') { Enable-MMAgent -MemoryCompression -ErrorAction Stop }
            if ($f.Count -ge 2 -and $f[1] -eq '1') { Enable-MMAgent -PageCombining -ErrorAction Stop }
        } catch { $ok = $false }
        Note '内存压缩与页合并' $ok
    }

    Write-Host '   · 还原电源项...'
    $idle = Rc 'CpuIdleStateV1'
    if ($idle -ne '') {
        $f = $idle -split '\|'
        $ok = $true
        if ($f.Count -ge 2 -and $f[1] -match '^[0-9a-fA-F-]{36}$') {
            powercfg /setacvalueindex $f[1] 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 0 | Out-Null
            if ($LASTEXITCODE -ne 0) { $ok = $false }
            powercfg /setdcvalueindex $f[1] 54533251-82be-4824-96c1-47b60b740d00 5d76a2ca-e8c0-402f-a133-2158492d58ad 0 | Out-Null
            if ($LASTEXITCODE -ne 0) { $ok = $false }
        }
        Note 'CPU 空闲状态' $ok
    }
    Get-CimInstance -Namespace root\cimv2\power -ClassName Win32_PowerPlan -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.InstanceID -match '\{([0-9a-fA-F-]{36})\}') { $plans[$matches[1].ToLower()] = [string]$_.ElementName }
    }
    $prevPlan = (Rc 'PrevPowerPlan').ToLower()
    $managedGuid = (Rc 'PgPlanGuid').ToLower()
    if ($prevPlan -ne '' -and $plans.ContainsKey($prevPlan) -and $prevPlan -ne $managedGuid) {
        powercfg /setactive $prevPlan | Out-Null
        Note '原电源方案' ($LASTEXITCODE -eq 0)
    }
    $overlay = Rc 'PowerOverlaySnap'
    if ($overlay -match '^[0-9a-fA-F-]{36}$') {
        powercfg /overlaysetactive $overlay | Out-Null
        Note '电源模式' ($LASTEXITCODE -eq 0)
    }

    Write-Host '   · 还原网卡、笔记本、显卡与服务...'
    $rss = Rc 'RssSteerReceipt'
    if ($rss -ne '') {
        $ok = $true
        foreach ($rec in ($rss -split ';')) {
            $f = $rec -split '\|'
            if ($f.Count -ne 3) { continue }
            if ($null -eq (Get-NetAdapter -Name $f[0] -ErrorAction SilentlyContinue)) { continue }
            try { Set-NetAdapterRss -Name $f[0] -BaseProcessorNumber ([byte][int]$f[1]) -MaxProcessorNumber ([byte][int]$f[2]) -ErrorAction Stop } catch { $ok = $false }
        }
        Note '网卡 RSS 处理器范围' $ok
    }
    $eee = Rc 'EeeReceipt'
    if ($eee -ne '') {
        $ok = $true
        $changed = @()
        foreach ($rec in ($eee -split ';')) {
            $f = $rec -split '\|'
            if ($f.Count -ne 3) { continue }
            if ($null -eq (Get-NetAdapter -Name $f[0] -ErrorAction SilentlyContinue)) { continue }
            try {
                Set-NetAdapterAdvancedProperty -Name $f[0] -RegistryKeyword $f[1] -RegistryValue $f[2] -NoRestart -ErrorAction Stop
                if ($changed -notcontains $f[0]) { $changed += $f[0] }
            } catch { $ok = $false }
        }
        foreach ($c in $changed) { Restart-NetAdapter -Name $c -ErrorAction SilentlyContinue }
        Note '网卡链路节能' $ok
    }
    $lm = Rc 'LinkMetricReceipt'
    if ($lm -match '^\d+$') {
        $ok = $true
        if ($null -ne (Get-NetAdapter -InterfaceIndex ([int]$lm) -ErrorAction SilentlyContinue)) {
            try { Set-NetIPInterface -InterfaceIndex ([int]$lm) -AutomaticMetric Enabled -ErrorAction Stop } catch { $ok = $false }
        }
        Note '路由跃点' $ok
    }
    $lp = Rc 'LaptopPerfSnap'
    if ($lp -ne '') {
        $f = $lp -split '\|'
        $ok = $false
        if ($f.Count -eq 2) {
            try {
                if ($f[0] -eq '1') {
                    $gz = Get-WmiObject -Namespace root\WMI -Class LENOVO_GAMEZONE_DATA -ErrorAction Stop | Select-Object -First 1
                    if ($null -ne $gz) { $gz.SetSmartFanMode([uint32]$f[1]) | Out-Null; $ok = $true }
                } elseif ($f[0] -eq '2') {
                    $atk = Get-WmiObject -Namespace root\WMI -Class AsusAtkWmi_WMNB -ErrorAction Stop | Select-Object -First 1
                    if ($null -ne $atk) { $atk.DEVS([uint32]0x00120075, [uint32]$f[1]) | Out-Null; $ok = $true }
                }
            } catch { $ok = $false }
        }
        Note '笔记本厂商性能档' $ok
    }
    $gpu = Rc 'GpuClockLockReceipt'
    if ($gpu -ne '') {
        $smi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
        if ($null -eq $smi) {
            $cand = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
            if (Test-Path $cand) { $smi = Get-Item $cand }
        }
        $ok = $false
        if ($null -ne $smi) {
            $smiPath = $smi.Source
            if ($null -eq $smiPath) { $smiPath = $smi.FullName }
            $p = Start-Process $smiPath -ArgumentList '-rgc' -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
            if ($null -ne $p -and $p.ExitCode -eq 0) { $ok = $true }
        }
        Note 'NVIDIA 频率锁定' $ok
    }
    Resume-Services 'PrevSvcPaused' @('SysMain', 'WSearch') '系统服务 SysMain/WSearch'
    Resume-Services 'PrevUpdatePaused' @('wuauserv', 'UsoSvc') 'Windows 更新服务'
    Resume-Services 'PrevDoSvcStopped' @('DoSvc') '传递优化服务'
    Resume-Services 'PrevOptionalServicesPausedV1' @('PrintNotify', 'Spooler', 'WSearch', 'WMPNetworkSvc', 'MapsBroker', 'DiagTrack', 'RetailDemo') '可选后台服务'
    if ($script:restored -eq 0 -and $script:failed.Count -eq 0) { Write-Host '       没有需要还原的收据' }
}

Write-Host '[4/8] 收据缺失的项按 Windows 默认值复位...'
$HKLM = 'HKLM:\'
$sysProfile = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
$kernel = 'SYSTEM\CurrentControlSet\Control\Session Manager\kernel'
$mm = 'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
Default-Reg 'PrevReservedCpuSets' ($HKLM + $kernel) 'ReservedCpuSets' $null 'Binary' '内核保留核（重启后生效）'
Default-Reg 'PrevGlobalTimerRes' ($HKLM + $kernel) 'GlobalTimerResolutionRequests' $null 'DWord' '全局计时器分辨率'
Default-Reg 'PrevSpecOverride' ($HKLM + $mm) 'FeatureSettingsOverride' $null 'DWord' '推测执行缓解 Override'
Default-Reg 'PrevSpecMask' ($HKLM + $mm) 'FeatureSettingsOverrideMask' $null 'DWord' '推测执行缓解 Mask'
Default-Reg 'PrevMaintDisabled' ($HKLM + 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance') 'MaintenanceDisabled' $null 'DWord' '系统维护'
Default-Reg 'PrevPresenceQos' ($HKLM + 'SYSTEM\CurrentControlSet\Control\Power\PowerThrottling') 'DisableUserPresenceQos' $null 'DWord' '在场感知 QoS'
Default-Reg 'Mmcss_Resp' ($HKLM + $sysProfile) 'SystemResponsiveness' 20 'DWord' 'MMCSS 系统响应度'
Default-Reg 'Mmcss_NoLazy' ($HKLM + $sysProfile) 'NoLazyMode' $null 'DWord' 'MMCSS NoLazyMode'
Default-Reg 'Mmcss_Sched' ($HKLM + $sysProfile + '\Tasks\Games') 'Scheduling Category' 'Medium' 'String' 'MMCSS 调度类别'
Default-Reg 'Mmcss_Sfio' ($HKLM + $sysProfile + '\Tasks\Games') 'SFIO Priority' 'Normal' 'String' 'MMCSS IO 优先级'
Default-Reg 'Mmcss_Pri' ($HKLM + $sysProfile + '\Tasks\Games') 'Priority' 2 'DWord' 'MMCSS 游戏优先级'
Default-Reg 'PrevNetThrottle' ($HKLM + $sysProfile) 'NetworkThrottlingIndex' 10 'DWord' '网络限流'
Default-Reg 'PrevWin32PriSep' ($HKLM + 'SYSTEM\CurrentControlSet\Control\PriorityControl') 'Win32PrioritySeparation' 2 'DWord' '前台优先级分离'
if ((Rc 'PrevTimerTick') -eq '') {
    $ok = $true
    foreach ($n in @('useplatformclock', 'useplatformtick', 'disabledynamictick')) { Bcd ('/deletevalue ' + $n) | Out-Null }
    Note '默认值 计时器节拍（重启后生效）' $ok
}
if ((Rc 'PrevMMAgent') -eq '') {
    $ok = $true
    try { Enable-MMAgent -MemoryCompression -ErrorAction Stop } catch { $ok = $false }
    try { Enable-MMAgent -PageCombining -ErrorAction Stop } catch { }
    Note '默认值 内存压缩' $ok
}
$svcDefaults = @{ 'SysMain' = 'Automatic'; 'WSearch' = 'Automatic'; 'wuauserv' = 'Manual'; 'UsoSvc' = 'Manual'; 'DoSvc' = 'Manual' }
$ok = $true
foreach ($n in $svcDefaults.Keys) {
    $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
    if ($null -eq $svc) { continue }
    try {
        if ($svc.StartType -eq 'Disabled') { Set-Service -Name $n -StartupType $svcDefaults[$n] -ErrorAction Stop }
        if ($svc.Status -ne 'Running' -and $svcDefaults[$n] -eq 'Automatic') { Start-Service -Name $n -ErrorAction Stop }
    } catch { $ok = $false }
}
Note '默认值 系统服务启动类型' $ok
foreach ($k in @('PrevHwSch', 'PrevVbsEnable', 'PrevHvLaunch')) {
    if ((Rc $k) -eq '') { Write-Host ('       未动 ' + $k + ' 没有收据 HAGS 与 VBS 保持现状') }
}

Write-Host '[5/8] 电源方案恢复出厂并切回平衡...'
$balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'
$active = ''
$out = powercfg /getactivescheme
if ($out -match '([0-9a-fA-F-]{36})') { $active = $matches[1].ToLower() }
$doomed = @()
if ($managedGuid -match '^[0-9a-f-]{36}$') { $doomed += $managedGuid }
Get-CimInstance -Namespace root\cimv2\power -ClassName Win32_PowerPlan -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.InstanceID -match '\{([0-9a-fA-F-]{36})\}') {
        $g = $matches[1].ToLower()
        $n = [string]$_.ElementName
        if ($n.StartsWith('PG ') -or $n.StartsWith('由软件调度') -or $n.StartsWith('Scheduled by Pavise')) {
            if ($doomed -notcontains $g) { $doomed += $g }
        }
    }
}
if ($doomed.Count -eq 0) {
    powercfg /list | ForEach-Object {
        if ($_ -match '([0-9a-fA-F-]{36})\s*\((PG |Scheduled by Pavise).*?\)') { $doomed += $matches[1].ToLower() }
    }
}
foreach ($g in $doomed) {
    if ($g -eq $active) {
        $fallback = $balanced
        if ($prevPlan -ne '' -and $prevPlan -ne $g -and $plans.ContainsKey($prevPlan)) { $fallback = $prevPlan }
        powercfg /setactive $fallback | Out-Null
        $active = $fallback
    }
    powercfg /delete $g | Out-Null
}
# 空闲策略写进托管方案的旋钮可能已经漏到别的方案 全部方案恢复出厂最省事
powercfg -restoredefaultschemes | Out-Null
powercfg /setactive $balanced | Out-Null
Note '全部电源方案恢复出厂 当前平衡' ($LASTEXITCODE -eq 0)

Write-Host '[6/8] 复位显卡频率锁定与功耗墙...'
$smi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
if ($null -eq $smi) {
    $cand = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
    if (Test-Path $cand) { $smi = Get-Item $cand }
}
if ($null -ne $smi) {
    $smiPath = $smi.Source
    if ($null -eq $smiPath) { $smiPath = $smi.FullName }
    $p = Start-Process $smiPath -ArgumentList '-rgc' -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
    Note 'NVIDIA 频率锁定解除' ($null -ne $p -and $p.ExitCode -eq 0)
    $limits = & $smiPath --query-gpu=power.default_limit --format=csv,noheader,nounits 2>$null
    $i = 0
    foreach ($line in @($limits)) {
        $w = 0.0
        if ([double]::TryParse(([string]$line).Trim(), [ref]$w) -and $w -gt 0) {
            $p = Start-Process $smiPath -ArgumentList ('-i ' + $i + ' -pl ' + [math]::Round($w)) -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
            Note ('NVIDIA 功耗墙回默认 GPU' + $i + ' ' + [math]::Round($w) + 'W') ($null -ne $p -and $p.ExitCode -eq 0)
        }
        $i++
    }
} else {
    Write-Host '       未找到 nvidia-smi 非 NVIDIA 显卡或驱动未装 跳过'
}

Write-Host '[7/8] 删除 Pavise 设置与数据...'
Remove-Item $hive -Recurse -Force -ErrorAction SilentlyContinue
$data = Join-Path $env:APPDATA 'Pavise'
if (Test-Path $data) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $data) {
    Write-Host '       部分文件被占用未能删除，请重启后手动删除该目录' -ForegroundColor Yellow
} else {
    Write-Host '       完成'
}

Write-Host '[8/8] 清理程序目录与临时文件...'
$here = Split-Path -Parent $self
$leftovers = @('Pavise.log','Pavise.reports.log','Pavise.games.txt','Pavise.targets.txt',
    'Pavise.whitelist.txt','Pavise.profiles.dat','Pavise.renderer-observations.dat',
    'Pavise.suppression.state','Pavise.freeze.state','crash.log','setup.log','swap.log')
foreach ($name in $leftovers) {
    $path = Join-Path $here $name
    if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}
Get-ChildItem $here -Filter 'Pavise.*.log' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $here -Filter 'crash.*.log' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $env:TEMP 'PaviseShot_*') -Directory -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $env:TEMP 'PaviseSelftestData-*') -Directory -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '       完成'

Write-Host ''
Write-Host '============================================================'
Write-Host (' 急救完成。已还原或复位 ' + $script:restored + ' 项。现在请重启电脑。')
if ($script:failed.Count -gt 0) {
    Write-Host (' 未能处理：' + ($script:failed -join '、')) -ForegroundColor Yellow
}
Write-Host ''
Write-Host (' 证据已导出到 ' + $out) -ForegroundColor Cyan
Write-Host ' 重启后把这个文件夹整个打包发给作者。' -ForegroundColor Cyan
Write-Host ''
Write-Host ' Machine Check Exception 是处理器报的硬件错误。建议：' -ForegroundColor Yellow
Write-Host ' 1. BIOS 恢复默认设置，关闭 XMP/EXPO 与任何超频，卸载 XTU、ThrottleStop 一类降压工具；' -ForegroundColor Yellow
Write-Host ' 2. 把 BIOS 更新到厂商最新版，笔记本顺手清一次散热；' -ForegroundColor Yellow
Write-Host ' 3. 这台机器以后不要开系统环境页里关闭 VBS 与卸载推测执行缓解那两项。' -ForegroundColor Yellow
Write-Host '============================================================'
Write-Host ''
try { Start-Process explorer.exe $out } catch { }
Read-Host '按回车键退出' | Out-Null
