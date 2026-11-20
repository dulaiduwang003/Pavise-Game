namespace PaviseApp
{
    internal static class PerformanceActionCatalog
    {
        internal const string DefenderResources = "AutoDefenderResourcesV1";

        internal const string LegacyDefenderSession = "AutoDefenderSessionV1";

        internal const string ShortNames = "AutoNtfsShortNamesV1";

        internal const string BackgroundFps = "AutoBackgroundFpsV1";

        internal const string VisualEffects = "MatchVisualEffectsV1";

        internal const string CpuMitigations = "EnvCpuMitigationsV1";

        internal const string LastAccess = "EnvNtfsLastAccessV1";

        internal const string Quantum = "EnvQuantumProfileV1";

        internal const string AudioIdle = "EnvAudioIdleV1";

        internal static readonly PerformanceAction[] All = new PerformanceAction[]
        {
            new PerformanceAction("EnvCpuMitigationsV1", "关闭可配置 CPU 软件缓解", "Disable configurable CPU software mitigations", "降低 Intel/AMD 可配置的 CPU 漏洞防护，需 Win10 2004+，范围由处理器与系统决定。应用或恢复后重启电脑；关闭恢复原值。旧版已开启时，先关再开以使用扩展策略。", "Reduces configurable CPU vulnerability protections on Intel/AMD with Windows 10 2004+. Scope depends on the CPU and OS. Restart after applying or restoring; Off restores originals. If enabled in an older version, switch off and on for the expanded policy.", true),
            new PerformanceAction("EnvAudioIdleV1", "交流供电下音频保持唤醒", "Keep AC-powered audio awake", "仅修改已有 PortCls 空闲配置的音频驱动。减少空闲后第一次出声的唤醒等待；默认已禁用时无新增收益。驱动需重载，按重启处理。", "Changes only existing PortCls audio idle settings. Reduces first-sound wake delay; no additional benefit if already disabled. Requires driver reload; treat as requiring restart.", true)
        };

        // 下线的功能留在这里只为识别旧的恢复记录 不能再开启 也不在调优页显示
        internal static readonly PerformanceAction[] Retired = new PerformanceAction[]
        {
            new PerformanceAction("AutoNtfsShortNamesV1", "旧版游戏盘短文件名减负", "Legacy game-volume short-name reduction", "实测对游戏没有可测收益，已下线；只恢复以前在游戏盘上关闭的 8.3 短文件名设置。", "Removed because it had no measurable gaming benefit; only restores 8.3 short-name settings changed on game volumes earlier.", false),
            new PerformanceAction("EnvNtfsLastAccessV1", "旧版关闭 NTFS 最后访问时间更新", "Legacy NTFS last-access change", "新系统默认已关闭，实测没有可测收益，已下线；只恢复以前留下的设置，重启电脑后生效。", "Removed because current Windows already disables it and it had no measurable benefit; only restores the earlier setting, effective after a PC restart.", true),
            new PerformanceAction("EnvQuantumProfileV1", "旧版前台时间片", "Legacy foreground quantum", "推荐档位就是 Windows 默认值，实测没有可测收益，已下线；只恢复以前留下的时间片设置。", "Removed because the recommended profile equals the Windows default and showed no measurable benefit; only restores the quantum setting left earlier.", true),
            new PerformanceAction("MatchVisualEffectsV1", "旧版视觉效果精简", "Legacy reduced visual effects", "此功能已移除；仅恢复旧版留下的桌面视觉设置记录。", "Removed; only restores desktop visual-setting records left by older versions.", false),
            new PerformanceAction("AutoBackgroundFpsV1", "旧版后台 NVIDIA 渲染限帧", "Legacy NVIDIA background rendering cap", "后台限帧已移除，后台压制已覆盖它的作用；只恢复旧版本写入的后台程序限帧记录。", "The background rendering cap has been removed; background suppression covers it. Only records written by older versions are restored.", false),
            new PerformanceAction("AutoDefenderResourcesV1", "旧版游戏资源扫描排除", "Legacy game resource scan exclusions", "自动添加游戏资源扫描排除已移除；只撤销旧版明确新增的排除，保留用户原有项与未完成的恢复记录。", "Automatic game resource scan exclusions have been removed. Only exclusions explicitly added by older versions are removed; pre-existing exclusions and unresolved recovery records are preserved.", false),
            new PerformanceAction("AutoDefenderSessionV1", "旧版对局 Defender 调整", "Legacy match Defender change", "3.1.0.0 至 3.1.0.4 曾在对局中尝试关闭 Defender 实时扫描与行为监控，现已移除；只恢复旧版留下的记录。", "3.1.0.0 to 3.1.0.4 attempted to pause Defender real-time scanning and behavior monitoring during matches. Removed; only records left by those versions are restored.", false)
        };

        internal static bool IsRetired(string key)
        {
            foreach (PerformanceAction action in Retired)
            {
                if (action.Key == key) return true;
            }
            return false;
        }

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
