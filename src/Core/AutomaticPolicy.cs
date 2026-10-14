using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static class AutomaticPolicy
    {
        internal const string VersionKey = "AutomaticPolicyV1";
        internal const string GameLowLatencyKey = "GameLowLatencyOn";
        internal static bool UsesFrameCoordination(PerformancePreset mode)
        {
            return mode == PerformancePreset.Standard || mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Extreme;
        }
        internal static bool GameLowLatency { get { return Settings.LoadCached(GameLowLatencyKey, false); } }
        internal static bool IsLowLatency(string key)
        {
            return key == PolicyCatalog.KeyNvLowLat;
        }
        internal static bool IsRetired(string key)
        {
            return key == PolicyCatalog.KeyAmdAntiLag || key == PolicyCatalog.KeyGpuPowerMax;
        }
        internal static bool IsRetiredInAuto(string key)
        {
            // CPU 占用率不能证明线程与帧关键路径有关。
            return IsRetired(key) || key == PolicyCatalog.KeyRenderLane;
        }
        internal static bool Enabled { get { return Settings.LoadCached(VersionKey, false); } }
        internal static bool IsEnvironment(string key)
        {
            return key == PolicyCatalog.KeyTimerTick || key == PolicyCatalog.KeyGlobalTimer
                || key == PolicyCatalog.KeyCacheWarm || key == PolicyCatalog.KeyStandbyCleaner;
        }
        internal static bool EnvironmentPreference(string key)
        {
            bool cached;
            if (Settings.TryLoadCached(key, out cached)) return cached;
            bool fallback = key == PolicyCatalog.KeyTimerTick ? TimerTickTweak.OwnsState
                : key == PolicyCatalog.KeyGlobalTimer ? GlobalTimerResTweak.OwnsState : false;
            return Settings.LoadCached(key, fallback);
        }
        internal static bool IsIntent(string key)
        {
            return key == PolicyCatalog.KeyPreset || key == PolicyCatalog.KeyAutoCores
                || key == PolicyCatalog.KeySuppressFamily || key == PolicyCatalog.KeyGameRebar
                || key == PolicyCatalog.KeyGameFullscreenOptimizations;
        }
        internal static string Resolve(string key, Func<string, string> raw)
        {
            if (IsRetiredInAuto(key)) return "0";
            if (IsEnvironment(key)) return EnvironmentPreference(key) ? "1" : "0";
            if (IsIntent(key)) return raw(key);
            if (IsLowLatency(key)) return GameLowLatency ? "on" : "off";
            int parsed;
            PerformancePreset mode = int.TryParse(raw(PolicyCatalog.KeyPreset), out parsed)
                ? PresetValue.From(parsed) : PerformancePreset.Standard;
            bool handheld = mode == PerformancePreset.Handheld;
            bool competitive = PresetValue.IsCompetitive(mode);
            bool extreme = mode == PerformancePreset.Extreme;
            switch (key)
            {
                case FrameCoordination.ActionKey: return UsesFrameCoordination(mode) ? "1" : "0";
                case PolicyCatalog.KeyDwmBoost:
                case PolicyCatalog.KeyIntelEndurance: return extreme ? "1" : "0";
                case PolicyCatalog.KeyIdlePolicy: return extreme && PowerPlan.AutomaticSelection ? "1" : "0";
                case PolicyCatalog.KeyWsTrim: return handheld ? "0" : "1";
                case PolicyCatalog.KeySuppress:
                case PolicyCatalog.KeyBoost:
                case PolicyCatalog.KeyPauseMaintenance:
                case PolicyCatalog.KeyPauseUpdate:
                case PolicyCatalog.KeyAwake: return "1";
                case PolicyCatalog.KeyAggressive: return competitive ? "1" : "0";
                case PolicyCatalog.KeyGpuDemote: return "1";
                case PolicyCatalog.KeyAdaptiveEscalate: return mode == PerformancePreset.Standard ? "1" : "0";
                case PolicyCatalog.KeyPowerPlan: return !PowerPlan.NoSwitchSelected
                    && (!handheld || !PowerPlan.AutomaticSelection) ? "1" : "0";
                case PolicyCatalog.KeyPauseDl: return competitive || handheld ? "1" : "0";
                case PolicyCatalog.KeyPauseServices: return competitive ? "1" : "0";
                case PolicyCatalog.KeyNvMaxPerf: return handheld ? "0" : "1";
                case PolicyCatalog.KeyNvShaderCache: return "1";
                case PolicyCatalog.KeyNvDlss: return "off";
                default: return "0";
            }
        }
        internal static string Reason(string key)
        {
            switch (key)
            {
                case FrameCoordination.ActionKey:
                    return Text("智能、竞技与极限自动参与；内存受限时保护低优先级关键线程页面，退局恢复；掌机保留系统调度", "Automatic in Smart, Esports and Extreme; protects low-priority critical-thread pages under memory constraints and restores on exit; Handheld retains system scheduling");
                case PolicyCatalog.KeyRenderLane:
                    return Text("CPU 耗时候选提优已停用，避免误提优阻塞游戏", "CPU-time candidate boost disabled to avoid starving game work");
                case PolicyCatalog.KeyWsTrim:
                    return Text("掌机模式不启用此项", "Not enabled in Handheld mode");
                case PolicyCatalog.KeyAggressive: return Text("竞技与极限模式启用", "Enabled in Esports and Extreme modes");
                case PolicyCatalog.KeyAdaptiveEscalate: return Text("仅智能模式需要动态升级", "Dynamic escalation is used in Smart mode only");
                case PolicyCatalog.KeyPauseDl: return Text("竞技、极限与掌机模式启用", "Enabled in Esports, Extreme and Handheld modes");
                case PolicyCatalog.KeyPauseServices: return Text("竞技与极限模式暂停可选服务", "Optional services are paused in Esports and Extreme modes");
                case PolicyCatalog.KeyPauseUpdate: return Text("所有模式对局期间启用", "Enabled during matches in all modes");
                case PolicyCatalog.KeyNvLowLat:
                    return Text("由环境中的游戏低延迟开关控制", "Controlled by Game low latency in Environment");
                case PolicyCatalog.KeyGpuPowerMax: return Text("GPU 功耗上限调优已移除", "GPU power-limit tuning removed");
                case PolicyCatalog.KeyAmdAntiLag: return Text("AMD Anti-Lag 已移除", "AMD Anti-Lag removed");
                case PolicyCatalog.KeyDwmBoost:
                case PolicyCatalog.KeyIntelEndurance: return Text("仅极限模式启用", "Enabled in Extreme mode only");
                case PolicyCatalog.KeyIdlePolicy: return Text("极限模式使用 Pavise 托管电源方案时启用", "Requires Extreme mode and the Pavise managed power plan");
                case PolicyCatalog.KeyNvMaxPerf: return Text("掌机模式保留显卡节能调度", "Handheld retains GPU power saving");
                case PolicyCatalog.KeyPowerPlan: return PowerPlan.NoSwitchSelected
                    ? Text("已选择不切换，保留系统电源设置", "Don't switch is selected; system power settings are retained")
                    : Text("掌机模式由系统管理电源；指定计划时仍会切换", "Handheld uses system power management unless a plan is selected");
                default: return Text("当前模式不需要此动作", "Not needed by the current mode");
            }
        }
        internal static string Text(string zh, string en) { return Lang.Cur == 0 ? zh : en; }
    }
}
