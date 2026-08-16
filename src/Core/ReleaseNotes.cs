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
            new ReleaseNote("1.8.0.3", "2026-08-16", new[]
            {
                new[]{ "重要 升级到本版会清除此前所有旧版本数据 游戏库 白名单 配置与设置一并重置 请重新添加游戏 全新安装不受影响", "Important: upgrading to this version wipes all data from any earlier version — game library, whitelist, configuration and settings are all reset, so you'll need to re-add your games. Fresh installs are unaffected." },
                new[]{ "新增 系统环境页可按游戏本体关闭控制流保护 CFG 只改该缓解位保留其它设置 下次启动游戏生效 关闭即逐个还原", "Added: the System Environment page can disable Control Flow Guard per game executable; it touches only that mitigation bit and preserves other settings, takes effect on the next game launch, and restores each one when turned off." },
                new[]{ "移除驱动级帧率上限 NVIDIA 与 AMD 一并下架 升级后自动还原驱动原值", "Removed the driver-level frame rate cap for both NVIDIA and AMD; original driver values are restored automatically on upgrade." },
                new[]{ "修复 独立的对局核心解停泊覆盖会在还原路径死循环 反复写回失败刷日志 该覆盖与托管电源计划的停泊设置重复 已移除 真正的停泊优化保留在托管方案内 旧版残留快照升级后自动清理", "Fixed: the standalone in-match core-unparking override could loop on its restore path, spamming repeated write-back failures. It duplicated the managed power plan's parking settings, so it was removed; the real parking optimization stays inside the managed plan, and legacy residue snapshots are cleaned up automatically on upgrade." },
                new[]{ "移除 USB 与硬盘控制器中断亲和 在 P 核少的混合架构上会把中断投到低频 E 核 造成高回报率鼠标空闲后首次点击延迟 升级自动把设备中断策略还原到系统默认 靠近渲染核的 GPU 中断亲和保留", "Removed USB and storage controller interrupt affinity: on hybrid CPUs with few P-cores it landed interrupts on low-clock E-cores, causing first-click delay on high-polling mice after idle. Device interrupt policy is restored to the system default on upgrade. GPU interrupt affinity, which targets cores near the render thread, is kept." },
                new[]{ "移除 竞技档在台式机上禁用处理器空闲状态 C-state 的行为 全核常驻 C0 只增功耗与发热 换不来帧 现代 CPU 从 C-state 唤醒是纳秒级 托管电源计划改为恒不禁 idle 保持系统默认空闲省电 升级后自动还原", "Removed the competitive preset's disabling of processor idle states (C-states) on desktops: pinning all cores at C0 only raises power and heat without gaining frames — modern CPUs wake from C-states in nanoseconds. The managed power plan now never disables idle, keeping the system default idle power saving; restored automatically on upgrade." },
                new[]{ "修复 对局进行中切换模式或修改全局设置不生效 必须退出游戏才能生效的问题 会话策略快照此前把全局值一并冻结 现改为只钉住每游戏覆盖项 全局改动下一轮扫描即时生效 每游戏固定的模式仍按覆盖优先", "Fixed: switching the mode or changing global settings during a match had no effect until the game was closed. The session policy snapshot used to freeze global values too; it now pins only per-game overrides, so global changes apply on the next sweep, while a per-game pinned mode still takes precedence." },
                new[]{ "移除 逐游戏强制独显偏好 按 exe 写注册表且下次启动才生效 属持久改动却混在对局链里 现代 Windows 本就默认给游戏挑独显 升级自动还原已写过的偏好 需要指定时可在系统设置的显示卡页自行设置", "Removed the per-game forced discrete-GPU preference: it wrote per-exe registry entries effective only on the next launch — a persistent change hiding inside the match pipeline — and modern Windows already defaults games to the discrete GPU. Previously written preferences are restored on upgrade; set it manually in Windows Settings > Display > Graphics if needed." },
                new[]{ "改名 常规模式更名为智能 竞技模式更名为独占 智能=按需自适应加压 独占=整台机器让给游戏 行为与档位对应关系不变", "Renamed: Standard mode is now Smart, Competitive mode is now Exclusive. Smart escalates adaptively on demand; Exclusive hands the whole machine to the game. Behavior and tier mapping are unchanged." },
                new[]{ "调整 独占档不再放行前台窗口族 切出游戏时切到的程序照压 只有白名单例外 游戏模式只专注游戏 此前 1.6 起会临时放行切出后的前台程序", "Adjusted: the Exclusive preset no longer exempts the foreground window family — whatever you alt-tab to stays suppressed, with only the whitelist exempt. Game mode focuses on the game alone. Since 1.6 the foreground app used to be let through temporarily." },
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
                new[]{ "移除旧版本兼容 V4 及更早档案不再读取 旧版游戏列表不再迁移 程序目录数据不再自动搬家 旧档案一律只读保护不覆写", "Removed legacy compatibility: V4 and earlier profiles are no longer read, old game lists are no longer migrated, program-directory data no longer auto-relocates, and old profiles are read-only protected and never overwritten." },
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
                new[]{ "移除后台赶去集显 它会把驱动录屏 视频播放这类后台程序也永久改走集显 而且对已在运行的程序无法生效 升级后自动还原写过的显卡偏好", "Removed pushing background apps to the iGPU: it also permanently rerouted driver recording and video playback background programs to the iGPU, and couldn't affect already-running programs. GPU preferences it wrote are auto-restored after upgrade." },
                new[]{ "移除禁用 MPO 排查开关 只有极少数驱动与显示器组合用得上 关掉它画面全部改走合成 抓屏类程序也可能受影响 升级后自动还原 Pavise 写过的值 你自己改的不动", "Removed the disable-MPO troubleshooting switch: only a tiny number of driver and monitor combinations ever need it, turning it off routes all rendering through composition, and screen-capture programs may be affected. Values Pavise wrote are auto-restored after upgrade; your own edits are untouched." },
            }),
            new ReleaseNote("1.7.0.4", "2026-08-11", new[]
            {
                new[]{ "游戏核心范围可以选绑哪块了 双 CCD 机型两块任选 X3D 多出高频 CCD 给少数吃频率的游戏 切换即时生效 对局中会等对局结束", "Game core range can now choose which block to bind: either of the two CCDs on dual-CCD machines, and X3D additionally offers the high-frequency CCD for the few frequency-hungry games. Switching takes effect immediately; mid-match it waits for the match to end." },
                new[]{ "电源计划改成一个下拉选完 不切换 用 Pavise 托管方案 或者用你自己调好的计划 选了自己的以后 Pavise 只负责切过去 一个设置都不改 笔记本嫌托管方案太热的选平衡就行", "Power plan is now a single dropdown: don't switch, use the Pavise managed plan, or use a plan you've tuned yourself. Once you pick your own, Pavise only switches to it and changes not a single setting. Laptops that find the managed plan too hot can just pick Balanced." },
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
                new[]{ "修复对局中和挂机后按键鼠标可能变卡 键盘 鼠标 耳机 音频 输入法 手柄类程序 还遇到变卡的 把后台的按键宏类工具加白名单", "Fixed keyboard and mouse possibly turning sluggish mid-match and after idling — keyboard, mouse, headset, audio, IME, and controller programs. If you still hit sluggishness, add background key-macro tools to the whitelist." },
                new[]{ "游戏退出后如果有设置没还原成功 不再每 20 秒刷一条日志 改成写明卡在哪一项 静默重试", "If some settings fail to restore after a game exits, no more log line every 20 seconds; it now states exactly which item is stuck and retries silently." },
                new[]{ "新增极限模式 在竞技之上把 GPU 让位和后台策略一次拉到位 模式页里被强制的开关会显示为锁定 游戏核心范围和电源计划仍由你手动选择 键鼠耳机输入法照常不碰", "Added Extreme mode: on top of Competitive, it pushes GPU yielding and background policies all the way in one go. Switches it forces show as locked on the mode page. Game core range and power plan remain your manual choice; keyboard, mouse, headset, and IME stay untouched as always." },
                new[]{ "新增后台限速 被强力压制的后台整体限到 5% CPU 运行时间 台架实测 1% 最差帧更稳 吞吐也更高 极限模式强制开启 自定义档可选 Pavise 意外退出由看门狗自动解除", "Added background CPU throttling: heavily suppressed background processes are collectively limited to 5% CPU run time. Bench-verified steadier 1% lows and higher throughput. Forced on in Extreme mode, optional in the Custom tier; if Pavise exits unexpectedly, the watchdog lifts it automatically." },
                new[]{ "竞技电源计划按 CPU 类别分方案 Intel 大小核线程优先性能核 AMD 密集核铺开全部核心 X3D 不插手调度交给大缓存偏好 日志写明用的哪套", "Competitive power plan now varies by CPU class: Intel hybrid chips prefer P-cores for threads, AMD dense-core chips spread across all cores, and X3D stays out of scheduling, deferring to the large-cache preference. The log states which set is in use." },
                new[]{ "新增后台赶去集显 双显卡本把被压制后台里用显卡的程序改走集显 给独显腾显存和引擎时间 显卡页开启 默认关 对正在运行的程序下次启动生效", "Added pushing background apps to the iGPU: on dual-GPU laptops, GPU-using programs among the suppressed background are rerouted to the iGPU, freeing VRAM and engine time for the discrete GPU. Enable on the GPU page, off by default; for already-running programs it takes effect on their next launch." },
                new[]{ "核心分区改成独立手动选择 策略页可随时在全核和单 CCD 间切换 极限模式不再强制；7945HX 7950X 5900X 会选择完整处理器 Die，Zen2 不再把单个 CCX 误当 CCD，X3D 全核时继续把调度交给 AMD 驱动", "Core partitioning is now an independent manual choice; the strategy page can switch between all cores and single CCD at any time, and Extreme mode no longer forces it. 7945HX, 7950X, and 5900X now select the full processor die; Zen2 no longer mistakes a single CCX for a CCD; X3D on all cores still leaves scheduling to the AMD driver." },
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
                new[]{ "智能保帧上线 默认开启 单独抬高决定帧数的那个线程 游戏吃满 CPU 时 1% 最差帧改善 77% 到 96% 识别不出时提优自动降档 负载退去 1 秒内恢复", "Smart frame guard is live, on by default: it individually raises the one thread that decides your frame rate. When the game saturates the CPU, 1% lows improve 77% to 96%. When the thread can't be identified, boost steps down a tier automatically; recovery within 1 second once the load subsides." },
                new[]{ "后台 GPU 让位重新上线 后台吃满显卡时中位帧时间减半 尾部帧改善约四成", "Background GPU yielding is back online: when background saturates the GPU, median frame time halves and 1% lows improve about 40%." },
                new[]{ "移除 AMD 驱动调优 部分 A 卡有副作用 升级后自动还原相关驱动设置", "Removed AMD driver tuning: some AMD cards had side effects. Related driver settings are auto-restored after upgrade." },
                new[]{ "新增 QQ 交流二群 1101249532", "Added second QQ chat group: 1101249532" },
            }),
            new ReleaseNote("1.6.8.1", "2026-08-07", new[]
            {
                new[]{ "保帧线程重新上线 补充测试发现 6 核 12 线程这类机型提升明显 游戏吃满 CPU 时 1% 最差帧改善 77% 到 96% 开关在策略页 沿用你之前的设置", "Frame-guard thread is back online. Follow-up testing found clear gains on machines like 6-core 12-thread: when the game saturates the CPU, 1% lows improve 77% to 96%. The switch is on the strategy page and keeps your previous setting." },
                new[]{ "竞技模式禁用 CPU 空闲状态重新上线 新增平台识别 Intel K 系配 Z 板 AMD 桌面 Ryzen 配 B 板或 X 板可用 其余平台禁 C-State 会连带关掉睿频 开关自动置灰 之前升级把它重置成了关 要用的重新打开", "Competitive mode's disable-CPU-idle-states is back online, now with platform detection: available on Intel K-series with Z boards and AMD desktop Ryzen with B or X boards; on other platforms disabling C-States would also kill turbo, so the switch grays out automatically. A previous upgrade reset it to off — turn it back on if you use it." },
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
