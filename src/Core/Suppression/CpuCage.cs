// @author bdth 2074055628@qq.com
// 文件用途 独占档资源牢笼 把持续重负载的后台装进 Job 硬性限速 释放靠关额度而非等进程退出
// 内核对象名随最后一个句柄消亡 崩溃后无法按名重开 因此由守护子进程持句柄
// 守护只在牢笼激活期间存在 等到主进程退出即清额度自灭 绝不设置 kill-on-close

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class CpuCage
    {
        // 所有被关的重负载合计最多吃全机 10% 周期 上限故意宽松 崩溃残留时伤害有界
        private const uint CapPercentOfSystem = 10;
        // 持续半个物理核以上跑满 10 秒才算重负载 短突发不关
        private const double HotCoreShare = 0.5;
        private const int HotSustainSeconds = 10;
        private const int MaxCaged = 16;
        private const string JournalHeader = "PAVISE_CAGE_V1";
        internal const string StateFileName = "Pavise.cage.state";

        internal struct Candidate
        {
            public int Pid;
            public string Name;
            public long Creation;
            public long Cpu;
        }

        private sealed class Track
        {
            public long Creation;
            public long LastCpu;
            public long LastTicks;
            public long HotSinceTicks;
            public bool Caged;
            public bool Refused;
        }

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private static IntPtr job;
        private static string jobName;
        private static string dataDir;
        private static int jobSeq;
        private static int cagedCount;
        private static System.Diagnostics.Process guard;

        public static void Configure(string dir)
        {
            lock (sync) dataDir = dir;
        }

        public static int CagedCount { get { lock (sync) return cagedCount; } }

        public static void Observe(bool active, List<Candidate> candidates)
        {
            lock (sync)
            {
                if (!active)
                {
                    ReleaseLocked();
                    tracks.Clear();
                    return;
                }
                long now = DateTime.UtcNow.Ticks;
                var seen = new HashSet<int>();
                if (candidates != null)
                    foreach (Candidate c in candidates)
                    {
                        if (c.Pid <= 4 || c.Creation <= 0) continue;
                        seen.Add(c.Pid);
                        ObserveOneLocked(c, now);
                    }
                // 被关的进程从合格名单里消失 若还活着说明它被白名单或豁免救出 整笼放开重新计
                // 单个成员无法退出 Job 只能整体关额度
                var gone = new List<int>();
                bool freedLive = false;
                foreach (KeyValuePair<int, Track> kv in tracks)
                {
                    if (seen.Contains(kv.Key)) continue;
                    if (kv.Value.Caged && ProcessAlive(kv.Key, kv.Value.Creation)) { freedLive = true; break; }
                    gone.Add(kv.Key);
                }
                if (freedLive)
                {
                    ReleaseLocked();
                    tracks.Clear();
                    return;
                }
                foreach (int pid in gone) tracks.Remove(pid);
            }
        }

        private static void ObserveOneLocked(Candidate c, long now)
        {
            Track t;
            if (!tracks.TryGetValue(c.Pid, out t) || t.Creation != c.Creation)
            {
                tracks[c.Pid] = new Track { Creation = c.Creation, LastCpu = c.Cpu, LastTicks = now };
                return;
            }
            if (t.Caged || t.Refused) return;
            long dt = now - t.LastTicks;
            long dc = c.Cpu - t.LastCpu;
            t.LastCpu = c.Cpu;
            t.LastTicks = now;
            if (dt <= 0) return;
            double cores = dc / (double)dt;
            if (cores < HotCoreShare) { t.HotSinceTicks = 0; return; }
            if (t.HotSinceTicks == 0) { t.HotSinceTicks = now; return; }
            if (now - t.HotSinceTicks < HotSustainSeconds * TimeSpan.TicksPerSecond) return;
            if (cagedCount >= MaxCaged) return;
            if (TryCageLocked(c.Pid, c.Name, c.Creation))
            {
                t.Caged = true;
                cagedCount++;
                Logger.Log(Lang.T("log.cpucage.1") + c.Name + " pid " + c.Pid
                    + Lang.T("log.cpucage.2") + CapPercentOfSystem + "%");
            }
            else t.Refused = true;
        }

        private static bool TryCageLocked(int pid, string name, long creation)
        {
            if (!EnsureJobLocked()) return false;
            IntPtr h = Native.OpenProcess(
                ProcessSetQuota | ProcessTerminate | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long cur, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out cur, out cpu, out io) || cur != creation) return false;
                bool already;
                if (IsProcessInJob(h, job, out already) && already) return true;
                return AssignProcessToJobObject(job, h);
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool EnsureJobLocked()
        {
            if (job != IntPtr.Zero) return true;
            int selfPid;
            string exe;
            using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                try { exe = self.MainModule.FileName; }
                catch { return false; }
            }
            string name = "Local\\Pavise.Cage." + selfPid + "." + (++jobSeq);
            var sa = new SecurityAttributes
            {
                Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                InheritHandle = 1
            };
            IntPtr created = CreateJobObjectW(ref sa, name);
            if (created == IntPtr.Zero) return false;
            var info = new CpuRateInfo { ControlFlags = RateEnable | RateHardCap, CpuRate = CapPercentOfSystem * 100 };
            if (!SetInformationJobObject(created, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf(typeof(CpuRateInfo)))
                || !RateActive(created)
                || !WriteJournal(name))
            {
                RollbackJobLocked(created);
                return false;
            }
            System.Diagnostics.Process spawned = StartGuard(exe, selfPid, created);
            if (spawned == null)
            {
                RollbackJobLocked(created);
                DeleteJournal();
                return false;
            }
            job = created;
            jobName = name;
            guard = spawned;
            return true;
        }

        private static void RollbackJobLocked(IntPtr handle)
        {
            var off = new CpuRateInfo();
            SetInformationJobObject(handle, JobObjectCpuRateControlInformation, ref off, Marshal.SizeOf(typeof(CpuRateInfo)));
            CloseHandle(handle);
        }

        private static System.Diagnostics.Process StartGuard(string exe, int watchPid, IntPtr jobHandle)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--cage-guard " + watchPid + " " + jobHandle.ToInt64()
                        + " \"" + JournalPath() + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                return System.Diagnostics.Process.Start(psi);
            }
            catch { return null; }
        }

        // 守护模式入口 持继承来的 Job 句柄等待主进程退出 一旦退出立即清额度并自灭
        public static void RunGuard(string watchPidText, string handleText, string journal)
        {
            int watchPid;
            long handleValue;
            if (!int.TryParse(watchPidText, out watchPid) || !long.TryParse(handleText, out handleValue)) return;
            IntPtr handle = new IntPtr(handleValue);
            IntPtr parent = Native.OpenProcess(Synchronize, false, watchPid);
            if (parent != IntPtr.Zero)
            {
                try { WaitForSingleObject(parent, Infinite); }
                finally { Native.CloseHandle(parent); }
            }
            var off = new CpuRateInfo();
            SetInformationJobObject(handle, JobObjectCpuRateControlInformation, ref off, Marshal.SizeOf(typeof(CpuRateInfo)));
            CloseHandle(handle);
            try { if (!string.IsNullOrEmpty(journal) && File.Exists(journal)) File.Delete(journal); }
            catch { }
        }

        public static void Release()
        {
            lock (sync)
            {
                ReleaseLocked();
                tracks.Clear();
            }
        }

        private static void ReleaseLocked()
        {
            if (job == IntPtr.Zero)
            {
                cagedCount = 0;
                return;
            }
            var off = new CpuRateInfo();
            bool cleared = SetInformationJobObject(job, JobObjectCpuRateControlInformation, ref off,
                Marshal.SizeOf(typeof(CpuRateInfo))) && !RateActive(job);
            CloseHandle(job);
            job = IntPtr.Zero;
            jobName = null;
            if (guard != null)
            {
                try { if (!guard.HasExited) guard.Kill(); }
                catch { }
                try { guard.Dispose(); } catch { }
                guard = null;
            }
            if (cagedCount > 0)
                Logger.Log(Lang.T("log.cpucage.3") + cagedCount + Lang.T("log.cpucage.4")
                    + (cleared ? "" : Lang.T("log.cpucage.5")));
            cagedCount = 0;
            DeleteJournal();
        }

        private static bool RateActive(IntPtr handle)
        {
            var info = new CpuRateInfo();
            if (!QueryInformationJobObject(handle, JobObjectCpuRateControlInformation, ref info,
                    Marshal.SizeOf(typeof(CpuRateInfo)), IntPtr.Zero)) return false;
            return (info.ControlFlags & RateEnable) != 0;
        }

        // 正常崩溃由守护进程清额度并删状态文件 走到这里说明守护也被杀了
        // 名字已随句柄消亡无法重开 只能明示残留 额度随目标进程退出自动消失
        public static void HealFromCrash(string dir)
        {
            Configure(dir);
            string path = JournalPath();
            if (path == null || !File.Exists(path)) return;
            Logger.Log(Lang.T("log.cpucage.6"));
            DeleteJournal();
        }

        private static bool ProcessAlive(int pid, long creation)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                long cur, cpu; ulong io;
                return Native.StillActive(h)
                    && Native.QueryProcessSample(h, out cur, out cpu, out io) && cur == creation;
            }
            finally { Native.CloseHandle(h); }
        }

        private static string JournalPath()
        {
            return dataDir == null ? null : Path.Combine(dataDir, StateFileName);
        }

        private static bool WriteJournal(string name)
        {
            string path = JournalPath();
            if (path == null) return false;
            try
            {
                File.WriteAllLines(path, new[] { JournalHeader, name }, new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        private static void DeleteJournal()
        {
            string path = JournalPath();
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch { }
        }

#if PAVISE_SELFTEST
        internal static bool ProbeCage(int pid, string name, long creation)
        {
            lock (sync)
            {
                if (!TryCageLocked(pid, name, creation)) return false;
                cagedCount++;
                return true;
            }
        }

        internal static string ProbeJobName { get { lock (sync) return jobName; } }

        internal static bool ProbeRateActiveByName(string name)
        {
            IntPtr h = OpenJobObjectW(JobQuery, false, name);
            if (h == IntPtr.Zero) return false;
            try { return RateActive(h); }
            finally { Native.CloseHandle(h); }
        }

        internal static bool ProbeStartGuard(int watchPid)
        {
            lock (sync)
            {
                if (job == IntPtr.Zero) return false;
                string exe;
                using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess())
                    exe = self.MainModule.FileName;
                System.Diagnostics.Process extra = StartGuard(exe, watchPid, job);
                if (extra == null) return false;
                extra.Dispose();
                return true;
            }
        }
#endif

        private const int ProcessSetQuota = 0x0100;
        private const int ProcessTerminate = 0x0001;
        private const int Synchronize = 0x00100000;
        private const uint Infinite = 0xFFFFFFFF;
        private const int JobObjectCpuRateControlInformation = 15;
        private const uint RateEnable = 1;
        private const uint RateHardCap = 4;
        private const uint JobQuery = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuRateInfo
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int InheritHandle;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(ref SecurityAttributes attributes, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenJobObjectW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint timeoutMs);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr jobHandle, out bool inJob);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr jobHandle, int infoClass, ref CpuRateInfo info, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr jobHandle, int infoClass, ref CpuRateInfo info, int size, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
