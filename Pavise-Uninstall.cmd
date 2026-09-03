@echo off
rem @author bdth 2074055628@qq.com
rem file: one-shot cleanup of all Pavise data (uninstall helper)
rem ASCII ONLY above the marker line, and CRLF line endings only. cmd
rem decodes this file with the console startup codepage (936 or 65001);
rem any non-ASCII byte up here shifts the parser, and bare-LF endings
rem break batch parsing outright. All logic and Chinese text live in the
rem PowerShell block below, read back as UTF-8 and never parsed by cmd.
set "PAVISE_UNINSTALL_SELF=%~f0"
set "PAVISE_UNINSTALL_MODE=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$m=[IO.File]::ReadAllText($env:PAVISE_UNINSTALL_SELF,(New-Object Text.UTF8Encoding($false)));$i=$m.IndexOf([string][char]10+'#PSBEGIN');Invoke-Expression $m.Substring($i)"
exit /b

#PSBEGIN
$ErrorActionPreference = 'SilentlyContinue'
$self = $env:PAVISE_UNINSTALL_SELF
$fromApp = $env:PAVISE_UNINSTALL_MODE -eq '--from-app'

# ---- elevation: restores, scheduled task and power plan removal need admin ----
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Host '需要管理员权限，正在请求提升...'
    $relaunch = '"' + $self + '"'
    if ($fromApp) { $relaunch += ' --from-app' }
    Start-Process cmd -Verb RunAs -ArgumentList '/c', $relaunch
    exit
}

Write-Host '============================================================'
Write-Host ' Pavise 一键清除（相当于卸载）'
Write-Host '============================================================'
Write-Host ''
Write-Host ' 将执行：退出运行中的 Pavise；找到 Pavise.exe 时先由程序自身按收据完整还原'
Write-Host ' 全部系统改动（含 NVIDIA/AMD/Intel 显卡项、网卡中断合并、中断钉核）；随后脚本再按'
Write-Host ' 收据兜底一遍；删除开机自启任务、托管电源方案、全部设置（注册表）、数据目录、'
Write-Host ' 旧版本遗留的注册表项与临时文件。执行完相当于从未安装过 Pavise。'
Write-Host ''
Write-Host ' 脚本自身能还原：注册表类改动（HAGS、VBS、推测执行缓解、MMCSS、网络限流、' -ForegroundColor Cyan
Write-Host ' 游戏模式守护、Game DVR、辅助功能按键、键鼠与网卡设备电源、MSI、IFEO、' -ForegroundColor Cyan
Write-Host ' 中断优先级、窗口化优化与可变刷新率优化、系统维护、DO 带宽等）、启动项' -ForegroundColor Cyan
Write-Host ' （计时器节拍、虚拟机监控程序）、内核保留核、内存压缩、电源方案与电源模式、' -ForegroundColor Cyan
Write-Host ' CPU 空闲、网卡 RSS 与链路节能、路由跃点、QoS 策略、笔记本厂商性能档、' -ForegroundColor Cyan
Write-Host ' NVIDIA 频率锁定与功耗墙、全屏优化与 DPI 兼容层、被暂停的系统服务。' -ForegroundColor Cyan
Write-Host ''
Write-Host ' 只有程序自身能还原（找不到 Pavise.exe 时会跳过，收据随设置一并删除）：' -ForegroundColor Yellow
Write-Host ' NVIDIA 逐游戏配置（可在 NVIDIA 控制面板“恢复默认设置”）、AMD 最低频率与' -ForegroundColor Yellow
Write-Host ' SAM、Intel 低延迟与 Endurance Gaming、网卡中断合并、逐应用显卡偏好、FTH。' -ForegroundColor Yellow
Write-Host ''
Write-Host ' 请把本脚本和 Pavise.exe 放在同一目录运行；程序不在旁边时会按开机任务里' -ForegroundColor Green
Write-Host ' 记录的路径去找。' -ForegroundColor Green
Write-Host ''
if ($fromApp) {
    Write-Host '已在 Pavise 中确认卸载，开始执行...' -ForegroundColor Green
} else {
    $answer = Read-Host '输入 Y 并回车继续清除，其它任意输入取消'
    if ($answer -ne 'Y' -and $answer -ne 'y') { Write-Host '已取消。'; Start-Sleep 1; exit }
}

