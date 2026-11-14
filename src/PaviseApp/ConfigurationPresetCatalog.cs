using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 功能组合与智能、竞技两种运行策略分别设置。
    internal enum ConfigurationPresetKind { Default, Extreme }

    internal sealed class ConfigurationPresetEntry
    {
        internal readonly string Key, Zh, En, DefaultValue, ExtremeValue;
        internal readonly bool RestartRequired;
        internal ConfigurationPresetEntry(string key, string zh, string en, string normal, string extreme, bool restart)
        { Key = key; Zh = zh; En = en; DefaultValue = normal; ExtremeValue = extreme; RestartRequired = restart; }
        internal string Title { get { return PerformancePolicy.Text(Zh, En); } }
        internal string Value(ConfigurationPresetKind preset) { return preset == ConfigurationPresetKind.Extreme ? ExtremeValue : DefaultValue; }
        // 预设值为空时保留当前选择。
        internal static bool IsEnabled(string value) { return value != null && value != "0" && value != "off" && value != "default"; }
    }

    internal static class ConfigurationPresetCatalog
    {
        private static ConfigurationPresetEntry E(string key, string zh, string en, string normal, string extreme, bool restart = false)
        { return new ConfigurationPresetEntry(key, zh, en, normal, extreme, restart); }
        // 游戏卡片的调度、选核及资源优先级独立保存，不纳入性能预设。
        internal static readonly ConfigurationPresetEntry[] Entries = {
            E("AutoNtfsShortNamesV1", "游戏盘短文件名减负", "Game-volume short names", "0", "1"),
            E("GmCacheWarmV2", "缓存预热", "Cache warm-up", "0", "0"),
            E("AutoGameMitigationsV1", "游戏保护开销减负 · CFG", "Game mitigation overhead · CFG", "0", "1", true),
            E("GmMmcss", "多媒体调度", "Multimedia scheduling", "1", "1", true),
            E("GmSuppress", "后台治理", "Background governance", "1", "1"),
            E("BackgroundGovernanceMode", "后台治理强度", "Background governance strength", "0", "1"),
            E("GmPauseUpdate", "对局期间暂停 Windows 更新", "Pause Windows Update during matches", "1", "1"),
            E("GmPauseMaintenanceV1", "对局期间限制自动维护触发", "Limit maintenance triggers", "0", "1"),
            E("EnvPrivacyBackgroundGlobalV1", "限制 Windows 应用后台任务", "Restrict Windows app background tasks", "0", "1", true),
            E("GmDwmBoost", "桌面合成调度", "Desktop compositor scheduling", "0", "1"),
            E("GmIntelEnduranceV1", "对局关闭 Endurance Gaming", "Disable Endurance Gaming during matches", "0", "0"),
            E("GmEnglishInputV1", "游戏窗口切换英文输入", "English input for game windows", "0", "0"),
            E("GameDvrOff", "关闭 Game DVR 与 Xbox 后台录制", "Disable Game DVR and Xbox capture", "0", "0"),
            E("NvMaxPerf", "NVIDIA 电源模式最高性能优先", "NVIDIA prefer maximum performance", "0", "1", true),
            E("NvShaderCache", "NVIDIA 着色器缓存无上限", "NVIDIA unlimited shader cache", "0", "0", true),
            E("AutoOpenGlThreadsV1", "游戏 OpenGL 驱动多线程", "Game OpenGL driver threading", "0", "0", true),
            E("NvLowLat", "游戏低延迟", "Game low latency", "off", "on", true),
            E("EnvNtfsLastAccessV1", "关闭 NTFS 最后访问时间更新", "Disable NTFS last-access updates", "0", "1", true),
            E("EnvAudioIdleV1", "交流供电下音频保持唤醒", "Keep AC-powered audio awake", "1", "1", true),
            E("EnvVbsDisabledV1", "关闭 VBS", "Disable VBS", "1", "1", true),
            E("EnvCpuMitigationsV1", "关闭可配置 CPU 软件缓解", "Disable configurable CPU software mitigations", "legacy", "1", true),
            E("EnvQuantumProfileV1", "前台时间片", "Foreground quantum", "0", "0", true),
            E("EnvStoragePowerGroupV1", "存储设备电源管理 · 高级", "Advanced storage power management", "0", "1", true),
            E("AutoTimerTick", "计时器恒定节拍", "Fixed timer tick", "1", "1", true),
            E("AutoGlobalTimer", "全局计时器分辨率", "Global timer resolution", "1", "1", true),
            E("EnvPrivacyNtfsMemoryV1", "增加 NTFS 缓存池", "Increase NTFS cache pools", "0", "1", true),
            E("LabEppYieldV1", "EPP 功耗让路", "EPP power yielding", "0", "0"),
            E("LabGpuClockMaxV1", "GPU 频率锁最大值", "Maximum GPU clock lock", "0", "1"),
            E("LabGpuPowerMaxV1", "GPU 功耗墙拉满", "Maximum GPU power limit", "0", "1"),
            E("LabAudioLowBufferV1", "音频低缓冲流", "Low-buffer audio stream", "0", "0"),
            E("LabCpuIdleStrategyV1", "CPU 空闲策略", "CPU idle policy", "default", "default")
        };
        internal static bool IsGameTuningKey(string key)
        {
            switch (key)
            {
            case "GmBoost":
            case "GmBoostCpuV1":
            case "GmBoostIoV1":
            case "GmBoostGpuV1":
            case "GmBoostMemoryV1":
            case "GmThreadBoostStrategyV1":
            case "GmRenderLane":
            case "FrameCoordination":
            case "GmGameSchedulingV1":
            case "GameCriticalThreadHardRuleV1":
            case "GmAutoCoresV2":
            case "GmManualCorePlanV2":
            case "GameFamilyBackground":
            case "GameNvRebarV1":
            case "GameDisableFullscreenOptimizationsV1":
                return true;
            default:
                return false;
            }
        }
        internal static string Name(ConfigurationPresetKind preset)
        { return PerformancePolicy.Text(preset == ConfigurationPresetKind.Extreme ? "极限配置" : "作者同款", preset == ConfigurationPresetKind.Extreme ? "Extreme setup" : "Author’s setup"); }
    }
}
