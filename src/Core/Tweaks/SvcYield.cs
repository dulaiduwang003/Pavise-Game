// @author bdth 2074055628@qq.com
// 文件用途 服务让路 已退役 仅保留还原能力
// 退役原因 它引流的诊断/更新/打印类服务本就几乎不吃 CPU 挪核收益≈0;而真正的游戏核隔离由后台压制系统(SuppressionCore + CpuSets)已覆盖
// 仅保留 Restore/HasResidue/HealFromCrash 清理旧版本把服务宿主进程引到后台核的残留

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SvcYield
    {
        private const string Flag = "SvcYieldApplied";
        private static readonly object lk = new object();

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

        private static void SaveApplied(List<KeyValuePair<int, long>> list)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<int, long> kv in list) parts.Add(kv.Key + ":" + kv.Value);
            Settings.SaveStr(Flag, string.Join("|", parts.ToArray()));
        }

        public static bool Restore()
        {
            lock (lk)
            {
                List<KeyValuePair<int, long>> applied = LoadApplied();
                if (applied.Count == 0) return true;
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
                return remain.Count == 0;
            }
        }

        public static bool HasResidue() { return Settings.LoadStr(Flag, "").Length > 0; }

        public static void HealFromCrash() { if (HasResidue()) Restore(); }
    }
}