# ---- receipts live in HKCU\Software\Pavise; strings are REG_SZ, flags are DWORD ----
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

# ReversibleReg slot: original [US applied]; original is __pavise_absent__, b<base64> or =<value>
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

# one field inside HKCU DirectX UserGpuPreferences; the slot holds the whole original string
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

# AppCompat layer token per exe; empty remainder deletes the value
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

# service ledger: "2\n<phase>\t<name>\t<flag>" lines, or legacy "1" / "a|b"; start what we stopped
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

Write-Host ''
Write-Host '[1/10] 退出运行中的 Pavise...'
try { [System.Threading.EventWaitHandle]::OpenExisting('Global\Pavise_Exit').Set() | Out-Null } catch { }
# 从程序里发起时 程序自己走完整退出流程 会话项要还原 给它 25 秒 手动运行时 8 秒
$exitWait = 8
if ($fromApp) { $exitWait = 25 }
$waited = 0
while ((Get-Process 'Pavise*' -ErrorAction SilentlyContinue) -and $waited -lt $exitWait) {
    Start-Sleep 1; $waited++
}
Get-Process 'Pavise*' -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host '       完成'

Write-Host '[2/10] 交给 Pavise 自身按收据完整还原...'
$exe = $null
$here = Split-Path -Parent $self
$cands = @()
foreach ($dir in @($here, (Join-Path $here 'build'))) {
    if (Test-Path $dir) {
        $cands += Get-ChildItem $dir -Filter 'Pavise*.exe' -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'selftest|check|shot|regression|audit|review' }
    }
}
if ($cands.Count -gt 0) { $exe = ($cands | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
if ($null -eq $exe) {
    $xml = schtasks /Query /TN Pavise /XML 2>$null
    if ($xml -match '<Command>(.*?)</Command>') {
        $c = [System.Net.WebUtility]::HtmlDecode($matches[1]).Trim('"')
        if (Test-Path $c) { $exe = $c }
    }
}
$exeCleared = $false
if ($null -eq $exe) {
    Write-Host '       未找到 Pavise.exe 跳过 显卡驱动侧与网卡中断合并等项只能靠程序自身还原' -ForegroundColor Yellow
} else {
    $result = Join-Path $env:TEMP 'Pavise.uninstall.txt'
    Remove-Item $result -Force -ErrorAction SilentlyContinue
    Write-Host ('       使用 ' + $exe)
    $p = Start-Process $exe -ArgumentList @('--uninstall', ('"' + $result + '"')) -PassThru -ErrorAction SilentlyContinue
    # 旧版本不认这个参数 会当普通启动把界面打开 等 40 秒没有结果文件就当它不支持 强制结束
    $waited = 0
    while ($null -ne $p -and -not $p.HasExited -and -not (Test-Path $result) -and $waited -lt 40) { Start-Sleep 1; $waited++ }
    if ($null -ne $p -and -not $p.HasExited) {
        if (Test-Path $result) { $p.WaitForExit(20000) | Out-Null }
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    Get-Process 'Pavise*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $text = ''
    if (Test-Path $result) { $text = [IO.File]::ReadAllText($result, (New-Object Text.UTF8Encoding($false))) }
    if ($text -match 'cleared=1') {
        $exeCleared = $true
        Write-Host '       程序已按收据还原全部系统改动并清空设置与数据'
    } elseif ($text -eq '') {
        Write-Host '       这个 Pavise.exe 不支持自卸载 已强制退出 下面由脚本按收据兜底' -ForegroundColor Yellow
    } else {
        $why = ''
        if ($text -match 'unrestored=(.*)') { $why = $matches[1].Trim() }
        if ($text -match 'error=(.*)') { $why = $matches[1].Trim() }
        Write-Host ('       程序自身还原未完成 ' + $why + ' 下面由脚本按收据兜底') -ForegroundColor Yellow
    }
    Remove-Item $result -Force -ErrorAction SilentlyContinue
}

$plans = @{}
$prevPlan = ''
$managedGuid = ''
if (-not (Test-Path $hive)) {
    Write-Host '[3/10] 没有收据，跳过脚本侧还原'
    Write-Host '[4/10] 跳过'
    Write-Host '[5/10] 跳过'
    Write-Host '[6/10] 跳过'
} else {
    Write-Host '[3/10] 按收据还原注册表改动...'
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

    Write-Host '[4/10] 还原启动项与内核项...'
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

    Write-Host '[5/10] 还原电源项...'
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

    Write-Host '[6/10] 还原网卡、笔记本、显卡与服务...'
    foreach ($exe0 in ((Rc 'IfeoList') -split ';')) {
        if ($exe0 -eq '') { continue }
        $perf = $HKLM + 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\' + $exe0 + '\PerfOptions'
        Restore-Slot ('IfeoPri_' + $exe0) $perf 'CpuPriorityClass' 'DWord' ('IFEO CPU 优先级 ' + $exe0)
        Restore-Slot ('IfeoIo_' + $exe0) $perf 'IoPriority' 'DWord' ('IFEO IO 优先级 ' + $exe0)
        Restore-Slot ('IfeoPg_' + $exe0) $perf 'PagePriority' 'DWord' ('IFEO 内存页优先级 ' + $exe0)
    }
    $irqPrio = Rc 'IrqPrio'
    if ($irqPrio -ne '') {
        $ok = $true
        foreach ($rec in ($irqPrio -split ';')) {
            $f = $rec -split '\|'
            if ($f.Count -ne 2) { continue }
            try {
                $id = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($f[0]))
                $path = $HKLM + $enum + '\' + $id + '\Device Parameters\Interrupt Management\Affinity Policy'
                if (Test-Path $path) {
                    if ($f[1] -eq '-') { Remove-ItemProperty -Path $path -Name DevicePriority -ErrorAction SilentlyContinue }
                    else { Set-ItemProperty -Path $path -Name DevicePriority -Value ([int32]$f[1]) -Type DWord -ErrorAction Stop }
                }
            } catch { $ok = $false }
        }
        Note '设备中断优先级' $ok
    }
    $qos = Rc 'NetQosPolicyNames'
    if ($qos -ne '') {
        $ok = $true
        foreach ($n in ($qos -split ';')) {
            if ($n -eq '') { continue }
            try { Remove-NetQosPolicy -Name $n -Confirm:$false -ErrorAction Stop } catch { if ($null -ne (Get-NetQosPolicy -Name $n -ErrorAction SilentlyContinue)) { $ok = $false } }
        }
        Note 'QoS 游戏流量策略' $ok
    }
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
    $smi = Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue
    if ($null -eq $smi) {
        $cand = Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'
        if (Test-Path $cand) { $smi = Get-Item $cand }
    }
    if ($null -ne $smi) {
        $smiPath = $smi.Source
        if ($null -eq $smiPath) { $smiPath = $smi.FullName }
        $p = Start-Process $smiPath -ArgumentList '-rgc' -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
        if ((Rc 'GpuClockLockReceipt') -ne '') { Note 'NVIDIA 频率锁定' ($null -ne $p -and $p.ExitCode -eq 0) }
        # NVAPI 收据里的功耗值是千分比 不是瓦 脚本侧直接按驱动默认功耗墙回写
        if ((Rc 'GpuPowerSnap') -like 'nv:*') {
            $limits = & $smiPath --query-gpu=power.default_limit --format=csv,noheader,nounits 2>$null
            $i = 0
            foreach ($line in @($limits)) {
                $w = 0.0
                if ([double]::TryParse(([string]$line).Trim(), [ref]$w) -and $w -gt 0) {
                    $p = Start-Process $smiPath -ArgumentList ('-i ' + $i + ' -pl ' + [math]::Round($w)) -NoNewWindow -Wait -PassThru -ErrorAction SilentlyContinue
                    Note ('NVIDIA 功耗墙回默认 GPU' + $i) ($null -ne $p -and $p.ExitCode -eq 0)
                }
                $i++
            }
        }
    }
    Resume-Services 'PrevSvcPaused' @('SysMain', 'WSearch') '系统服务 SysMain/WSearch'
    Resume-Services 'PrevUpdatePaused' @('wuauserv', 'UsoSvc') 'Windows 更新服务'
    Resume-Services 'PrevDoSvcStopped' @('DoSvc') '传递优化服务'
    Resume-Services 'PrevOptionalServicesPausedV1' @('PrintNotify', 'Spooler', 'WSearch', 'WMPNetworkSvc', 'MapsBroker', 'DiagTrack', 'RetailDemo') '可选后台服务'
    if ($script:restored -eq 0 -and $script:failed.Count -eq 0) { Write-Host '       没有需要还原的收据' }
}

Write-Host '[7/10] 删除开机自启任务...'
schtasks /Delete /F /TN Pavise 2>$null | Out-Null
schtasks /Delete /F /TN Aegis 2>$null | Out-Null
Write-Host '       完成'

Write-Host '[8/10] 删除托管电源方案...'
# the plan is named "由软件调度 XXXX" / "Scheduled by Pavise XXXX"; older builds used "PG ..."
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
        if ($n.StartsWith('PG ') -or $n.StartsWith('由软件调度') -or $n.StartsWith('Scheduled by Pavise') -or $n.StartsWith('Aegis') -or $n.StartsWith('Pavise')) {
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
Write-Host '       完成'

Write-Host '[9/10] 删除注册表设置与数据目录...'
Remove-Item $hive -Recurse -Force -ErrorAction SilentlyContinue
foreach ($old in @('HKCU:\Software\Aegis', 'HKCU:\Software\PaviseSelfTest', 'HKCU:\Software\PaviseTest')) {
    if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
}
$data = Join-Path $env:APPDATA 'Pavise'
if (Test-Path $data) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($old in @((Join-Path $env:APPDATA 'Aegis'), (Join-Path $env:LOCALAPPDATA 'Pavise'), (Join-Path $env:ProgramData 'Pavise'))) {
    if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue }
}
if (Test-Path $data) {
    Write-Host '       部分文件被占用未能删除，请重启后手动删除该目录' -ForegroundColor Yellow
} else {
    Write-Host '       完成'
}

Write-Host '[10/10] 清理程序目录与临时文件...'
$here = Split-Path -Parent $self
$leftovers = @('Pavise.log','Pavise.reports.log','Pavise.games.txt','Pavise.targets.txt','Pavise.ico',
    'Pavise.autoignore.txt','Pavise.selftest.txt','Pavise.portable','Aegis.ico',
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
Get-ChildItem (Join-Path $env:TEMP 'PaviseSelftest-*') -Directory -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $env:TEMP -Filter 'Pavise*' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $here -Filter 'Pavise.selftest.*.txt' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Write-Host '       完成'

Write-Host ''
Write-Host '============================================================'
if ($exeCleared) { Write-Host (' 清除完毕。程序自身已完整还原，脚本另补 ' + $script:restored + ' 项。Pavise.exe 请自行删除。') }
else { Write-Host (' 清除完毕。已还原 ' + $script:restored + ' 项系统改动。Pavise.exe 请自行删除。') }
if ($script:failed.Count -gt 0) {
    Write-Host (' 未能还原：' + ($script:failed -join '、')) -ForegroundColor Yellow
}
if ($script:restored -gt 0) {
    Write-Host ' 启动项与内核项的改动需要重启一次才生效。'
}
Write-Host '============================================================'
Write-Host ''
Read-Host '按回车键退出' | Out-Null
