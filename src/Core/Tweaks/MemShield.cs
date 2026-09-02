// @author bdth 2074055628@qq.com
// 文件用途 内存驻留已下架 只保留崩溃残账的配额还原 快照清账与残留报告
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 内存驻留 2.1.3.3 上架 随后下架 不再有任何挂载入口
    //   旧版本对局中崩溃会在快照里留下目标进程的原工作集配额 这里负责启动时还原并清账
    //   配额随进程退出自然消失 游戏没了或 PID 已被复用就直接清记录
    //   目标还活着但还原失败时保留记录 交给下次启动或重置流程
    internal static class MemShield
    {
        internal const string SnapKey = "MemShieldSnap";

        internal const uint HardMinEnable = 0x1;

        private static readonly object opLk = new object();
        private static readonly object lk = new object();
        private static bool recoveryBlocked;

        // Pavise 异常退出时配额还挂在游戏进程上 下次启动补撤
        public static bool HealFromCrash()
        {
            lock (opLk)
            {
                string snapshot = Settings.LoadStr(SnapKey, "");
                if (snapshot.Length == 0)
                {
                    lock (lk) recoveryBlocked = false;
                    return true;
                }
                int pid;
                long creation;
                ulong min, max;
                uint flags;
                if (!DecodeSnapshot(snapshot, out pid, out creation, out min, out max, out flags))
                {
                    // 快照坏了没法安全还原 保留记录不清 由重置流程处置
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                bool restored;
                try { restored = RestoreQuota(pid, creation, min, max, flags); }
                catch { restored = false; }
                if (!restored)
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                if (!Settings.SaveStr(SnapKey, "") || Settings.LoadStr(SnapKey, "").Length != 0)
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                lock (lk) recoveryBlocked = false;
                return true;
            }
        }

        public static bool HasResidue()
        {
            lock (lk) if (recoveryBlocked) return true;
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        internal static string EncodeSnapshot(int pid, long creation, ulong min, ulong max, uint flags)
        {
            return "1|" + pid.ToString(CultureInfo.InvariantCulture)
                + "|" + creation.ToString(CultureInfo.InvariantCulture)
                + "|" + min.ToString(CultureInfo.InvariantCulture)
                + "|" + max.ToString(CultureInfo.InvariantCulture)
                + "|" + flags.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool DecodeSnapshot(string snap, out int pid, out long creation,
            out ulong min, out ulong max, out uint flags)
        {
            pid = 0; creation = 0; min = 0; max = 0; flags = 0;
            string[] parts = (snap ?? "").Split('|');
            return parts.Length == 6 && parts[0] == "1"
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out creation)
                && ulong.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out min)
                && ulong.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out max)
                && uint.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out flags)
                && pid > 0 && creation > 0;
        }

        private static bool RestoreQuota(int pid, long creation, ulong min, ulong max, uint flags)
        {
#if PAVISE_SELFTEST
            if (RestoreForTest != null) return RestoreForTest(pid, creation, min, max, flags);
#endif
            if (pid <= 0 || creation <= 0) return false;
            bool gone;
            long liveCreation;
            ulong ws, curMin, curMax;
            uint curFlags;
            if (!TryQueryTarget(pid, out gone, out liveCreation, out ws,
                    out curMin, out curMax, out curFlags))
                return gone;
            if (liveCreation != creation) return true;
            if ((curFlags & HardMinEnable) == 0) return true;
            TrySetTarget(pid, creation, min, max, flags);
            if (!TryQueryTarget(pid, out gone, out liveCreation, out ws,
                    out curMin, out curMax, out curFlags))
                return gone;
            return liveCreation != creation || (curFlags & HardMinEnable) == 0;
        }

        // 两个原语 隔离测试必须注入 生产走原生调用
        private static bool TryQueryTarget(int pid, out bool gone, out long creation,
            out ulong workingSet, out ulong min, out ulong max, out uint flags)
        {
            gone = false; creation = 0; workingSet = 0; min = 0; max = 0; flags = 0;
#if PAVISE_SELFTEST
            if (QueryForTest != null)
                return QueryForTest(pid, out gone, out creation, out workingSet, out min, out max, out flags);
            throw new InvalidOperationException("MemShield target query requires an injected test double.");
#else
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero)
            {
                gone = Native.LastOpenProcessFailureWasNoSuchProcess();
                return false;
            }
            try
            {
                long cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                IntPtr rawMin, rawMax;
                uint rawFlags;
                if (!GetProcessWorkingSetSizeEx(h, out rawMin, out rawMax, out rawFlags)) return false;
                min = (ulong)rawMin.ToInt64(); max = (ulong)rawMax.ToInt64(); flags = rawFlags;
                var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
                if (!GetProcessMemoryInfo(h, out counters, counters.Size)) return false;
                workingSet = (ulong)counters.WorkingSetSize.ToInt64();
                return true;
            }
            catch { return false; }
            finally { Native.CloseHandle(h); }
#endif
        }

        private static void TrySetTarget(int pid, long creation, ulong min, ulong max, uint flags)
        {
#if PAVISE_SELFTEST
            if (SetForTest != null) { SetForTest(pid, creation, min, max, flags); return; }
            throw new InvalidOperationException("MemShield quota writes require an injected test double.");
#else
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) return;
            try
            {
                long liveCreation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out liveCreation, out cpu, out io)
                    || liveCreation != creation) return;
                SetProcessWorkingSetSizeEx(h, new IntPtr((long)min), new IntPtr((long)max), flags);
            }
            catch { }
            finally { Native.CloseHandle(h); }
#endif
        }

#if PAVISE_SELFTEST
        internal delegate bool TargetQueryOverride(int pid, out bool gone, out long creation,
            out ulong workingSet, out ulong min, out ulong max, out uint flags);
        internal delegate void TargetSetOverride(int pid, long creation, ulong min, ulong max, uint flags);
        internal static TargetQueryOverride QueryForTest;
        internal static TargetSetOverride SetForTest;
        internal static Func<int, long, ulong, ulong, uint, bool> RestoreForTest;
        internal static bool RecoveryBlockedForTest { get { lock (lk) return recoveryBlocked; } }

        internal static void ResetForTest()
        {
            lock (opLk)
            lock (lk)
            {
                recoveryBlocked = false;
                QueryForTest = null;
                SetForTest = null;
                RestoreForTest = null;
            }
        }
#endif

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessWorkingSetSizeEx(IntPtr process,
            out IntPtr minimum, out IntPtr maximum, out uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr process,
            IntPtr minimum, IntPtr maximum, uint flags);
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process,
            out ProcessMemoryCounters counters, uint size);
    }
}
