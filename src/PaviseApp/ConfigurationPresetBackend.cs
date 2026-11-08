using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class ConfigurationPresetBackend : IConfigurationPresetBackend, IConfigurationPresetSnapshot
    {
        private readonly GameMode gameMode;
        private static readonly string[] bundle = { "EnvCpuMitigationsV1", "EnvNtfsLastAccessV1", "EnvAudioIdleV1", "EnvVbsDisabledV1" };
        internal ConfigurationPresetBackend(GameMode gameMode) { this.gameMode = gameMode; }
        public bool SaveIdentity(string value)
        { return Settings.SaveStrDurable(ConfigurationPresetRuntime.IdentityKey, value) && Settings.LoadStr(ConfigurationPresetRuntime.IdentityKey, "") == value; }
        public string LoadIdentity() { return Settings.LoadStr(ConfigurationPresetRuntime.IdentityKey, ""); }
        private static string Boolean(string key, bool fallback)
        {
            bool stored; if (Settings.TryLoadStoredBool(key, out stored)) return stored ? "1" : "0";
            string raw; if (!Settings.TryLoadStr(key, out raw)) return null;
            if (string.IsNullOrEmpty(raw)) return fallback ? "1" : "0";
            if (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return "1";
            if (raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return "0";
            return null;
        }
        private static string GroupValue(IEnumerable<string> members)
        {
            bool any = false;
            foreach (string member in members) { string value = Boolean(member, false); if (value == null) return null; any |= value == "1"; }
            return any ? "1" : "0";
        }
        public string Read(ConfigurationPresetEntry entry)
        { return ReadSavedValue(entry); }
        private static string ReadSavedValue(ConfigurationPresetEntry entry)
        {
            string key = entry.Key;
            if (key == BackgroundGovernancePolicy.SettingKey)
            {
                string selected;
                return Settings.TryLoadStr(key, out selected)
                    ? BackgroundGovernancePolicy.Parse(selected).ToString(CultureInfo.InvariantCulture) : null;
            }
            if (key == PerformanceActionCatalog.CpuMitigations)
            {
                string enabled = Boolean(key, false), expanded = Boolean(CpuMitigationPolicy.ExpandedPreferenceKey, false);
                return enabled == null || expanded == null ? null : enabled == "0" ? "0" : expanded == "1" ? "1" : "legacy";
            }
            if (key == "EnvQuantumProfileV1")
            {
                string enabled = Boolean(key, false), raw;
                if (enabled == null || !Settings.TryLoadStr("EnvQuantumSelectedProfileV1", out raw)) return null;
                if (enabled == "0") return "0";
                int parsed; return string.IsNullOrEmpty(raw) ? "38" : int.TryParse(raw, out parsed) && Array.IndexOf(QuantumProfiles.Values, parsed) > 0 ? raw : null;
            }
            if (key == PerformanceTuning.EnvironmentBundleKey) return GroupValue(bundle);
            if (key == PerformanceTuning.StorageGroupKey) return GroupValue(PerformanceTuning.StorageMembers());
            if (PrivacyControl.IsAction(key)) return Boolean(PrivacyControl.PreferencePrefix + key, false);
            if (key == ThreadBoostSelection.Key) { string selected; return ThreadBoostSelection.TryReadGlobal(out selected) ? selected : null; }
            if (key == LaboratoryPower.IdleKey) { string selected; return !Settings.TryLoadStr(key, out selected) ? null : string.IsNullOrEmpty(selected) ? "default" : selected; }
            PolicyItem policy = PolicyCatalog.ItemOf(key);
            if (policy != null && policy.Kind == PolicyValueKind.Choice)
            {
                string selected; if (!Settings.TryLoadStr(key, out selected)) return null;
                if (string.IsNullOrEmpty(selected)) return policy.Fallback;
                foreach (string choice in policy.Choices) if (choice == selected) return selected;
                return null;
            }
            if (key == "AutoTimerTick" || key == "AutoGlobalTimer")
                return Boolean(key, key == "AutoTimerTick" ? TimerTickTweak.OwnsState : GlobalTimerResTweak.OwnsState);
            return Boolean(key, policy != null ? policy.Fallback == "1" : key == "GmMmcss" || key == "GpuPrefStageOn" || key == "GameDvrOff");
        }
        public string ExtraSnapshot()
        { return ReadSavedExtra(); }
        private static string ReadSavedExtra()
        {
            StringBuilder text = new StringBuilder();
            foreach (string key in bundle) { string value = Boolean(key, false); if (value == null) return null; text.Append(key).Append('=').Append(value).Append('\n'); }
            foreach (string key in PerformanceTuning.StorageMembers()) { string value = Boolean(key, false); if (value == null) return null; text.Append(key).Append('=').Append(value).Append('\n'); }
            return text.ToString();
        }
        // Read durable preferences only; exclude capability probes, receipts and last-applied metadata.
        internal static string SavedIdentityPhase()
        {
            StringBuilder text = new StringBuilder();
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
            {
                string value = ReadSavedValue(entry);
                int quantum;
                if (entry.Key == "EnvQuantumProfileV1" && int.TryParse(value, out quantum))
                    value = quantum.ToString(CultureInfo.InvariantCulture);
                text.Append(entry.Key).Append('=').Append(value ?? "?").Append('\n');
            }
            return text.Append(ReadSavedExtra() ?? "?").ToString();
        }
        private static ConfigurationPresetAvailability Available(bool yes, string zh, string en)
        { return yes ? ConfigurationPresetAvailability.Yes() : ConfigurationPresetAvailability.No(PerformancePolicy.Text(zh, en)); }
        private static bool ServiceExists(string name)
        { using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + name)) return key != null; }
        private static bool NtfsAvailable()
        { foreach (DriveInfo drive in DriveInfo.GetDrives()) try { if (drive.IsReady && drive.DriveType == DriveType.Fixed && drive.DriveFormat == "NTFS") return true; } catch { } return false; }
        private static bool RegistryAvailable(string path)
        { using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path)) return key != null; }
        public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry)
        {
            try
            {
                if (!Native.IsElevated()) return ConfigurationPresetAvailability.No(PerformancePolicy.Text("需要管理员权限，未开启", "Administrator permission required; not enabled"));
                int build = Native.OsBuild();
                if (build <= 0) return ConfigurationPresetAvailability.No(PerformancePolicy.Text("系统版本无法核验", "OS version could not be verified"));
                switch (entry.Key)
                {
                    case "NvMaxPerf": return Available(NvApi.Available && NvApi.SupportsDwordSetting(NvDrsTweaks.SettingIdOf("pstate")), "NVIDIA 电源策略接口不支持或无法读取", "NVIDIA power-policy setting unsupported or unreadable");
                    case "NvShaderCache": return Available(NvApi.Available && NvApi.SupportsDwordSetting(NvDrsTweaks.SettingIdOf("shadercache")), "NVIDIA 缓存接口不支持或无法读取", "NVIDIA shader-cache setting unsupported or unreadable");
                    case "NvLowLat": return Available(NvApi.Available && NvApi.SupportsDwordSetting(NvDrsTweaks.SettingIdOf("prerender")), "NVIDIA 低延迟接口不支持或无法读取", "NVIDIA low-latency setting unsupported or unreadable");
                    case "GpuPrefStageOn": return Available(GpuPrefStage.Supported, "系统不支持游戏显卡偏好登记", "Game GPU preferences are unsupported");
                    case "AutoGlobalTimer": return Available(build < 19041 || build >= 20348, "系统不支持全局计时器配置", "Global timer configuration is unsupported on this OS");
                    case "AutoTimerTick": return Available(!LaboratoryClockRepair.HasReceipt && TimerTickTweak.CanReadForPreset(), "计时配置不可读取或有待恢复的修复记录", "Timer settings unreadable or timer repair recovery pending");
                    case "GmMmcss": return Available(ServiceExists("MMCSS") && RegistryAvailable("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games"), "游戏多媒体调度配置不可用", "Game multimedia scheduling configuration unavailable");
                    case "GmPauseUpdate": return Available(ServiceExists("wuauserv"), "未找到 Windows 更新服务", "Windows Update service unavailable");
                    case "GmPauseDl": return Available(ServiceExists("DoSvc"), "未找到传递优化服务", "Delivery Optimization service unavailable");
                    case "GmPauseMaintenanceV1": return Available(ServiceExists("Schedule"), "计划任务服务不可用", "Task Scheduler service unavailable");
                    case "AutoNtfsShortNamesV1": return Available(NtfsAvailable(), "未找到可核验的 NTFS 卷", "No verified NTFS volume available");
                    case "AutoGameMitigationsV1": return Available(build >= 16299, "系统不支持逐程序保护配置", "Per-image mitigation settings unsupported");
                    case PerformanceActionCatalog.CpuMitigations:
                        return Available(PerformanceTuning.CpuMitigationsExpansionSupported() && PerformanceActionBackend.EnvironmentTargets(entry.Key).Length > 0,
                            "本机不支持 CPU 软件缓解配置", "CPU software-mitigation configuration unsupported");
                    case "EnvNtfsLastAccessV1": case "EnvAudioIdleV1":
                        return Available(PerformanceActionBackend.EnvironmentTargets(entry.Key).Length > 0,
                            "本机未找到支持的配置目标", "No supported configuration targets found");
                    case "EnvVbsDisabledV1":
                        string vbsReason;
                        bool vbsAvailable = VbsTweak.CanConfigureForPreset(out vbsReason);
                        return vbsAvailable ? ConfigurationPresetAvailability.Yes() : ConfigurationPresetAvailability.No(vbsReason);
                    case "LabGpuClockMaxV1": case "LabGpuPowerMaxV1":
                        // Presets save intent; the session controller owns capability checks, application and restoration.
                        return ConfigurationPresetAvailability.Yes();
                    case "LabCpuIdleStrategyV1":
                        PowerPlan.NativeLaboratoryPower power = new PowerPlan.NativeLaboratoryPower(); Guid plan = power.ActivePlan();
                        return Available(plan != Guid.Empty && power.PlanStatus(plan) == 1 && power.Read(plan, LaboratoryPower.Promote).HasValue && power.Read(plan, LaboratoryPower.Scaling).HasValue && power.Read(plan, LaboratoryPower.Disable).HasValue,
                            "当前电源计划的 CPU 空闲参数不支持或无法读取", "CPU idle parameters in the current power plan unsupported or unreadable");
                    case "LabEppYieldV1": return Available(LaboratoryEppEligibility.Current.Available, "本机不适用 EPP 功耗让路", "EPP yielding is not applicable here");
                    case "EnvPrivacyNtfsMemoryV1": case "EnvPrivacyBackgroundGlobalV1":
                        PrivacyPlan privacy = new PrivacyNativeBackend().Inspect(entry.Key, "");
                        return Available(privacy != null && privacy.Supported && !privacy.Managed && privacy.Targets != null && privacy.Targets.Length > 0,
                            privacy == null ? "适用性无法核验" : privacy.Note ?? privacy.UnavailableReason ?? "本机不适用", privacy == null ? "Eligibility unavailable" : privacy.Note ?? privacy.UnavailableReason ?? "Not applicable");
                    case "EnvPerformanceBundleV1": return ConfigurationPresetAvailability.Yes();
                    case "EnvStoragePowerGroupV1":
                        foreach (string member in PerformanceTuning.StorageMembers()) if (PerformanceActionBackend.EnvironmentTargets(member).Length > 0) return ConfigurationPresetAvailability.Yes();
                        return ConfigurationPresetAvailability.No(PerformancePolicy.Text("未找到已登记 StorPort 的设备", "No registered StorPort devices found"));
                    default: return ConfigurationPresetAvailability.Yes();
                }
            }
            catch (Exception ex) { return ConfigurationPresetAvailability.No(PerformancePolicy.Text("适用性检测失败：", "Eligibility check failed: ") + ex.GetType().Name); }
        }
        private static bool BadState(string state)
        { return state == "failed" || state == "restore-failed" || state == "stopped" || state == "unverified" || state == "unsupported"; }
        private static bool Wait(Func<Action, bool> begin, Func<bool> cancelled)
        {
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                if (cancelled != null && cancelled()) return false;
                if (!begin(delegate { done.Set(); })) return false;
                // Do not abandon the callback: exit/reset must never race a still-running system transaction.
                done.WaitOne();
                return true;
            }
        }
        private static bool SetEnvironment(string key, bool on, Func<bool> cancelled, out string detail, bool selectCpuScope = false, bool expandedCpuScope = true)
        {
            bool accepted = Wait(done => selectCpuScope ? PerformanceTuning.SetCpuMitigations(on, done, expandedCpuScope) : PerformanceTuning.SetEnvironment(key, on, done), cancelled);
            string state = PerformanceTuning.State(key); detail = PerformanceTuning.Label(state);
            bool ok = accepted && !BadState(state) && Settings.LoadCached(key, false) == on;
            if (!ok && on && state != "working")
            {
                // A target can disappear after preflight. Do not leave a failed/unsupported ON intent behind.
                bool restored = Wait(done => PerformanceTuning.SetEnvironment(key, false, done), null);
                detail += Settings.LoadCached(key, false) ? PerformancePolicy.Text("；启用请求撤回未完成", "; enable request withdrawal incomplete")
                    : PerformancePolicy.Text("；已撤回启用请求", "; enable request withdrawn");
                if (!restored || BadState(PerformanceTuning.State(key))) detail += PerformancePolicy.Text("，恢复待处理", ", recovery remains pending");
            }
            return ok;
        }
        private static bool SetGroup(bool storage, bool on, Func<bool> cancelled, out string detail, string onlyMember = null)
        {
            bool ok = true; List<string> descriptions = new List<string>();
            string[] members = onlyMember != null ? new[] { onlyMember } : storage ? PerformanceTuning.StorageMembers().ToArray() : bundle;
            foreach (string key in members)
            {
                if (cancelled != null && cancelled()) { detail = PerformancePolicy.Text("后续设置已停止", "Further settings cancelled"); return false; }
                bool applicable = !on || key == "EnvVbsDisabledV1" || PerformanceActionBackend.EnvironmentTargets(key).Length > 0;
                string memberDetail;
                if (key == "EnvVbsDisabledV1")
                {
                    string unsupportedReason = "";
                    bool requested = on && VbsTweak.CanConfigureForPreset(out unsupportedReason);
                    bool result = false;
                    bool queued = Wait(done => PerformanceTuning.QueueEnvironmentWork(delegate
                    {
                        try
                        {
                            lock (SystemConfigurationGate.Sync)
                            {
                                if ((cancelled == null || !cancelled()) && !PerformanceTuning.EnvironmentWorkClosing && Settings.SaveDurable(key, requested))
                                    result = PerformanceTuning.ApplyEnvironmentVbs(requested);
                            }
                        }
                        finally { done(); }
                    }), cancelled);
                    memberDetail = PerformanceTuning.Label(PerformanceTuning.State(key));
                    if (on && !requested) memberDetail = PerformancePolicy.Text("已跳过：", "Skipped: ") + unsupportedReason + "; " + memberDetail;
                    ok &= queued && result;
                    if (queued && !result && requested)
                    {
                        bool restored = false;
                        Wait(done => PerformanceTuning.QueueEnvironmentWork(delegate
                        {
                            try { lock (SystemConfigurationGate.Sync) { if (Settings.SaveDurable(key, false)) restored = PerformanceTuning.ApplyEnvironmentVbs(false); } }
                            finally { done(); }
                        }), null);
                        memberDetail += PerformancePolicy.Text("；已撤回启用请求", "; enable request withdrawn");
                        if (!restored) memberDetail += PerformancePolicy.Text("，恢复待处理", ", recovery remains pending");
                    }
                }
                else if (!applicable)
                {
                    bool restored = SetEnvironment(key, false, cancelled, out memberDetail);
                    ok &= restored;
                    memberDetail = PerformancePolicy.Text("不适用，已跳过", "Not applicable; skipped");
                }
                else ok &= SetEnvironment(key, on, cancelled, out memberDetail);
                descriptions.Add((PerformanceActionCatalog.Find(key) == null ? "VBS" : PerformanceActionCatalog.Find(key).Title) + ": " + memberDetail);
            }
            detail = string.Join("; ", descriptions.ToArray()); return ok;
        }
        public bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancelled, out string detail)
        {
            detail = ""; if (gameMode == null || (cancelled != null && cancelled())) return false;
            bool on = ConfigurationPresetEntry.IsEnabled(value); string key = entry.Key;
            if (key == BackgroundGovernancePolicy.SettingKey)
            {
                if (value != "0" && value != "1") return false;
                bool accepted = gameMode.TrySetBackgroundGovernanceMode(value == "1" ? 1 : 0);
                detail = accepted ? PerformancePolicy.Text("后台治理档位已保存", "Background governance level saved")
                    : PerformancePolicy.Text("后台治理档位未保存", "Background governance level was not saved");
                return accepted;
            }
            if (key == PerformanceActionCatalog.CpuMitigations)
            {
                if (value != "0" && value != "1" && value != "legacy") return false;
                if (value == "1" && !PerformanceTuning.CpuMitigationsExpansionSupported())
                { detail = PerformancePolicy.Text("本机不支持扩展 CPU 软件缓解配置", "Expanded CPU software-mitigation configuration unsupported"); return false; }
                if (value == "legacy" && PerformanceTuning.CpuMitigationsExpanded
                    && !SetEnvironment(key, false, cancelled, out detail, true)) return false;
                return SetEnvironment(key, on, cancelled, out detail, true, value != "legacy");
            }
            if (key == PerformanceTuning.EnvironmentBundleKey || key == PerformanceTuning.StorageGroupKey)
                return SetGroup(key == PerformanceTuning.StorageGroupKey, on, cancelled, out detail);
            if (key == "EnvVbsDisabledV1") return SetGroup(false, on, cancelled, out detail, key);
            if (key == "EnvNtfsLastAccessV1" || key == "EnvAudioIdleV1") return SetEnvironment(key, on, cancelled, out detail);
            if (key == PerformanceActionCatalog.Quantum)
            {
                bool accepted = Wait(done => PerformanceTuning.SetQuantumProfile(on ? 1 : 0, done), cancelled);
                string state = PerformanceTuning.State(key); detail = PerformanceTuning.Label(state);
                bool ok = accepted && !BadState(state);
                if (!ok && on && state != "working")
                {
                    bool restored = Wait(done => PerformanceTuning.SetQuantumProfile(0, done), null);
                    detail += Settings.LoadCached(key, false) ? PerformancePolicy.Text("；启用请求撤回未完成", "; enable request withdrawal incomplete")
                        : PerformancePolicy.Text("；已撤回启用请求", "; enable request withdrawn");
                    if (!restored || BadState(PerformanceTuning.State(key))) detail += PerformancePolicy.Text("，恢复待处理", ", recovery remains pending");
                }
                return ok;
            }
            if (PrivacyControl.IsAction(key))
            {
                bool accepted = Wait(done => PrivacyControl.Set(key, on, "", done), cancelled);
                PrivacyResult state = PrivacyControl.State(key); detail = state.Detail;
                bool ok = accepted && PrivacyControl.PreferenceEnabled(key) == on && (on || !state.HasRecovery) && !BadState(state.State) && state.State != "managed";
                if (!ok && on && state.State != "working" && !state.Busy)
                {
                    bool restored = Wait(done => PrivacyControl.Set(key, false, "", done), null);
                    detail += PrivacyControl.PreferenceEnabled(key) ? PerformancePolicy.Text("；启用请求撤回未完成", "; enable request withdrawal incomplete")
                        : PerformancePolicy.Text("；已撤回启用请求", "; enable request withdrawn");
                    if (!restored || PrivacyControl.State(key).HasRecovery) detail += PerformancePolicy.Text("，恢复待处理", ", recovery remains pending");
                }
                return ok;
            }
            return gameMode.ApplyConfigurationPresetPreference(key, value, out detail);
        }
    }
}
