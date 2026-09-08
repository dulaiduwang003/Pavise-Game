// @author bdth 2074055628@qq.com
// 文件用途 关闭系统内存压缩与页合并 大内存机器专用 重启彻底生效 收据只回启原本开着的
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class MemCompressTweak
    {
        private const string OnKey = "MemCompressOffByPavise";
        private const string SnapKey = "PrevMMAgent";
        // 名义 24GB 减去硬件保留后仍稳在 23.5GB 之上 16GB 机器够不到
        private const double MinTotalBytes = 23.5 * 1073741824.0;
        private static readonly object lk = new object();

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile,
                TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool OwnsState
        { get { return EnabledByPavise || Settings.LoadStr(SnapKey, "").Length > 0; } }

        public static bool RamEligible()
        {
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                return GlobalMemoryStatusEx(ref status) && status.TotalPhys >= MinTotalBytes;
            }
            catch { return false; }
        }

        // Get-MMAgent 属性是 .NET bool 的 ToString 不随系统语言变
        internal static bool ParseState(string output, out bool compression, out bool combining)
        {
            compression = combining = false;
            if (string.IsNullOrEmpty(output)) return false;
            string[] parts = output.Trim().Split(',');
            if (parts.Length < 2) return false;
            bool ok1 = bool.TryParse(parts[0].Trim(), out compression);
            bool ok2 = bool.TryParse(parts[1].Trim(), out combining);
            return ok1 && ok2;
        }

        internal static bool ParseSnapshot(string snapshot, out bool compression, out bool combining)
        {
            compression = combining = false;
            if (string.IsNullOrEmpty(snapshot)) return false;
            string[] parts = snapshot.Split(',');
            if (parts.Length != 2
                || (parts[0] != "0" && parts[0] != "1")
                || (parts[1] != "0" && parts[1] != "1")) return false;
            compression = parts[0] == "1";
            combining = parts[1] == "1";
            return true;
        }

        internal static bool SnapshotRestored(string snapshot, bool compression, bool combining)
        {
            bool wantedCompression, wantedCombining;
            return ParseSnapshot(snapshot, out wantedCompression, out wantedCombining)
                && (!wantedCompression || compression)
                && (!wantedCombining || combining);
        }

        // 快照格式 1,0 表示当时压缩开着 合并关着 还原只回启当时开着的
        internal static string RestoreArguments(string snapshot)
        {
            bool compression, combining;
            if (!ParseSnapshot(snapshot, out compression, out combining)) return "";
            string args = "";
            if (compression) args += " -MemoryCompression";
            if (combining) args += " -PageCombining";
            return args;
        }

        private static bool QueryState(out bool compression, out bool combining)
        {
            compression = combining = false;
#if PAVISE_SELFTEST
            if (QueryForTest != null) return QueryForTest(out compression, out combining);
            throw new InvalidOperationException("MMAgent state queries require an injected test double.");
#else
            string output;
            if (!PsRunner.Run(
                "$m = Get-MMAgent; Write-Output ($m.MemoryCompression.ToString() + ',' + $m.PageCombining.ToString())",
                "mmagent-query", 20000, out output)) return false;
            return ParseState(output, out compression, out combining);
#endif
        }

        private static bool RunCommand(string command, string label, out string output)
        {
#if PAVISE_SELFTEST
            if (RunForTest != null) return RunForTest(command, label, out output);
            throw new InvalidOperationException("MMAgent writes require an injected test double.");
#else
            return PsRunner.Run(command, label, 20000, out output);
#endif
        }

        public static bool CurrentlyOff()
        {
            bool compression, combining;
            return QueryState(out compression, out combining) && !compression && !combining;
        }

        public static bool Enable()
        {
            lock (lk)
            {
                bool beforeCompression, beforeCombining;
                if (!QueryState(out beforeCompression, out beforeCombining))
                { Logger.Warn(Lang.T("log.memcompress.1")); return false; }
                // 正常入口先用 CurrentlyOff 跳过 这里再守一道并发变化
                // 不写快照也不写归属标记 外部已经关掉的状态还归外部
                if (!beforeCompression && !beforeCombining) return true;
                string snapshot;
                if (!Settings.TryLoadStr(SnapKey, out snapshot))
                { Logger.Log(Lang.T("log.memcompress.2")); return false; }
                if (snapshot.Length == 0)
                {
                    snapshot = (beforeCompression ? "1" : "0") + ","
                        + (beforeCombining ? "1" : "0");
                    Settings.SaveStr(SnapKey, snapshot);
                    if (Settings.LoadStr(SnapKey, "") != snapshot)
                    { Logger.Log(Lang.T("log.memcompress.2")); return false; }
                }
                else
                {
                    bool originalCompression, originalCombining;
                    if (!ParseSnapshot(snapshot, out originalCompression, out originalCombining))
                    { Logger.Log(Lang.T("log.memcompress.2")); return false; }
                }
                string output;
                // 退出码只能拿来诊断 生没生效以状态查询为准
                RunCommand("Disable-MMAgent -MemoryCompression -PageCombining",
                    "mmagent-off", out output);
                bool compression, combining;
                bool postOk = QueryState(out compression, out combining);
                // CIM 和 PowerShell 的退出码不是状态凭证 命令返回非零也没关系
                // 只要后验已经到目标 就留着原快照 认下这次实际变化
                if (postOk && !compression && !combining)
                {
                    Settings.Save(OnKey, true);
                    Logger.Log(Lang.T("log.memcompress.4"));
                    return true;
                }
                // 非零退出的详情 PsRunner 已经记了 这里只给用户一个稳定的功能级结论
                Logger.Log(Lang.T("log.memcompress.3"));

                // 状态其实还满足原快照 说明我们的改动没落下 可以销账
                // 否则立刻回滚 回滚也失败就得留着快照 交给极限账本接管重试
                bool recovered = postOk && SnapshotRestored(snapshot, compression, combining);
                if (!recovered) recovered = RestoreSnapshot(snapshot);
                if (recovered)
                {
                    if (!ClearOwnership()) Logger.Log(Lang.T("log.memcompress.5"));
                }
                else Logger.Log(Lang.T("log.memcompress.5"));
                return false;
            }
        }

        private static bool RestoreSnapshot(string snapshot)
        {
            bool wantedCompression, wantedCombining;
            if (!ParseSnapshot(snapshot, out wantedCompression, out wantedCombining)) return false;
            string args = RestoreArguments(snapshot);
            // 旧版本可能留下 0,0 收据 本来就没开启项 也就没有物理还原可做
            if (args.Length == 0) return true;
            string output;
            RunCommand("Enable-MMAgent" + args, "mmagent-restore", out output);
            bool compression, combining;
            return QueryState(out compression, out combining)
                && SnapshotRestored(snapshot, compression, combining);
        }

        private static bool ClearOwnership()
        {
            bool flagSaved = Settings.Save(OnKey, false);
            bool snapshotSaved = Settings.SaveStr(SnapKey, "");
            string snapshot;
            return flagSaved && snapshotSaved && !Settings.Load(OnKey, true)
                && Settings.TryLoadStr(SnapKey, out snapshot) && snapshot.Length == 0;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string snapshot = Settings.LoadStr(SnapKey, "");
                if (snapshot.Length > 0)
                {
                    // 命令非零照样看后验 已经恢复的幂等调用不能把收据永远卡在这
                    if (!RestoreSnapshot(snapshot))
                    { Logger.Log(Lang.T("log.memcompress.5")); return false; }
                }
                // 物理状态和两份归属记录都核完 极限层才能安全销账
                if (!ClearOwnership())
                { Logger.Log(Lang.T("log.memcompress.5")); return false; }
                Logger.Log(Lang.T("log.memcompress.6"));
                return true;
            }
        }

#if PAVISE_SELFTEST
        internal delegate bool QueryOverride(out bool compression, out bool combining);
        internal delegate bool RunOverride(string command, string label, out string output);
        internal static QueryOverride QueryForTest;
        internal static RunOverride RunForTest;

        internal static void ResetForTest()
        {
            lock (lk)
            {
                QueryForTest = null;
                RunForTest = null;
            }
        }
#endif
    }
}
