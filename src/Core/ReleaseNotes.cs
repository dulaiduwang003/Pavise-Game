// @author bdth 2074055628@qq.com
// 文件用途 维护内置的三语版本说明并记录已读版本
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class ReleaseNote
    {
        public readonly string Version;
        public readonly string Date;
        private readonly string[][] items;

        public ReleaseNote(string version, string date, string[][] entries)
        {
            Version = version; Date = date; items = entries;
        }

        public string Tag { get { return "v" + Version; } }

        public int Count { get { return items == null ? 0 : items.Length; } }

        public string Item(int index)
        {
            if (items == null || index < 0 || index >= items.Length) return "";
            string[] row = items[index];
            if (row == null || row.Length == 0) return "";
            int lang = Lang.Cur;
            if (lang < 0 || lang >= row.Length || string.IsNullOrEmpty(row[lang])) return row[0];
            return row[lang];
        }
    }

    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new[]
        {
            new ReleaseNote("2.0.1", "2026-08-24", new[]
            {
                new[]{ "修复 中断体检收尾时先撤自造负载 采集线程不再因抢不到内存带宽而超时作废", "Fixed the interrupt checkup so the self-generated load stops before collection is closed; the capture thread no longer times out while starved of memory bandwidth." },
                new[]{ "修复 体检采集超时后未交还探针所有权 导致此后每次体检都被判探针被占 只能重启恢复", "Fixed probe ownership not being released after a capture timeout, which made every later checkup fail as probe-in-use until Pavise was restarted." },
                new[]{ "修复 后台隔离不再关闭 Windows 动态优先级提升 游戏等待被隔离进程回应时偶发约一秒的卡顿已消除", "Fixed background isolation no longer disabling Windows dynamic priority boosts; the occasional one-second stall while the game waited on an isolated process is gone." },
                new[]{ "新增 模块已注入游戏进程的覆盖层宿主自动豁免后台压制 开局点名后即时生效", "Added automatic suppression exemption for overlay hosts whose modules are injected into the game process, effective as soon as the post-launch scan names them." },
                new[]{ "修复 开机自启任务的保护设置补齐从未成功 每次启动都误报需重新开关 现在补齐可以落地并附带失败原因", "Fixed the startup task protection settings never being successfully rebuilt, causing a false re-enable prompt at every launch; the rebuild now lands and failures carry the actual reason." },
                new[]{ "调整 中断挪核重启回来后主动提示还差打一局验证 对局观测未开时会点破打局也不会记录", "Adjusted IRQ relocation to announce after the reboot that one match is still needed for verification, and to call out that playing records nothing while in-match observation is off." },
            }),
            new ReleaseNote("2.0", "2026-08-24", new[]
            {
                new[]{ "移除 后台绑核收缩与后台移核 后台不再被限定核心", "Removed background core shrinking and background core relocation; background work is no longer confined to specific cores." },
                new[]{ "移除 后台压制的省电与克制两档 以及等待热度确认的观测", "Removed the eco and restrained background tiers along with the wait for measured pressure." },
                new[]{ "调整 对局一开始就直接隔离全部后台 不再等它们先抢资源", "Adjusted background suppression to isolate everything from the moment a match starts, instead of waiting for contention." },
                new[]{ "调整 对局中的后台扫描改由进程变动驱动 新出现的进程被压制得更快", "Adjusted in-match background scanning to be driven by process-set changes, so newly appearing processes are suppressed sooner." },
                new[]{ "调整 笔记本用电池时专注档不再照搬插电的电源参数 只放开纯省电项", "Adjusted the Focus tier on laptop battery to stop copying the plugged-in power settings, relaxing only the pure power-saving items." },
                new[]{ "调整 笔记本专注档不再强制所有核心不停泊", "Adjusted the Focus tier on laptops to stop forcing every core unparked." },
                new[]{ "新增 笔记本插电打专注档时把电源滑块拨到最佳性能 退出对局还原", "Added switching the power slider to Best Performance for Focus-tier matches on laptop AC power, restored when the match ends." },
                new[]{ "新增 体检可读出处理器封装与核心功耗 不需要安装驱动", "Added processor package and core power readings to the checkup, with no driver install required." },
                new[]{ "新增 笔记本对局中把共享功耗预算从 CPU 让给显卡 默认关闭 实验项", "Added an experimental, off-by-default option that yields shared power budget from the CPU to the GPU during laptop matches." },
                new[]{ "移除 独立配置页的套用到其它游戏按钮", "Removed the apply-to-other-games button from the per-game profile page." },
                new[]{ "移除 界面动画期间提升全局系统计时器精度", "Removed the global system timer precision boost during UI animation." },
                new[]{ "修复 无超线程的混合架构上显卡中断被投到低频能效核", "Fixed GPU interrupts being placed on low-clock efficiency cores on hybrid CPUs without hyper-threading." },
                new[]{ "修复 换了文件名的另一个 Pavise 构建会被当作后台进程压制", "Fixed another Pavise build with a different file name being suppressed as a background process." },
            }),
            new ReleaseNote("1.9.8.9", "2026-08-22", new[]
            {
                new[]{ "新增 游戏进程发现快速通道 游戏启动后更快被识别与接管", "Added a fast path for game process discovery; games are recognized and taken over sooner after launch." },
                new[]{ "新增 待命阶段提前写入 NVIDIA 逐游戏调优 本次启动即可生效 不再等下一局", "Added pre-staging of NVIDIA per-game tuning while on standby, so it takes effect on this launch instead of the next." },
                new[]{ "新增 双显卡机器待命阶段预选高性能显卡 避免游戏偶发跑核显 退场还原", "Added standby pre-selection of the high-performance GPU on dual-GPU machines, preventing games from occasionally launching on the integrated GPU; restored when the session ends." },
                new[]{ "新增 系统环境新增全局计时器分辨率实验开关 重启后生效", "Added an experimental global timer resolution switch under System Environment; takes effect after a reboot." },
                new[]{ "新增 体检检测 DirectStorage 直通是否被阻断 并点名阻断的驱动", "Added a checkup item that detects whether DirectStorage bypass reads are blocked and names the blocking driver." },
                new[]{ "新增 对局中观测 CPU 性能限制 撞功率墙或温度墙会写明程度与时长", "Added in-match CPU performance limit observation; hitting a power or thermal wall is reported with its depth and duration." },
                new[]{ "新增 开局后扫描注入游戏进程的覆盖层并在日志点名", "Added a post-launch scan that names overlay injections found in the game process in the log." },
                new[]{ "新增 体检说明内存通道布局 单通道与非对称混合会被指出", "Added a checkup note on memory channel layout; single-channel and asymmetric mixes are called out." },
                new[]{ "新增 中断选核弹窗可选把该设备的中断优先级提到 High 撤销时一并还原", "Added an option in the interrupt core picker to raise the device's interrupt priority to High, restored together with the revert." },
                new[]{ "调整 托盘右键菜单精简为守护开关 开机启动与退出 其余功能双击图标进面板", "Adjusted the tray menu down to the guard switch, autostart and exit; everything else lives in the panel, opened by double-clicking the icon." },
                new[]{ "新增 系统环境内核调度新增计时器恒定节拍开关 重启后生效", "Added a constant timer tick switch under System Environment kernel scheduling; takes effect after a reboot." },
                new[]{ "移除 体检页平台时钟校正项 时钟相关改动统一归系统环境的计时器开关管理", "Removed the platform timer correction item from the checkup page; clock-related changes are now managed by the timer switch under System Environment." },
                new[]{ "移除 AMD 显卡相关优化与体检项 显卡设置交还 AMD 软件本身", "Removed AMD GPU optimizations and audit items; GPU settings are handed back to AMD's own software." },
                new[]{ "移除 禁止 Ansel 注入游戏 残留由设置页清除按钮还原", "Removed blocking Ansel injection into games; residue is restored via the cleanup button on the Settings page." },
                new[]{ "移除 全部旧版本数据迁移与残留清理逻辑 程序不再自动改动任何历史配置", "Removed all legacy data migration and residue cleanup logic; the app no longer touches historical configuration on its own." },
                new[]{ "新增 中断体检 软件自己造负载当场观测 不必先打几局游戏 按 Esc 可随时中断", "Added the interrupt checkup: the app generates its own load and observes on the spot, with no need to play matches first. Press Esc to stop early." },
                new[]{ "修复 中断体检进度未走完就提前结束", "Fixed the interrupt checkup ending before the progress bar completed." },
                new[]{ "移除 专注档资源牢笼 后台压制不再使用 Job 硬性限速", "Removed the Focus resource cage; background suppression no longer uses hard Job rate caps." },
                new[]{ "修复 驱动调优遇驱动临时忙碌时本局不再重试", "Fixed driver tuning never retrying within the session after hitting a temporarily busy driver." },
                new[]{ "修复 对局在电源方案切换完成前结束时 机器可能停留在调优方案上", "Fixed the machine possibly staying on the tuned power plan when a match ended before the plan switch finished." },
                new[]{ "调整 MMCSS 多媒体调度不再写入两项系统并不读取的参数", "Adjusted MMCSS multimedia scheduling to stop writing two parameters the system does not read." },
                new[]{ "新增 体检给出建议目标核心 不适合改动的设备会说明原因", "Added suggested target cores from the checkup, with a stated reason for every device it advises against changing." },
                new[]{ "新增 显卡驱动容器与硬件功耗散热控制工具自动豁免后台压制", "Added automatic suppression exemption for graphics driver containers and hardware power/thermal control utilities." },
                new[]{ "调整 恢复智能保帧 CPU 持续吃满十秒且帧线程未接管时游戏本体让回普通优先级 旧版一秒即降的过敏已修掉", "Adjusted: the frame guard is back - when the CPU stays saturated for ten seconds with no render-thread takeover, the game process yields to normal priority; the old one-second hair trigger is gone." },
                new[]{ "修复 笔记本对局中核心停泊未解除 智能档插电只放开一半核心", "Fixed core parking staying active in-match on laptops; the Smart tier only unparked half the cores on AC power." },
                new[]{ "修复 反作弊相容名单的游戏仍被写入显卡调度优先级 名单语义恢复为完全零接触", "Fixed roster-protected anti-cheat games still receiving GPU scheduling priority writes; the roster once again means zero contact." },
                new[]{ "调整 帧线程识别开局数秒内完成 并定期复核 钉错的线程会被自动解除并重新识别", "Adjusted frame thread identification to complete within seconds of a match starting, with periodic re-checks that automatically unpin and re-identify a wrongly pinned thread." },
                new[]{ "修复 游戏退出后仍显示待命 WeGame 等平台常驻进程不再触发就绪", "Fixed the standby label lingering after a game exits; resident platform processes such as WeGame no longer count as a ready signal." },
                new[]{ "修复 游戏平台装在自定义目录时豁免失效 卸载记录缺少安装位置也能认出", "Fixed platform exemptions failing for custom install locations; platforms are now recognized even when their uninstall entry lacks an install path." },
                new[]{ "修复 网卡在中断页整行空白 现在按设备类归并框架驱动的数据", "Fixed network adapters showing an empty row on the Interrupts page; framework driver data now folds into the matching device class." },
                new[]{ "修复 中断观测丢事件时仍采信数据 现在整轮作废", "Fixed interrupt observation trusting data from a round that dropped events; such a round is now discarded." },
                new[]{ "修复 智能预设首轮不压制任何进程 一局下来收益接近于零", "Fixed the Smart preset suppressing nothing on its first sweep, leaving a match with almost no gain." },
                new[]{ "修复 电源计划调优值超出本机取值范围时整项放弃 现在夹到范围内再写", "Fixed a power plan value being abandoned when it fell outside this machine's allowed range; it is now clamped into range and written." },
                new[]{ "修复 电源计划写入失败只说失败不说原因 现在给出实际错误码", "Fixed power plan write failures reporting no reason; the actual error code is now shown." },
                new[]{ "修复 电源计划一项写入失败就整轮判失败 两轮后开关会被自动关掉", "Fixed a single failed power plan write failing the whole round, which switched the toggle off after two rounds." },
                new[]{ "修复 已经是目标值的项目仍被写入并认领 退出时又写回一次", "Fixed items already at the target value still being written and claimed, then written back again on exit." },
                new[]{ "修复 窗口化游戏优化 硬件加速 游戏模式在系统已开启时被重复写入", "Fixed windowed game optimization, hardware acceleration, and Game Mode being written again when the system already had them on." },
                new[]{ "修复 VBS 项原本不存在时被写成 auto 而不是删除", "Fixed the VBS entry being written as auto instead of deleted when it did not exist originally." },
                new[]{ "修复 NVIDIA 未知驱动键被误当成帧率上限处理", "Fixed unknown NVIDIA driver keys being treated as the frame rate cap." },
                new[]{ "修复 反作弊进程打不开时崩溃台账被提前清掉", "Fixed the crash ledger being cleared early when an anti-cheat process could not be opened." },
                new[]{ "修复 环境项启用失败时先关开关后还原 可能留下残留", "Fixed an environment item failing to enable turning its switch off before restoring, which could leave residue." },
                new[]{ "修复 开机自启刷新失败时提示不实 现在给出实际错误码", "Fixed the misleading message when refreshing the startup task failed; the actual error code is now shown." },
                new[]{ "调整 挪核弹窗不再预选核心 移除使用推荐值按钮", "Adjusted the pin dialog: no cores are preselected and the recommended-value button is gone." },
                new[]{ "调整 中断页改为显示 p99 区间 不再显示单次最长", "Adjusted the Interrupts page to show a p99 band instead of the single longest run." },
                new[]{ "调整 多队列网卡与存储控制器在挪核前给出风险提示", "Adjusted multi-queue network adapters and storage controllers to warn before pinning." },
                new[]{ "调整 扫描动画重做 系统体检与中断体检共用一套", "Redesigned the scan animation, now shared by the system checkup and the interrupt checkup." },
                new[]{ "调整 一键恢复改为只保留重置 不再还原历史版本写过的项", "Adjusted one-click restore down to reset only; items written by older versions are no longer restored." },
                new[]{ "移除 三代已退役的中断亲和空壳代码", "Removed the shell code of three retired generations of interrupt affinity switches." },
                new[]{ "移除 游戏内帧率小窗 取帧要常驻内核会话 部分机器直接失败 另一些每局中止 拿不到数据还照付开销", "Removed the in-game frame rate overlay: capturing frames needed a resident kernel session that failed outright on some machines and aborted every match on others, costing overhead with no data to show." },
                new[]{ "新增 体检页检出容错堆 可一键解除 可还原", "Added a fault-tolerant heap check to the Checkup page, removable in one click and reversible." },
                new[]{ "移除 内存吃紧时清理待机内存 实测触发条件无法自解除 每 45 秒重复且单次只释放 0.1MB", "Removed purging standby memory when memory runs low: the trigger condition could never clear itself, so it repeated every 45 seconds while releasing 0.1MB per run." },
                new[]{ "调整 后台压制只压真抢资源的进程 闲置进程智能不碰 专注只降省电档", "Adjusted: background suppression now targets only processes actually taking resources; Smart leaves idle ones alone, Focus only drops them to eco." },
                new[]{ "修复 对局让位过早把优化器自己饿死 首轮清扫极慢 重负载下升不了档", "Fixed: yielding too early starved the optimizer itself, making the first sweep crawl and heavy load impossible to escalate." },
                new[]{ "修复 电源方案下发不再卡主循环", "Fixed: power plan enforcement no longer blocks the main loop." },
                new[]{ "修复 中断页重启后仍显示待重启 新增待验证状态 区分没重启和没观测到中断", "Fixed: the Interrupts page still showed pending-reboot after a reboot. A new unverified state separates not-yet-rebooted from not-yet-observed." },
                new[]{ "调整 重压后台绑核收缩的门槛从 7 个物理核降回 5 个 此前被关闭的请到设置页自行打开", "Adjusted: the core-squeeze threshold drops from 7 physical cores back to 5; if an earlier build turned it off, re-enable it on the Settings page." },
                new[]{ "修复 一键清除时中断亲和的还原顺序反了 会把两份备份一起打光", "Fixed: the interrupt affinity restore order during a one-click cleanup was backwards and could spend both backups at once." },
                new[]{ "修复 中断页钉设备前没查退役台账 可能把残留值当成原值", "Fixed: pinning a device did not check the retired ledgers, so a leftover value could be recorded as the original." },
                new[]{ "调整 中断页排版 按钮并到顶部 设备列表撑满剩余高度", "Adjusted the Interrupts page layout: buttons on one top row, the device list fills the remaining height." },
                new[]{ "调整 显卡驱动接口不可用时日志写明原因 快照留到下次启动继续还原", "Adjusted: when the graphics driver interface is unavailable the log says so, and snapshots are kept for the next startup to retry." },
                new[]{ "修复 启动时会把调好的设置整套还原 有一项还原失败就每次开机重来一遍", "Fixed: startup could restore every applied setting at once, and one failed step made it repeat on every launch." },
                new[]{ "调整 启动与版本更新不再改动机器上任何已经生效的值 还原只由设置页那颗清除按钮触发", "Adjusted: startup and version upgrades no longer change any value already in effect on the machine; restoring happens only via the cleanup button on the Settings page." },
                new[]{ "调整 NVIDIA 逐游戏驱动写入改为对局结束即还原 驱动里不留常驻值 手动改过的键不覆盖", "Adjusted: per-game NVIDIA driver writes now restore when the match ends, leaving no resident values in the driver; manually changed keys are never overwritten." },
                new[]{ "修复 新驱动已移除超低延迟键时不再每局报错 自动降级为低延迟 换驱动后自动重试", "Fixed: when the driver no longer has the Ultra Low Latency keys, no more errors every match; it downgrades to low latency and retries after a driver change." },
                new[]{ "新增 对局禁用处理器空闲改为可选开关 默认关闭 只在专注档插电时生效 升级时重置为关闭", "Added: disabling processor idle during a match is back as an opt-in switch, off by default, Focus on AC only, reset to off on upgrade." },
                new[]{ "移除 前台时间片的三档选择 两个写入值在内核里行为相同 判据交给体检页", "Removed the three-way foreground time slice picker; the two written values behaved identically, judgment moves to the Checkup page." },
                new[]{ "调整 前台时间片判据只看微软有文档的低两位 修复也只改这两位 其余位保留", "Adjusted: the foreground time-slice check and fix now touch only the two documented bits; all other bits are preserved." },
                new[]{ "调整 体检里这一项的说明只讲机制不讲收益", "Adjusted: this checkup item's wording describes the mechanism only, never a benefit." },
                new[]{ "新增 中断页 列出所有可改中断亲和的设备 可扫描测量并钉到指定核 全程可撤销", "Added an Interrupts page: every pinnable device listed, scan on demand, pin to chosen cores, fully reversible." },
                new[]{ "重要 中断亲和写入后需重启才生效 已写入与已生效分开显示", "Important: interrupt affinity takes effect after a reboot; written and effective are shown separately." },
                new[]{ "新增 中断页给出判读微秒数的尺子 每台设备标出分档与超时次数", "Added a yardstick for the microsecond numbers, with a grade and over-threshold count per device." },
                new[]{ "新增 每台设备可单独选中断落到哪几个核 预选按处理器架构给出推荐值", "Added a per-device core picker with an architecture-based pre-selection." },
                new[]{ "调整 中断页交互重做 扫一次 挑一台 钉住三步 表格七列减到四列", "Adjusted: the Interrupts page reworked into scan, pick, pin, with the table cut from seven columns to four." },
                new[]{ "新增 可能带着键鼠的设备会被标出来 钉住主控会把整条总线的输入中断一起挪走", "Added a marker for devices that may carry your keyboard and mouse; pinning the controller moves the whole bus." },
                new[]{ "修复 中断亲和的可撤销性 改成先记名单再动手 中途崩溃也能还原", "Fixed interrupt affinity reversibility: the ledger is written before the registry, so a crash can always be undone." },
                new[]{ "移除 体检页的中断分布与中断负载两项 判据交给中断页", "Removed the interrupt distribution and load rows from the Checkup page; the Interrupts page judges instead." },
                new[]{ "移除 环境页的显卡中断亲和开关 并入中断页 旧值升级时自动还原", "Removed the Environment page GPU interrupt affinity switch; it lives on the Interrupts page now, old values restored on upgrade." },
                new[]{ "移除 帧卡顿溯源整维下架 设置与残留升级后自动清除", "Removed frame stutter attribution in full; settings and residue are cleared on upgrade." },
                new[]{ "移除 对局禁用处理器空闲状态 该项彻底下架 旧残留升级后自动清除", "Removed disabling processor idle during a match for good; residue is cleared on upgrade." },
                new[]{ "调整 源码主体注释全部移除 每个文件只保留作者与文件用途两行", "Adjusted: all in-body source comments were removed; each file keeps only its author and purpose header." },
                new[]{ "重要 升级到本版会清除此前所有旧版本数据 游戏库 白名单 配置与设置一并重置 请重新添加游戏 全新安装不受影响", "Important: upgrading to this version wipes all data from any earlier version — game library, whitelist, configuration and settings are all reset, so you'll need to re-add your games. Fresh installs are unaffected." },
                new[]{ "调整 智能档后台压制重做 按每个进程自己的热度逐级升档 前台家族不动", "Adjusted: Smart suppression rebuilt to escalate per-process by heat; the foreground family is never touched." },
                new[]{ "新增 帧线程接管后本体让位开关", "Added a switch for the game process to yield once the frame thread is boosted." },
                new[]{ "调整 被反作弊拒绝写入的游戏只跳过必然失败的项 其余照常尝试", "Adjusted: anti-cheat-protected games now skip only the writes certain to fail." },
                new[]{ "新增 MMCSS 多媒体调度重新上线 关闭或退出即还原 需管理员权限", "Added MMCSS multimedia scheduling back; restored on switch-off or exit, requires admin." },
                new[]{ "新增 前台时间片可选前台加权 三档自选", "Added a foreground-weighted option for the foreground time slice with three choices." },
                new[]{ "新增 对局禁用处理器空闲重新上线 默认关闭 专注档插电时生效 低于睿频基线自动关掉", "Added disabling processor idle back, off by default, Focus on AC only, auto-off if turbo falls below baseline." },
                new[]{ "调整 智能档在台式机上把处理器能效偏好写回最偏性能 并关掉插电时的时钟占空比调制 瞬时升频比此前积极", "Adjusted: on desktops the Smart preset writes the processor energy-performance preference back to the most performance-biased value and turns off clock duty cycling on AC power, so it ramps up more eagerly than before." },
                new[]{ "新增 专注档资源牢笼 持续重负载的后台硬限到系统 CPU 的一成 意外退出自动解除", "Added the Focus resource cage: sustained heavy background hard-capped at ten percent CPU, lifted automatically on unexpected exit." },
                new[]{ "调整 重压后台绑核收缩在 6 核及以下机器上强制关闭并置灰", "Adjusted: core squeezing is forced off and greyed out on machines with 6 or fewer physical cores." },
                new[]{ "调整 设置页语言选择改为中文与 English 同时显示 点击目标语言直接切换 不再用单按钮来回轮换", "Adjusted: the Settings language selector now shows 中文 and English at the same time. Select the target language directly instead of cycling with a single button." },
                new[]{ "优化 通用弹窗顶部 TAG 增加上下留白 并统一顺延标题 正文与内容区 不再与标题贴得过紧", "Improved: the shared dialog TAG now has balanced spacing above and below, with the title, body, and content area shifted consistently so the header no longer feels cramped." },
                new[]{ "优化 白名单中只有进程名而没有 EXE 路径的内置保护项改用统一的盾牌进程图标 不再留下空白图标位", "Improved: built-in whitelist protections that have only a process name and no EXE path now use a unified shield-and-process icon instead of leaving the icon slot blank." },
                new[]{ "新增 白名单页直接展示四类自动豁免规则 并标出本机检测到的平台", "Added: the Whitelist page exposes the four automatic exemption categories and detected platforms." },
                new[]{ "新增 系统环境页可按游戏本体关闭控制流保护 CFG 只改该缓解位保留其它设置 下次启动游戏生效 关闭即逐个还原", "Added: the System Environment page can disable Control Flow Guard per game executable; it touches only that mitigation bit and preserves other settings, takes effect on the next game launch, and restores each one when turned off." },
                new[]{ "移除驱动级帧率上限 NVIDIA 与 AMD 一并下架 升级后自动还原驱动原值", "Removed the driver-level frame rate cap for both NVIDIA and AMD; original driver values are restored automatically on upgrade." },
                new[]{ "修复 对局核心解停泊覆盖在还原路径死循环 该覆盖与托管方案重复 已移除", "Fixed the in-match core-unparking override looping on restore; it duplicated the managed plan and was removed." },
                new[]{ "移除 USB 与硬盘控制器中断亲和 升级自动还原到系统默认", "Removed USB and storage controller interrupt affinity; restored to system default on upgrade." },
                new[]{ "移除 竞技档在台式机上禁用处理器空闲状态 托管方案恒不禁 idle 升级后自动还原", "Removed the competitive preset's disabling of C-states on desktops; the managed plan never disables idle, restored on upgrade." },
                new[]{ "修复 对局中切换模式或改全局设置要退出游戏才生效", "Fixed mode or global setting changes during a match not applying until the game closed." },
                new[]{ "移除 逐游戏强制独显偏好 已写过的升级时还原", "Removed the per-game forced discrete-GPU preference; written values restored on upgrade." },
                new[]{ "改名 常规模式更名为智能 竞技模式更名为专注 行为不变", "Renamed Standard to Smart and Competitive to Focus; behavior unchanged." },
                new[]{ "调整 专注档不再放行前台窗口族 只有白名单例外", "Adjusted: Focus no longer exempts the foreground window family; only the whitelist is exempt." },
                new[]{ "AMD Anti-Lag 不再被帧率上限互斥抑制 开关即生效", "AMD Anti-Lag is no longer suppressed by the frame cap conflict; the switch now always takes effect." },
                new[]{ "修复新装用户重启后配置被清空", "Fixed configuration being wiped after a restart on fresh installs." },
                new[]{ "修复进程状态记账残留把清除配置永久卡死", "Fixed leftover process state bookkeeping permanently blocking the configuration wipe." },
                new[]{ "修复恢复全部系统改动只还原运行态 持久改动一并还原", "Fixed Restore All System Changes only reverting runtime state; persistent changes are now restored too." },
                new[]{ "修复 X3D 双缓存平台对局中被解除核心停泊 手动指定频率核时才解除", "Fixed core parking being lifted on X3D dual-cache platforms during matches; now only lifted when the frequency CCD is chosen manually." },
                new[]{ "修复硬件加速 GPU 调度能力探测在所有机器上失败 不支持的机器现在置灰", "Fixed hardware-accelerated GPU scheduling capability detection failing on every machine; unsupported machines are now greyed out." },
                new[]{ "修复电源方案 功耗墙 驱动调优在用户手动改动后被强行写回", "Fixed the power plan, power limit and driver tuning overwriting values the user changed manually." },
                new[]{ "修复显卡 MSI 中断一键修复无确认 可能改坏刻意关闭的机器", "Fixed the GPU MSI interrupt one-click fix applying without confirmation on machines where it was disabled on purpose." },
                new[]{ "修复开机自启任务运行 72 小时后被任务计划强制结束", "Fixed the startup task being force-terminated by Task Scheduler after 72 hours." },
                new[]{ "调整中断亲和按核心布局选核 少性能核机型改投能效核", "Adjusted interrupt affinity core selection by topology; machines with few performance cores now target efficiency cores." },
                new[]{ "调整逐游戏 Game DVR 开关下架 全局开关不变", "Removed the per-game Game DVR switch; the global switch is unchanged." },
            }),

            new ReleaseNote("1.8.0.2", "2026-08-15", new[]
            {
                new[]{ "新增英文界面 设置页一键切换", "Added an English UI with one-click switching in Settings." },
                new[]{ "新增竞技档插电时禁用处理器闲置 电池上不启用", "Added: Competitive tier disables processor idle when plugged in; not on battery." },
                new[]{ "新增体检页顶部一键修复总按钮", "Added a master one-click fix button at the top of the Checkup page." },
                new[]{ "修复自定义主题色后启动即崩", "Fixed a startup crash after setting a custom mode accent color." },
                new[]{ "修复穿越火线等 TP/内核反作弊游戏对局中掉帧卡死", "Fixed frame drops and freezes in TP/kernel anti-cheat games such as CrossFire." },
                new[]{ "修复升级清数据在部分机器上无限中止", "Fixed the upgrade data purge aborting endlessly on some machines." },
                new[]{ "修复对局电源方案在退出后偶发滞留", "Fixed the match power plan occasionally lingering after exit." },
                new[]{ "修复 svchost 等后台进程被误判为外设而豁免压制", "Fixed background processes like svchost being misidentified as peripherals and exempted from suppression." },
                new[]{ "移除英雄联盟专栏", "Removed the League of Legends Spotlight." },
                new[]{ "移除极限模式 竞技即最高档", "Removed Extreme mode; Competitive is the top tier." },
                new[]{ "移除暂停搜索索引与预读服务 旧版本停掉的自动恢复", "Removed search-indexing/prefetch service pausing; services stopped by older versions are restored automatically." },
                new[]{ "移除电源滑块最佳性能 已切换过的自动还原", "Removed the Best Performance power slider setting; machines already switched are restored automatically." },
                new[]{ "移除服务让路与后台上传让位", "Removed service yielding and background upload yielding." },
                new[]{ "调整常规模式压制升压提速 重负载后台更快隔离", "Adjusted Standard mode to escalate suppression faster on heavy background load." },
                new[]{ "调整 Game DVR 关闭为游戏模式期间常驻 退出还原", "Adjusted Game DVR disabling to stay applied while game mode is on, restored on exit." },
                new[]{ "调整电源计划为设一次 不再每 30 秒强制拉回", "Adjusted power plan to set once instead of forcing it back every 30 seconds." },
                new[]{ "调整待机内存清理为内存吃紧时触发 默认开启", "Adjusted standby memory cleanup to trigger only when memory is tight; on by default." },
                new[]{ "调整 AMD 帧率上限走 Radeon Chill 老卡自动回退", "Adjusted AMD frame rate cap to use Radeon Chill, falling back automatically on older cards." },
                new[]{ "本版配置结构大改 升级首次启动清空旧版本全部数据并还原系统改动", "Configuration overhaul: first launch after upgrading wipes all old data and restores system changes." },
            }),
            new ReleaseNote("1.8.0.1", "2026-08-14", new[]
            {
                new[]{ "因为呼声过高 游戏专栏回归 目前仅有英雄联盟专栏", "By popular demand, game Spotlight is back. Currently only a League of Legends Spotlight." },
                new[]{ "新增 AMD 显卡调优", "Added AMD GPU tuning" },
                new[]{ "新增逐游戏独立配置 未覆盖跟随全局 生效值在对局激活瞬间定格 对局中的任何修改含自定义核心都是下一局生效", "Added per-game profiles. Games without one follow global settings. Effective values are frozen the moment a match activates; any change during a match, including custom cores, takes effect next match." },
                new[]{ "概览页和托盘标注实际生效模式来自哪个游戏", "Overview page and tray now show which game the actual active mode comes from" },
                new[]{ "游戏库条目支持重命名 英雄联盟条目带专栏小标", "Game library entries can be renamed; the League of Legends entry carries a Spotlight badge" },
                new[]{ "右上角搜索覆盖全部页面含独立配置二级页 命中直达定位 覆盖计数可点击逐项跳转", "Top-right search now covers every page, including per-game profile sub-pages. Hits jump straight to their location, and the coverage count is clickable to step through items one by one." },
                new[]{ "体检页新增一键修复 MSI 中断 前台时间片 平台时钟 网络限流 键鼠队列检出异常直接修 修完自动复检 可还原", "Checkup page adds one-click fix: detected anomalies in MSI interrupts, foreground time slice, platform clock, network throttling, and keyboard/mouse queues are fixed directly, auto-rechecked afterward, and reversible." },
                new[]{ "驱动级帧率上限改为滑块 30 到 500 滚轮逐帧微调 NVIDIA 与 AMD 同步", "Driver-level frame rate cap is now a slider from 30 to 500, with per-frame mouse-wheel fine-tuning. NVIDIA and AMD in sync." },
                new[]{ "对局电源计划默认 PG 托管方案 首次对局自动创建 可在策略页选回自己的计划", "Match power plan defaults to the PG managed plan, created automatically on the first match. You can switch back to your own plan on the strategy page." },
                new[]{ "窗口化游戏优化已修复 目前也是回归", "Windowed game optimization is fixed; this too is a returning feature." },
                new[]{ "游戏档案升到 V5 驱动写入连续失败自动关开关时该游戏的对应覆盖一并清除", "Game profiles upgraded to V5. When consecutive driver write failures auto-disable a switch, that game's corresponding overrides are cleared as well." },
                new[]{ "修复清除数据时 NVIDIA 驱动项恢复失败仍删快照 现在任何一项还原失败整体中止", "Fixed data clearing still deleting the snapshot when restoring NVIDIA driver items failed; now any single restore failure aborts the whole operation." },
                new[]{ "修复关闭驱动调优失败后无人重试 启动时自动补还原残留快照", "Fixed nothing retrying after disabling driver tuning failed; leftover snapshots are now restored automatically at startup." },
                new[]{ "修复关闭 VBS 失败残留快照 之后清数据会改写用户自己设置的 hypervisor 启动项", "Fixed a failed VBS disable leaving a stale snapshot, which made a later data clear overwrite the user's own hypervisor boot setting." },
                new[]{ "修复便携标志丢失时数据被搬进本机并删源 现在只复制不删除不覆盖", "Fixed data being moved into the local machine with the source deleted when the portable flag was lost; now it only copies, never deletes or overwrites." },
                new[]{ "修复档案首行损坏被当空库 下次保存覆盖掉可恢复内容 现在只读保护并备份", "Fixed a corrupted first line in the profile file being treated as an empty library, letting the next save overwrite recoverable content; now it becomes read-only protected and backed up." },
                new[]{ "修复扫描 Program Files 这类目录会把整个目录当成一款游戏入库", "Fixed scanning directories like Program Files adding the entire directory to the library as one game" },
                new[]{ "修复体检 MSI 统计到已拔掉的历史显卡 只统计在场设备", "Fixed checkup MSI stats counting historical GPUs that were already unplugged; only present devices are counted now" },
                new[]{ "修复 59Hz 与 60Hz 同一物理模式被判为刷新率损失", "Fixed 59Hz and 60Hz of the same physical mode being flagged as a refresh rate loss" },
                new[]{ "修复对局中增删白名单和关闭上传限速会卡住界面 改为后台执行", "Fixed the UI freezing when adding/removing whitelist entries or disabling upload throttling mid-match; now runs in the background" },
                new[]{ "修复联系方式弹窗打开期间空转一个核心", "Fixed one core spinning idle while the contact dialog was open" },
                new[]{ "修复 GPU 降频探测在驱动重启后永久失效 体检中断会话在程序被杀后残留", "Fixed the GPU throttling probe permanently breaking after a driver restart, and checkup interrupt sessions lingering after the app was killed" },
                new[]{ "修复若干位图泄漏 亮暗切换会退出独立配置页 托盘菜单不随缩放调整", "Fixed several bitmap leaks, light/dark switching exiting the per-game profile page, and the tray menu not adjusting with scaling" },
                new[]{ "移除 旧版本兼容 V4 及更早档案不再读取 旧档案只读保护不覆写", "Removed legacy compatibility: V4 and earlier archives are no longer read and stay read-only." },
                new[]{ "移除两个开关 禁止前台无输入降级与逐游戏强制独显改为始终开启", "Removed two switches: prevent-foreground-no-input-demotion and per-game forced discrete GPU are now always on." },
                new[]{ "移除环境页五个修复型开关 对应能力并入体检页一键修复", "Removed five fix-type switches from the environment page; their capabilities merged into the checkup page's one-click fix." },
                new[]{ "移除体检页 NVIDIA 写入实测和鼠标回报率实测", "Removed the checkup page's NVIDIA write test and mouse polling rate test" },
            }),
            new ReleaseNote("1.7.1", "2026-08-13", new[]
            {
                new[]{ "新增关闭指针精度增强 默认关 关掉系统那条鼠标加速曲线 立即生效 可还原", "Added disabling Enhance Pointer Precision, off by default. Turns off the system's mouse acceleration curve. Takes effect immediately; reversible." },
                new[]{ "新增辅助功能按键拦截 清掉筛选键 粘滞键 切换键 键鼠里唯一能救回两位数毫秒的软件项 可还原", "Added removal of accessibility key interception: clears Filter Keys, Sticky Keys, and Toggle Keys — the only software item in the keyboard/mouse chain that can win back double-digit milliseconds. Reversible." },
                new[]{ "新增禁止键鼠设备选择性暂停 治空闲一会儿之后第一下操作发飘 只碰键鼠 不碰 U 盘声卡 可还原", "Added blocking selective suspend for keyboard/mouse devices, curing the floaty first input after a short idle. Touches only keyboard and mouse — not USB drives or sound cards. Reversible." },
                new[]{ "新增键鼠队列校正 把被第三方工具改过的队列长度改回系统默认", "Added keyboard/mouse queue correction: queue lengths changed by third-party tools are set back to system defaults" },
                new[]{ "USB 中断避让改成开启前先弹窗说明代价 只有 4K 以上回报率的鼠标值得开 1000Hz 及以下基本没收益", "USB interrupt avoidance now shows a dialog explaining the cost before enabling. Only mice with 4K+ polling rates are worth it; 1000Hz and below gains basically nothing." },
                new[]{ "USB 中断避让新增开机自检 换过 CPU 或在 BIOS 改过核心数 失效的掩码会自动还原", "USB interrupt avoidance adds a boot-time self-check: if you swapped the CPU or changed core counts in BIOS, invalidated masks are restored automatically." },
                new[]{ "体检新增键鼠链路整段 鼠标回报率实测 延迟量级说明 蓝牙键鼠会被点名 只读不改", "Checkup adds a full keyboard/mouse chain section: measured mouse polling rate, latency magnitude notes, and Bluetooth keyboards/mice called out by name. Read-only, changes nothing." },
                new[]{ "新增重压后台绑核收缩 默认开 治后台抢内存带宽 台架实测 33 帧到 95 帧", "Added heavy-suppression background core shrinking, on by default. Cures background processes stealing memory bandwidth; bench-verified 33 fps to 95 fps." },
                new[]{ "收缩落点跟着你划的游戏核实时算 多 CCD 会整个躲开游戏核那块 L3 这项不需要你设置", "The shrink target is computed in real time from the game cores you assigned. On multi-CCD chips it steers clear of the entire L3 block holding the game cores. Nothing for you to configure." },
                new[]{ "新增对局稳定后回收后台工作集 默认关 代价是切回那些程序头一下会顿", "Added reclaiming background working sets once the match stabilizes, off by default. The cost: those programs stutter the first moment you switch back to them." },
                new[]{ "新增待机列表超阈值时清空 默认关 用过 ISLC 的开这个", "Added purging the standby list when it exceeds a threshold, off by default. If you've used ISLC, turn this on." },
                new[]{ "对局前清理低优先级待机内存重新上线 默认关 内存三项不再被极限模式强制", "Pre-match low-priority standby memory cleanup is back online, off by default. The three memory items are no longer forced by Extreme mode." },
                new[]{ "对局电源计划默认改成卓越性能 本机没有会自动建一份 有就直接用你那份", "Match power plan now defaults to Ultimate Performance. Created automatically if this machine doesn't have it; if it does, yours is used directly." },
                new[]{ "托管方案降成下拉最后一项 改名 PG 竞技 带本机签名 一键还原会连它删掉", "The managed plan is demoted to the last item in the dropdown, renamed PG Competitive with a local-machine signature. One-click restore deletes it as well." },
                new[]{ "升级时旧的 Pavise 竞技方案自动删除 正在用它会先切走再删", "The old Pavise Competitive plan is deleted automatically on upgrade; if it's in use, we switch away first, then delete." },
                new[]{ "检查更新改成九条线路并发赛跑 直连不通自动落到 ghproxy 和 jsDelivr 镜像", "Check-for-updates now races nine routes concurrently. If direct connection fails, it falls back to the ghproxy and jsDelivr mirrors." },
                new[]{ "下载地址跟着通的那条线路走 新增官方网盘入口 只允许打开可信域名", "The download URL follows whichever route got through. Added an official cloud drive entry; only trusted domains are allowed to open." },
                new[]{ "游戏库新增强制接管 模拟器 云游戏这类识别不到的 进程一起来就进对局 开启前会说明代价", "Game library adds forced takeover for emulators, cloud gaming, and other unrecognizable titles: the moment the process starts, a match begins. The cost is explained before enabling." },
                new[]{ "添加游戏改成实时 每 3 秒自动补上新开的程序 扫描和 GPU 采样各阶段都有进度提示", "Add-game is now live: newly launched programs are picked up automatically every 3 seconds, with progress hints through the scanning and GPU sampling stages." },
                new[]{ "核心分配手动档补了操作说明 已划核数挪到矩阵上方 新增一行重压后台落点", "Manual core allocation gains usage notes, the assigned-core count moved above the matrix, and a new row shows where heavily suppressed background lands." },
                new[]{ "策略页被预设锁住的开关写明是强制开还是强制关 暂停索引和预取的说明原来写反了", "Switches locked by a preset on the strategy page now state whether they're forced on or forced off; the descriptions for pausing indexing and prefetch had been swapped." },
                new[]{ "打开主界面不再自动弹窗 版本说明和反馈交流都改成到关于页手动打开", "Opening the main window no longer auto-pops dialogs; release notes and feedback chat are now opened manually from the About page." },
                new[]{ "修复大小核机型的后台压制等于没做 现在挤核叠在核心分区之上", "Fixed background suppression amounting to nothing on hybrid (P/E-core) machines; core squeezing now stacks on top of core partitioning." },
                new[]{ "修复挤核会挤到你划给游戏的核上 现在只在后台能待的区域里收缩", "Fixed core squeezing pushing background onto the cores you assigned to the game; it now shrinks only within the region where background is allowed to stay." },
                new[]{ "修复后台档位在门槛上反复抖 一局 135 次降到 4 次", "Fixed the background tier flapping back and forth at the threshold: from 135 flips per match down to 4." },
                new[]{ "修复开局最要紧的十秒被停服务占住 停服务挪到对局稳定 20 秒后", "Fixed the crucial first ten seconds of a match being occupied by stopping services; service stops now wait until 20 seconds after the match stabilizes." },
                new[]{ "修复服务让路在核心不多的机器上从来没生效 暂停索引预取与它互相抵消", "Fixed service yielding never taking effect on machines with few cores; pausing indexing/prefetch was cancelling it out." },
                new[]{ "修复常规档在电池上会打开 USB 选择性暂停 现在四档都不打开它", "Fixed the Standard tier enabling USB selective suspend on battery; none of the four tiers enable it now." },
                new[]{ "修复核心矩阵三处看不清 后台落点标不出来 无超线程的大小核不分带 快捷选核出现重复按钮", "Fixed three legibility issues in the core matrix: background landing spots couldn't be marked, hybrid chips without hyper-threading had no band separation, and quick core-select showed duplicate buttons." },
                new[]{ "修复版本说明弹窗打开时卡顿 改成整卡自绘 控件从 293 个降到 17 个", "Fixed stutter when opening the release notes dialog; it's now a fully self-drawn card, down from 293 controls to 17." },
                new[]{ "对局中托盘提示的刷新从 1.5 秒放宽到 6 秒", "In-match tray tooltip refresh relaxed from 1.5 seconds to 6 seconds" },
                new[]{ "移除窗口化游戏优化 交回系统设置里由你自己决定 升级后自动还原", "Removed windowed game optimization, handing it back to system settings for you to decide. Auto-restored after upgrade." },
                new[]{ "移除后台硬限帧 升级后自动还原", "Removed background hard frame cap. Auto-restored after upgrade." },
                new[]{ "移除游戏文件预热", "Removed game file preheating" },
            }),
            new ReleaseNote("1.7.0.6", "2026-08-11", new[]
            {
                new[]{ "新增核心分配 全核 只用大核 手动三选一 手动可以逐个逻辑核挑 只在对局中下发 退出自动还原", "Added core allocation: all cores, P-cores only, or manual — pick one. Manual lets you choose logical cores individually. Applied only during a match, auto-restored on exit." },
                new[]{ "修复上传让位一直没生效 写的是旧版策略格式 现在的系统不认 升级后才真的开始限速", "Fixed upload yielding never having worked: it wrote the legacy policy format that current systems don't recognize. Throttling only truly starts after this upgrade." },
                new[]{ "修复 Steam 覆盖层被当成普通后台压制 平台豁免改成按安装目录识别", "Fixed the Steam overlay being suppressed like an ordinary background process; platform exemption now identifies by install directory." },
                new[]{ "移除对局内存驻留", "Removed in-match memory pinning" },
                new[]{ "移除对局前清理待机内存", "Removed pre-match standby memory cleanup" },
                new[]{ "移除游戏时降级桌面视觉效果 升级后自动还原", "Removed downgrading desktop visual effects while gaming. Auto-restored after upgrade." },
                new[]{ "日志改成全中文 不再输出内部代号和十六进制掩码", "Logs are now entirely in Chinese; internal codenames and hex masks are no longer printed" },
            }),
            new ReleaseNote("1.7.0.5", "2026-08-11", new[]
            {
                new[]{ "移除 后台赶去集显 升级后自动还原写过的显卡偏好", "Removed pushing background apps to the iGPU; written GPU preferences restored on upgrade." },
                new[]{ "移除 禁用 MPO 排查开关 升级后自动还原 Pavise 写过的值", "Removed the MPO-disable switch; values Pavise wrote are restored on upgrade." },
            }),
            new ReleaseNote("1.7.0.4", "2026-08-11", new[]
            {
                new[]{ "游戏核心范围可以选绑哪块 双 CCD 机型两块任选 切换即时生效", "The game core range can now pick its CCD on dual-CCD machines, effective immediately." },
                new[]{ "电源计划改成一个下拉选完 用托管方案或你自己的计划", "The power plan is now one dropdown: the managed plan or your own." },
                new[]{ "优化策略页按 核心控制 预设细节 会话附加 分成三个标签 不再一列滚到底 设置搜索直达时自动切到对应标签", "Optimization strategy page is split into three tabs — Core Control, Preset Details, Session Extras — no more scrolling one long column. Settings search jumps auto-switch to the matching tab." },
                new[]{ "设置页新增清除全部配置 升级后遇到卡顿掉帧可以试试 把配置环境全部删除", "Settings page adds Clear All Configuration. If you hit stutter or frame drops after upgrading, try it: it deletes the entire configuration environment." },
                new[]{ "移除后台限速", "Removed background CPU throttling" },
                new[]{ "移除全屏游戏自动入库", "Removed automatic library addition of fullscreen games" },
                new[]{ "移除刷新率守护", "Removed refresh rate guardian" },
                new[]{ "移除自定义重压时清空后台内存", "Removed clearing background memory on custom heavy suppression" },
            }),
            new ReleaseNote("1.7.0.3", "2026-08-10", new[]
            {
                new[]{ "标题栏新增设置搜索 跨页面匹配 点击直达", "Title bar adds settings search: matches across pages, click to jump straight there" },
                new[]{ "移除禁用 CPU 空闲状态 升级后电源计划自动写回空闲允许", "Removed disabling CPU idle states; the power plan is auto-rewritten to allow idle after upgrade" },
                new[]{ "移除硬盘中断避让 升级后自动还原写入的值", "Removed disk interrupt avoidance; written values auto-restored after upgrade" },
                new[]{ "收到反馈 极限模式 不如 竞技模式 此版本进行了紧急修正", "Feedback came in that Extreme mode was worse than Competitive mode; this release ships an emergency correction." },
            }),
            new ReleaseNote("1.7.0.2", "2026-08-08", new[]
            {
                new[]{ "修复 对局中和挂机后按键鼠标可能变卡", "Fixed keys and mouse turning sluggish during or after a match." },
                new[]{ "游戏退出后如果有设置没还原成功 不再每 20 秒刷一条日志 改成写明卡在哪一项 静默重试", "If some settings fail to restore after a game exits, no more log line every 20 seconds; it now states exactly which item is stuck and retries silently." },
                new[]{ "新增 极限模式 在竞技之上把 GPU 让位和后台策略一次拉到位", "Added Extreme mode: GPU yield and background policy all-in above Competitive." },
                new[]{ "新增 后台限速 被强力压制的后台整体限到 5% CPU 极限模式强制开启", "Added a background cap at 5% CPU for strongly suppressed processes, forced on in Extreme." },
                new[]{ "竞技电源计划按 CPU 类别分方案 日志写明用的哪套", "The competitive power plan now varies by CPU class; the log names the set in use." },
                new[]{ "新增 后台赶去集显 双显卡本把被压制后台的显卡程序改走集显 默认关", "Added pushing suppressed background GPU apps to the iGPU on dual-GPU laptops, off by default." },
                new[]{ "核心分区改成独立手动选择 全核和单 CCD 随时切换", "Core partitioning is now a standalone manual choice between all cores and one CCD." },
                new[]{ "新增游戏文件预热 默认关闭 对局开始后把游戏文件悄悄读进缓存 加载和切图更顺 内存不足自动放弃", "Added game file preheating, off by default: after a match starts, game files are quietly read into cache for smoother loading and map transitions. Backs off automatically when memory is low." },
                new[]{ "新增对局内存驻留 默认关闭 内存紧张时把游戏内存钉住不被换出 内存充裕时不动作 退出解除", "Added in-match memory pinning, off by default: when memory is tight, game memory is pinned so it can't be paged out. Does nothing when memory is plentiful; released on exit." },
                new[]{ "新增后台上传让位 默认关闭 对局时限制被压制后台的上行带宽 网盘同步和 P2P 做种不再拖高延迟", "Added background upload yielding, off by default: limits the upstream bandwidth of suppressed background during a match, so cloud drive sync and P2P seeding no longer drive latency up." },
                new[]{ "系统环境页新增前台时间片校正 被老优化教程改过的时间片值修回系统默认 正常的不动", "System environment page adds foreground time-slice correction: time-slice values altered by old optimization guides are set back to system defaults; normal ones are left alone." },
                new[]{ "PUBG 塔科夫这类被反作弊挡住提优的游戏 概览页现在直接指路开 后备提优 不再只写进日志", "For games like PUBG and Tarkov where anti-cheat blocks the boost, the overview page now points you straight to enabling fallback boost instead of only writing it to the log." },
                new[]{ "大改体验页 自行查看", "Major overhaul of the experience page — see for yourself" },
                new[]{ "添加游戏支持直接从正在运行的程序里选 和 白名单一样", "Add-game now supports picking directly from running programs, same as the whitelist" },
            }),
            new ReleaseNote("1.7.0", "2026-08-08", new[]
            {
                new[]{ "智能保帧上线 默认开启 单独抬高决定帧数的那个线程", "Smart frame guard is live by default, boosting the frame-deciding thread alone." },
                new[]{ "后台 GPU 让位重新上线 后台吃满显卡时中位帧时间减半 尾部帧改善约四成", "Background GPU yielding is back online: when background saturates the GPU, median frame time halves and 1% lows improve about 40%." },
                new[]{ "移除 AMD 驱动调优 部分 A 卡有副作用 升级后自动还原相关驱动设置", "Removed AMD driver tuning: some AMD cards had side effects. Related driver settings are auto-restored after upgrade." },
                new[]{ "新增 QQ 交流二群 1101249532", "Added second QQ chat group: 1101249532" },
            }),
            new ReleaseNote("1.6.8.1", "2026-08-07", new[]
            {
                new[]{ "保帧线程重新上线 开关在策略页 沿用你之前的设置", "The frame-guard thread is back; the switch is on the Policy page with your previous setting kept." },
                new[]{ "竞技模式禁用 CPU 空闲状态重新上线 不适用的平台自动置灰", "Disabling CPU idle states in Competitive is back, greyed out on unsuitable platforms." },
                new[]{ "暂停索引和预取服务重新上线 机械盘建议开 SSD 不用", "Pausing indexing and prefetch services is back online: recommended for HDDs, unnecessary on SSDs" },
                new[]{ "这三项启动时不再被自动清理", "These three items are no longer auto-cleaned at startup" },
            }),
        };

        public static ReleaseNote Current
        {
            get
            {
                foreach (ReleaseNote n in All)
                    if (string.Equals(n.Version, App.Version, StringComparison.OrdinalIgnoreCase)) return n;
                return null;
            }
        }

        public static bool HasUnseen
        {
            get { return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase); }
        }

        public static void MarkSeen() { Settings.SaveStr(SeenKey, App.Version); }

#if PAVISE_SELFTEST
        internal static List<string> MissingTranslations()
        {
            var bad = new List<string>();
            foreach (ReleaseNote n in All)
                for (int i = 0; i < n.Count; i++)
                {
                    int prev = Lang.Cur;
                    try
                    {
                        for (int lang = 0; lang < 3; lang++)
                        {
                            Lang.Cur = lang;
                            if (string.IsNullOrEmpty(n.Item(i))) bad.Add(n.Version + " #" + i + " lang" + lang);
                        }
                    }
                    finally { Lang.Cur = prev; }
                }
            return bad;
        }
#endif
    }
}
