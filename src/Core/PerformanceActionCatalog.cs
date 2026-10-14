using System;

namespace PaviseApp
{
    internal sealed class PerformanceAction
    {
        internal string Key, Zh, En, DetailZh, DetailEn;
        internal bool Environment;
        internal PerformanceAction(string key, string zh, string en, string dz, string de, bool environment)
        { Key = key; Zh = zh; En = en; DetailZh = dz; DetailEn = de; Environment = environment; }
        internal string Title { get { return AutomaticPolicy.Text(Zh, En); } }
        internal string Detail { get { return AutomaticPolicy.Text(DetailZh, DetailEn); } }
    }
    internal static class PerformanceActionCatalog
    {
        internal const string Mitigations = "AutoGameMitigationsV1", DefenderResources = "AutoDefenderResourcesV1",
            LegacyDefenderSession = "AutoDefenderSessionV1", ShortNames = "AutoNtfsShortNamesV1",
            BackgroundFps = "AutoBackgroundFpsV1", OpenGl = "AutoOpenGlThreadsV1", VisualEffects = "MatchVisualEffectsV1";
        internal const string CpuMitigations = "EnvCpuMitigationsV1", LastAccess = "EnvNtfsLastAccessV1",
            Quantum = "EnvQuantumProfileV1", StorageIdle = "EnvStorageIdleV1", StorageRuntime = "EnvStorageRuntimeV1",
            StorageCold = "EnvStorageD3ColdV1", AudioIdle = "EnvAudioIdleV1";
        internal static readonly PerformanceAction[] All = {
            new PerformanceAction(VisualEffects, "视觉效果精简", "Reduced visual effects",
                "对局中减少桌面动画与阴影，保留字体平滑。退局自动恢复原设置。", "Reduces desktop animations and shadows during matches, preserving font smoothing. Restores your original settings when the match ends.", false),
            new PerformanceAction(Mitigations, "游戏保护开销减负 · CFG", "Game mitigation overhead · CFG",
                "自动为游戏库中的程序配置 CFG、EAF/EAF+、IAF、CallerCheck 关闭；32 位程序另含 SimExec。下次启动游戏生效，无需重启电脑。", "Preconfigures CFG, EAF/EAF+, IAF and CallerCheck off for library games; includes SimExec for 32-bit games. Effective at the next game launch; no PC restart.", false),
            new PerformanceAction(DefenderResources, "游戏资源扫描排除", "Game resource scan exclusions",
                "按游戏进程限定资源目录排除；旧平台使用目录排除。保留原有排除项，清除配置时撤销本程序新增项。", "Excludes game resources in the game process context, with directory exclusions on older platforms. Preserves existing exclusions; reset removes only owned entries.", false),
            new PerformanceAction(ShortNames, "游戏盘短文件名减负", "Game-volume short-name reduction",
                "对支持逐卷设置的 NTFS 游戏盘停止创建 8.3 短文件名；不删除已有短名称，不更改全机策略。无需重启。", "Stops new 8.3 names on NTFS game volumes with per-volume policy. Keeps existing names and global policy. No restart.", false),
            new PerformanceAction(BackgroundFps, "后台 NVIDIA 渲染限帧", "NVIDIA background rendering cap",
                "对局期间限制已识别的后台 3D 程序，排除游戏、白名单和工作应用。只配置后台限帧，不设置游戏前台上限。", "Caps identified background 3D applications during matches; excludes games, whitelisted and work applications. Uses the background-only limit.", false),
            new PerformanceAction(OpenGl, "游戏 OpenGL 驱动多线程", "Game OpenGL driver threading",
                "为 NVIDIA 游戏配置 OpenGL 线程优化；下次启动程序生效，不改变纹理过滤、分辨率或特效。", "Preconfigures NVIDIA OpenGL threaded optimization for games. Effective on next launch; preserves texture filtering, resolution and effects.", false),
            new PerformanceAction(CpuMitigations, "关闭 Spectre V2 / Meltdown 软件缓解", "Disable Spectre V2 / Meltdown software mitigations",
                "关闭这两类可配置的软件缓解，保留其他缓解位。应用或恢复后需要重启电脑；不是关闭所有 CPU 漏洞缓解。", "Disables configurable software mitigations for these two classes while preserving other bits. Applying or restoring requires a PC restart; does not disable every CPU mitigation.", true),
            new PerformanceAction(LastAccess, "关闭 NTFS 最后访问时间更新", "Disable NTFS last-access updates",
                "减少文件访问时间元数据维护。应用或恢复后需要重启电脑；原本已关闭时无新增收益。", "Reduces last-access metadata maintenance. Applying or restoring requires a PC restart; no additional benefit if already disabled.", true),
            new PerformanceAction(Quantum, "前台时间片", "Foreground quantum",
                "正在玩游戏、浏览网页或日常操作：推荐「游戏推荐 · 短可变 3:1」，优先照顾当前使用的窗口。\n主要让电脑在后台做渲染、批量计算：推荐「后台计算推荐 · 长固定 1:1」，每轮运行更久，前后台每轮时间相同。其他档位用于对比调试。\n例如游戏在前台、浏览器在后台，3:1 表示游戏每轮运行时间为浏览器的 3 倍；切到浏览器后，它就成为前台。这不是帧数提升倍数，实际效果因游戏而异。应用或恢复后需重启电脑。", "Playing a game, browsing or using everyday apps: choose Gaming pick (short variable 3:1), which favors the window you are using.\nMainly running background rendering or batch calculations: choose Background pick (long fixed 1:1), with longer, equal turns for active and background apps. Other profiles are for comparison and tuning.\nIf the game is foreground and the browser is background, 3:1 means the game's turn is three times as long. Switching to the browser makes it foreground. This is not an FPS multiplier; results vary by game. Restart after applying or restoring.", true),
            new PerformanceAction(StorageIdle, "存储设备保持唤醒", "Keep storage devices awake",
                "仅配置已登记 StorPort 的磁盘设备，关闭空闲电源管理。驱动需重新读取，保守按重启电脑生效处理。", "Disables idle power management only for disks with registered StorPort configuration. Requires driver reload; conservatively treat as requiring PC restart.", true),
            new PerformanceAction(StorageRuntime, "关闭存储控制器运行时电源管理", "Disable storage controller runtime power management",
                "为已登记 StorPort 的存储控制器关闭运行时电源管理。驱动需重新读取，保守按重启电脑生效处理。", "Disables runtime power management for registered StorPort controllers. Requires driver reload; conservatively treat as requiring PC restart.", true),
            new PerformanceAction(StorageCold, "存储控制器禁用 D3Cold", "Disable storage-controller D3Cold",
                "为已登记 StorPort 的存储控制器禁止 D3Cold。取决于驱动与硬件，按重启电脑后生效处理。", "Disables D3Cold for registered StorPort controllers. Hardware and driver dependent; treat as effective after PC restart.", true),
            new PerformanceAction(AudioIdle, "交流供电下音频保持唤醒", "Keep AC-powered audio awake",
                "仅修改已有 PortCls 空闲配置的音频驱动。减少空闲后第一次出声的唤醒等待；默认已禁用时无新增收益。驱动需重载，按重启处理。", "Changes only existing PortCls audio idle settings. Reduces first-sound wake delay; no additional benefit if already disabled. Requires driver reload; treat as requiring restart.", true)
        };
        // 已下架的动作只用于识别并恢复旧版本留下的记录，不再执行。
        internal static readonly PerformanceAction[] Retired = {
            new PerformanceAction(LegacyDefenderSession, "旧版对局 Defender 调整", "Legacy match Defender change",
                "3.1.0.0 至 3.1.0.4 曾在对局中尝试关闭 Defender 实时扫描与行为监控，现已移除；只恢复旧版留下的记录。", "3.1.0.0 to 3.1.0.4 attempted to pause Defender real-time scanning and behavior monitoring during matches. Removed; only records left by those versions are restored.", false)
        };
        internal static PerformanceAction Find(string key)
        {
            foreach (PerformanceAction a in All) if (a.Key == key) return a;
            foreach (PerformanceAction a in Retired) if (a.Key == key) return a;
            return null;
        }
    }
}
