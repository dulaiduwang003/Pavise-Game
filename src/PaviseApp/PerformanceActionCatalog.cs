namespace PaviseApp
{
	internal static class PerformanceActionCatalog
	{
		internal const string Mitigations = "AutoGameMitigationsV1";

		internal const string DefenderResources = "AutoDefenderResourcesV1";

		internal const string LegacyDefenderSession = "AutoDefenderSessionV1";

		internal const string ShortNames = "AutoNtfsShortNamesV1";

		internal const string BackgroundFps = "AutoBackgroundFpsV1";

		internal const string OpenGl = "AutoOpenGlThreadsV1";

		internal const string VisualEffects = "MatchVisualEffectsV1";

		internal const string CpuMitigations = "EnvCpuMitigationsV1";

		internal const string LastAccess = "EnvNtfsLastAccessV1";

		internal const string Quantum = "EnvQuantumProfileV1";

		internal const string StorageIdle = "EnvStorageIdleV1";

		internal const string StorageRuntime = "EnvStorageRuntimeV1";

		internal const string StorageCold = "EnvStorageD3ColdV1";

		internal const string AudioIdle = "EnvAudioIdleV1";

        internal static readonly PerformanceAction[] All = new PerformanceAction[10]
		{
			new PerformanceAction("AutoGameMitigationsV1", "游戏保护开销减负 · CFG", "Game mitigation overhead · CFG", "自动为游戏库中的程序配置 CFG、EAF/EAF+、IAF、CallerCheck 关闭；32 位程序另含 SimExec。下次启动游戏生效，无需重启电脑。", "Preconfigures CFG, EAF/EAF+, IAF and CallerCheck off for library games; includes SimExec for 32-bit games. Effective at the next game launch; no PC restart.", false),
			new PerformanceAction("AutoNtfsShortNamesV1", "游戏盘短文件名减负", "Game-volume short-name reduction", "对支持逐卷设置的 NTFS 游戏盘停止创建 8.3 短文件名；不删除已有短名称，不更改全机策略。无需重启。", "Stops new 8.3 names on NTFS game volumes with per-volume policy. Keeps existing names and global policy. No restart.", false),
			new PerformanceAction("AutoOpenGlThreadsV1", "游戏 OpenGL 驱动多线程", "Game OpenGL driver threading", "为 NVIDIA 游戏配置 OpenGL 线程优化；下次启动程序生效，不改变纹理过滤、分辨率或特效。", "Preconfigures NVIDIA OpenGL threaded optimization for games. Effective on next launch; preserves texture filtering, resolution and effects.", false),
			new PerformanceAction("EnvCpuMitigationsV1", "关闭 Spectre V2 / Meltdown 软件缓解", "Disable Spectre V2 / Meltdown software mitigations", "关闭这两类可配置的软件缓解，保留其他缓解位。应用或恢复后需要重启电脑；不是关闭所有 CPU 漏洞缓解。", "Disables configurable software mitigations for these two classes while preserving other bits. Applying or restoring requires a PC restart; does not disable every CPU mitigation.", true),
			new PerformanceAction("EnvNtfsLastAccessV1", "关闭 NTFS 最后访问时间更新", "Disable NTFS last-access updates", "减少文件访问时间元数据维护。应用或恢复后需要重启电脑；原本已关闭时无新增收益。", "Reduces last-access metadata maintenance. Applying or restoring requires a PC restart; no additional benefit if already disabled.", true),
			new PerformanceAction("EnvQuantumProfileV1", "前台时间片", "Foreground quantum", "正在玩游戏、浏览网页或日常操作：推荐「游戏推荐 · 短可变 3:1」，优先照顾当前使用的窗口。\n主要让电脑在后台做渲染、批量计算：推荐「后台计算推荐 · 长固定 1:1」，每轮运行更久，前后台每轮时间相同。其他档位用于对比调试。\n例如游戏在前台、浏览器在后台，3:1 表示游戏每轮运行时间为浏览器的 3 倍；切到浏览器后，它就成为前台。这不是帧数提升倍数，实际效果因游戏而异。应用或恢复后需重启电脑。", "Playing a game, browsing or using everyday apps: choose Gaming pick (short variable 3:1), which favors the window you are using.\nMainly running background rendering or batch calculations: choose Background pick (long fixed 1:1), with longer, equal turns for active and background apps. Other profiles are for comparison and tuning.\nIf the game is foreground and the browser is background, 3:1 means the game's turn is three times as long. Switching to the browser makes it foreground. This is not an FPS multiplier; results vary by game. Restart after applying or restoring.", true),
			new PerformanceAction("EnvStorageIdleV1", "存储设备保持唤醒", "Keep storage devices awake", "仅配置已登记 StorPort 的磁盘设备，关闭空闲电源管理。驱动需重新读取，保守按重启电脑生效处理。", "Disables idle power management only for disks with registered StorPort configuration. Requires driver reload; conservatively treat as requiring PC restart.", true),
			new PerformanceAction("EnvStorageRuntimeV1", "关闭存储控制器运行时电源管理", "Disable storage controller runtime power management", "为已登记 StorPort 的存储控制器关闭运行时电源管理。驱动需重新读取，保守按重启电脑生效处理。", "Disables runtime power management for registered StorPort controllers. Requires driver reload; conservatively treat as requiring PC restart.", true),
			new PerformanceAction("EnvStorageD3ColdV1", "存储控制器禁用 D3Cold", "Disable storage-controller D3Cold", "为已登记 StorPort 的存储控制器禁止 D3Cold。取决于驱动与硬件，按重启电脑后生效处理。", "Disables D3Cold for registered StorPort controllers. Hardware and driver dependent; treat as effective after PC restart.", true),
			new PerformanceAction("EnvAudioIdleV1", "交流供电下音频保持唤醒", "Keep AC-powered audio awake", "仅修改已有 PortCls 空闲配置的音频驱动。减少空闲后第一次出声的唤醒等待；默认已禁用时无新增收益。驱动需重载，按重启处理。", "Changes only existing PortCls audio idle settings. Reduces first-sound wake delay; no additional benefit if already disabled. Requires driver reload; treat as requiring restart.", true)
		};

        internal static readonly PerformanceAction[] Retired = new PerformanceAction[4]
		{
            new PerformanceAction("MatchVisualEffectsV1", "旧版视觉效果精简", "Legacy reduced visual effects", "此功能已移除；仅恢复旧版留下的桌面视觉设置记录。", "Removed; only restores desktop visual-setting records left by older versions.", false),
			new PerformanceAction("AutoBackgroundFpsV1", "旧版后台 NVIDIA 渲染限帧", "Legacy NVIDIA background rendering cap", "后台限帧已移除，后台压制已覆盖它的作用；只恢复旧版本写入的后台程序限帧记录。", "The background rendering cap has been removed; background suppression covers it. Only records written by older versions are restored.", false),
			new PerformanceAction("AutoDefenderResourcesV1", "旧版游戏资源扫描排除", "Legacy game resource scan exclusions", "自动添加游戏资源扫描排除已移除；只撤销旧版明确新增的排除，保留用户原有项与未完成的恢复记录。", "Automatic game resource scan exclusions have been removed. Only exclusions explicitly added by older versions are removed; pre-existing exclusions and unresolved recovery records are preserved.", false),
			new PerformanceAction("AutoDefenderSessionV1", "旧版对局 Defender 调整", "Legacy match Defender change", "3.1.0.0 至 3.1.0.4 曾在对局中尝试关闭 Defender 实时扫描与行为监控，现已移除；只恢复旧版留下的记录。", "3.1.0.0 to 3.1.0.4 attempted to pause Defender real-time scanning and behavior monitoring during matches. Removed; only records left by those versions are restored.", false)
		};

		internal static PerformanceAction Find(string key)
		{
			PerformanceAction[] all = All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (performanceAction.Key == key)
				{
					return performanceAction;
				}
			}
			PerformanceAction[] retired = Retired;
			foreach (PerformanceAction performanceAction2 in retired)
			{
				if (performanceAction2.Key == key)
				{
					return performanceAction2;
				}
			}
			return null;
		}
	}
}
