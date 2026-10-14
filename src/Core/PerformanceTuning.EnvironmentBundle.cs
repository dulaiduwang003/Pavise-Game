using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        internal const string EnvironmentBundleKey = "EnvPerformanceBundleV1";
        internal static string EnvironmentBundleTitle
        { get { return AutomaticPolicy.Text("系统性能增强 · 需重启", "System performance boost · restart required"); } }
        internal static string EnvironmentBundleDetail
        { get { return AutomaticPolicy.Text(
            "统一控制关闭 VBS（含内存完整性和 hypervisor）、CPU 软件缓解、NTFS 最后访问时间，以及三项存储和一项音频电源管理，共七项。开启或恢复后需重启电脑；关闭时恢复原配置。不适用项自动跳过。关闭 hypervisor 会影响 WSL2、Hyper-V 等虚拟化功能。",
            "Combines seven settings: disabling VBS (including Memory Integrity and the hypervisor), CPU software mitigations, NTFS last-access updates, three storage power settings and audio power management. Restart after enabling or restoring. Switching off restores original settings; unsupported targets are skipped. Disabling the hypervisor affects WSL2, Hyper-V and other virtualization features.") + EnvironmentBundleIssues(); } }
        private static string EnvironmentMemberName(string key)
        {
            switch (key)
            {
                case PerformanceActionCatalog.CpuMitigations: return AutomaticPolicy.Text("CPU 缓解", "CPU mitigations");
                case PerformanceActionCatalog.LastAccess: return AutomaticPolicy.Text("NTFS 访问时间", "NTFS last access");
                case PerformanceActionCatalog.StorageIdle: return AutomaticPolicy.Text("存储空闲电源", "Storage idle power");
                case PerformanceActionCatalog.StorageRuntime: return AutomaticPolicy.Text("存储运行时电源", "Storage runtime power");
                case PerformanceActionCatalog.StorageCold: return AutomaticPolicy.Text("存储 D3Cold", "Storage D3Cold");
                case PerformanceActionCatalog.AudioIdle: return AutomaticPolicy.Text("音频空闲电源", "Audio idle power");
                default: return "VBS";
            }
        }
        private static string EnvironmentBundleIssues()
        {
            var failed = new List<string>(); var restoreFailed = new List<string>();
            foreach (string key in EnvironmentMembers())
            {
                string state = State(key);
                if (state == "failed" || state == "stopped") failed.Add(EnvironmentMemberName(key));
                if (state == "restore-failed") restoreFailed.Add(EnvironmentMemberName(key));
            }
            string detail = "";
            if (failed.Count > 0) detail += "\n" + AutomaticPolicy.Text("执行受限项：", "Blocked settings: ") + string.Join(", ", failed.ToArray());
            if (restoreFailed.Count > 0) detail += "\n" + AutomaticPolicy.Text("恢复未完成项：", "Restoration incomplete: ") + string.Join(", ", restoreFailed.ToArray());
            return detail;
        }
        private static List<string> EnvironmentMembers()
        {
            var members = new List<string>();
            foreach (PerformanceAction action in PerformanceActionCatalog.All) if (EnvironmentBundleMember(action)) members.Add(action.Key);
            members.Add(EnvironmentVbsKey);
            return members;
        }
        private static bool EnvironmentBundleMember(PerformanceAction action)
        {
            return action.Environment && action.Key != PerformanceActionCatalog.Quantum;
        }
        private static bool EnvironmentPreference(string key)
        {
            if (key == EnvironmentVbsKey) return Settings.LoadCached(key, VbsTweak.OwnsState);
            return Settings.LoadCached(key, false);
        }
        internal static int EnvironmentEnabledCount()
        {
            int count = 0;
            foreach (string key in EnvironmentMembers()) if (EnvironmentPreference(key)) count++;
            return count;
        }
        internal static bool EnvironmentBundleEnabled { get { return EnvironmentEnabledCount() > 0; } }
        internal static bool EnvironmentBundleBusy()
        { lock (gate) return busy.Count > 0; }
        internal static string EnvironmentBundleState()
        {
            if (EnvironmentBundleBusy()) return "working";
            int enabled = 0, applicable = 0;
            bool failed = false, isolated = false, restoreFailed = false, pending = false, restorePending = false, unknown = false, restored = false;
            List<string> members = EnvironmentMembers();
            foreach (string key in members)
            {
                bool on = EnvironmentPreference(key);
                string state = State(key);
                if (on) { enabled++; if (state != "unsupported") applicable++; }
                failed |= state == "failed";
                isolated |= state == "stopped";
                restoreFailed |= state == "restore-failed";
                pending |= state == "pending-reboot";
                restorePending |= state == "restore-pending-reboot";
                unknown |= state == "unverified" || (on && state.Length == 0);
                restored |= state == "restored";
            }
            if (restoreFailed) return "restore-failed";
            if (isolated) return "stopped";
            if (failed) return "failed";
            if (enabled > 0 && enabled < members.Count) return "partial";
            if (pending) return "pending-reboot";
            if (restorePending) return "restore-pending-reboot";
            if (unknown) return "unverified";
            if (enabled > 0) return applicable == 0 ? "unsupported" : "ready";
            return restored ? "restored" : "off";
        }
        internal static string EnvironmentBundleLabel()
        {
            string state = EnvironmentBundleState();
            if (state == "partial") return AutomaticPolicy.Text("部分启用 · 保留旧版选择 · ", "Partly enabled; existing choices retained · ")
                + EnvironmentEnabledCount() + "/" + EnvironmentMembers().Count;
            string label = state == "ready" ? Lang.T("env.performance.ready") : Label(state);
            if (state == "working") return label;
            int unsupported = 0, failures = 0;
            bool pending = false;
            foreach (string key in EnvironmentMembers())
            {
                string memberState = State(key);
                if (memberState == "unsupported") unsupported++;
                if (memberState == "failed" || memberState == "stopped" || memberState == "restore-failed") failures++;
                pending |= memberState == "pending-reboot" || memberState == "restore-pending-reboot";
            }
            if (failures > 0) label = failures + AutomaticPolicy.Text(" 项受限 · 展开查看", " blocked settings · expand for details")
                + (pending ? AutomaticPolicy.Text(" · 已写入项需重启", " · Written settings require restart") : "");
            return unsupported == 0 ? label : label + AutomaticPolicy.Text(" · 跳过不适用项：", " · Unsupported settings skipped: ") + unsupported;
        }
        private static void RestoreBundlePreferences(List<string> members, bool[] previous)
        {
            for (int i = 0; i < members.Count; i++)
                if (EnvironmentPreference(members[i]) != previous[i] && !Settings.SaveDurable(members[i], previous[i]))
                    Logger.Warn("Environment preference restore | " + members[i]);
        }
        internal static bool SetEnvironmentBundle(bool on, Action completed)
        {
            List<string> members = EnvironmentMembers();
            var previous = new bool[members.Count];
            bool queued = false;
            lock (gate)
            {
                if (EnvironmentWorkClosing || busy.Count > 0) return false;
                // 保存所有请求偏好后才允许系统写入。
                for (int i = 0; i < members.Count; i++) previous[i] = EnvironmentPreference(members[i]);
                for (int i = 0; i < members.Count; i++)
                    if (!Settings.SaveDurable(members[i], on)) { RestoreBundlePreferences(members, previous); return false; }
                busy.Add(EnvironmentBundleKey);
                try
                {
                    queued = QueueEnvironmentWork(delegate
                    {
                        try { ApplyEnvironmentBundle(on); }
                        finally
                        {
                            lock (gate)
                            {
                                busy.Remove(EnvironmentBundleKey);
                                Monitor.PulseAll(gate);
                            }
                            if (completed != null) try { completed(); } catch { }
                        }
                    });
                }
                catch (Exception error) { Logger.Warn("Environment configuration queue | " + error.Message); }
                if (!queued)
                    lock (gate)
                    {
                        busy.Remove(EnvironmentBundleKey);
                        Monitor.PulseAll(gate);
                        RestoreBundlePreferences(members, previous);
                    }
            }
            return queued;
        }
        internal static bool ApplyEnvironmentBundle(bool on)
        {
            bool ok = true;
            foreach (PerformanceAction action in PerformanceActionCatalog.All)
            {
                if (!EnvironmentBundleMember(action)) continue;
                if (EnvironmentWorkClosing) return false;
                try
                {
                    if (!ApplyEnvironment(action.Key, on) && State(action.Key) != "unsupported") ok = false;
                }
                catch (Exception error)
                {
                    ok = false;
                    SetState(action.Key, on ? "failed" : "restore-failed");
                    Logger.Warn(action.Title + " | " + error.Message);
                }
            }
            if (EnvironmentWorkClosing) return false;
            try { if (!ApplyEnvironmentVbs(on)) ok = false; }
            catch (Exception error)
            {
                ok = false; SetState(EnvironmentVbsKey, on ? "failed" : "restore-failed");
                Logger.Warn("VBS | " + error.Message);
            }
            return ok;
        }
    }
}
