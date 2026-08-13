// @author bdth 2074055628@qq.com
// 文件用途 对局中把安全的系统服务引到后台核心给游戏让路 只引流不停服务
// 同一宿主进程内只要有一个服务不在白名单就整个不动 服务自带 CpuSets 的视为他人管理不碰

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SvcYield
    {
        internal static readonly string[] Allow =
        {
            "DiagTrack", "dmwappushservice", "diagsvc", "DPS", "WdiSystemHost", "WdiServiceHost",
            "diagnosticshub.standardcollector.service", "BITS", "DoSvc", "wuauserv", "UsoSvc",
            "WaaSMedicSvc", "TrustedInstaller", "Spooler", "PrintNotify", "WerSvc", "wercplsupport",
            "MapsBroker", "ClickToRunSvc", "PcaSvc", "DusmSvc", "wisvc", "RetailDemo",
            "SysMain", "WSearch"
        };

        private const string Flag = "SvcYieldApplied";
        private static readonly object lk = new object();
        private static bool active;
        private static bool noTargetLogged;
        private static bool squeezeFallbackLogged;
        private static string[] skipNames;

        internal static void SetPausedServices(string[] names)
        {
            skipNames = names != null && names.Length > 0 ? names : null;
        }

        internal static List<int> EligiblePids(List<KeyValuePair<string, int>> running, int selfPid)
        {
            return EligiblePids(running, selfPid, skipNames);
        }

        internal static List<int> EligiblePids(List<KeyValuePair<string, int>> running, int selfPid, string[] skip)
        {
            var result = new List<int>();
            if (running == null) return result;
            var allow = new HashSet<string>(Allow, StringComparer.OrdinalIgnoreCase);
            if (skip != null) foreach (string s in skip) allow.Remove(s);
            var byPid = new Dictionary<int, bool>();
            foreach (KeyValuePair<string, int> kv in running)
            {
                int pid = kv.Value;
                if (pid <= 4 || pid == selfPid) continue;
                bool ok = allow.Contains(kv.Key);
                bool cur;
                byPid[pid] = byPid.TryGetValue(pid, out cur) ? (cur && ok) : ok;
            }
            foreach (KeyValuePair<int, bool> kv in byPid) if (kv.Value) result.Add(kv.Key);
            return result;
        }

        private static List<KeyValuePair<int, long>> LoadApplied()
        {
            var list = new List<KeyValuePair<int, long>>();
            foreach (string s in Settings.LoadStr(Flag, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int sep = s.IndexOf(':');
                if (sep <= 0) continue;
                int pid; long creation;
                if (int.TryParse(s.Substring(0, sep), out pid)
                    && long.TryParse(s.Substring(sep + 1), out creation))
                    list.Add(new KeyValuePair<int, long>(pid, creation));
            }
            return list;
        }

        private static bool SaveApplied(List<KeyValuePair<int, long>> list)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<int, long> kv in list) parts.Add(kv.Key + ":" + kv.Value);
            string joined = string.Join("|", parts.ToArray());
            Settings.SaveStr(Flag, joined);
            return Settings.LoadStr(Flag, "") == joined;
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;
                if (!CpuTopology.HasBackgroundYieldTarget())
                {
                    if (!noTargetLogged)
                    {
                        noTargetLogged = true;
                        Logger.Log("服务让路 本机既没有后台核心分区也没有可挤压的后台核 此项不生效");
                    }
                    active = true;
                    return true;
                }
                if (!CpuTopology.HasEffectiveBackgroundPartition() && !squeezeFallbackLogged)
                {
                    squeezeFallbackLogged = true;
                    Logger.Log("服务让路 本机没有后台核心分区 改用后台挤压核 "
                        + CpuTopology.DescribeMask(CpuTopology.BackgroundSqueezeMask()) + " 作为落点");
                }

                if (!Native.TryEnableDebugPrivilege())
                {
                    Logger.Log("服务让路 调试特权未能启用 系统服务进程打不开 此项不生效");
                    active = true;
                    return true;
                }

                uint[] bg = CpuTopology.BackgroundYieldCpuSetIds();
                List<KeyValuePair<string, int>> running = SvcQuery.RunningServices();
                if (running == null)
                {
                    Logger.Log("服务让路 服务枚举失败 本轮跳过");
                    return false;
                }

                int selfPid = 0;
                try { using (var me = System.Diagnostics.Process.GetCurrentProcess()) selfPid = me.Id; } catch { }

                var candidates = new List<KeyValuePair<int, long>>(LoadApplied());
                var fresh = new List<KeyValuePair<int, long>>();
                foreach (int pid in EligiblePids(running, selfPid))
                {
                    bool known = false;
                    foreach (KeyValuePair<int, long> kv in candidates) if (kv.Key == pid) { known = true; break; }
                    if (known) continue;
                    IntPtr h = Native.OpenProcess(
                        Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        long creation; long cpu; ulong io;
                        if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) continue;
                        uint[] orig = Native.QueryCpuSets(h);
                        if (orig == null || orig.Length > 0) continue;
                        fresh.Add(new KeyValuePair<int, long>(pid, creation));
                    }
                    finally { Native.CloseHandle(h); }
                }

                if (fresh.Count == 0) { active = true; return true; }

                candidates.AddRange(fresh);
                if (!SaveApplied(candidates))
                {
                    Logger.Log("服务让路 名单无法持久化 本轮不做任何改动");
                    return false;
                }

                int steered = 0;
                var kept = new List<KeyValuePair<int, long>>(LoadApplied());
                foreach (KeyValuePair<int, long> kv in fresh)
                {
                    bool ok = false;
                    IntPtr h = Native.OpenProcess(
                        Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, kv.Key);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            long creation; long cpu; ulong io;
                            if (Native.QueryProcessSample(h, out creation, out cpu, out io) && creation == kv.Value)
                                ok = Native.TrySetCpuSetsVerified(h, bg);
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    if (ok) steered++;
                    else kept.RemoveAll(delegate(KeyValuePair<int, long> x) { return x.Key == kv.Key && x.Value == kv.Value; });
                }
                if (steered != fresh.Count) SaveApplied(kept);
                if (steered > 0) Logger.Log("服务让路 已把 " + steered + " 个系统服务进程引到后台核心 退出对局恢复");
                active = true;
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                List<KeyValuePair<int, long>> applied = LoadApplied();
                if (applied.Count == 0) { active = false; return true; }
                Native.TryEnableDebugPrivilege();

                int restored = 0;
                var remain = new List<KeyValuePair<int, long>>();
                foreach (KeyValuePair<int, long> kv in applied)
                {
                    IntPtr h = Native.OpenProcess(
                        Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, kv.Key);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        long creation; long cpu; ulong io;
                        if (!Native.QueryProcessSample(h, out creation, out cpu, out io) || creation != kv.Value) continue;
                        if (Native.RestoreCpuSetsVerified(h, new uint[0])) restored++;
                        else remain.Add(kv);
                    }
                    finally { Native.CloseHandle(h); }
                }

                SaveApplied(remain);
                if (restored > 0) Logger.Log("服务让路 已还原 " + restored + " 个服务进程");
                if (remain.Count > 0) Logger.Log("服务让路 仍有 " + remain.Count + " 个未能还原 保留记录待重试");
                active = false;
                return remain.Count == 0;
            }
        }

        public static bool HasResidue() { return Settings.LoadStr(Flag, "").Length > 0; }

        public static void HealFromCrash() { if (HasResidue()) Restore(); }
    }
}
