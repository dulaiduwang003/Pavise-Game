// @author bdth 2074055628@qq.com
// 文件用途 内存驻留 实验功能 物理内存吃紧时锁定游戏工作集下限 防止游戏页被修剪 退局撤销
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 内存驻留 面向内存吃紧时的换页卡顿
    //   物理内存持续吃紧时 给游戏进程设置硬性工作集下限
    //   内存管理器修剪时不会把游戏压到下限以下 纹理和逻辑页留在内存里
    //   这不是分配内存 也不抢别人已有的页 只是"修剪时别动游戏的份"
    //
    // 为什么必须回读核实
    //   SetProcessWorkingSetSizeEx 返回成功不代表配额真的落地
    //   写完读回 最小值和硬下限标志都吻合才算数 不吻合立刻还原并熔断
    //
    // 边界 全部直接跳过 不猜
    //   句柄被反作弊拒绝 游戏已有硬下限 别人设置的 不抢所有权
    //   工作集太小不值得 内存并不吃紧时不动作
    //
    // 配额随进程退出自然消失 Pavise 崩溃后的残留以游戏生命周期为界
    //   快照记录原始配额 下次启动若游戏仍在且身份吻合则补撤
    internal static class MemShield
    {
        internal const string EnabledKey = "GmMemShield";
        private const string SnapKey = "MemShieldSnap";
        private const string FuseKey = "MemShieldFuse";

        // 判据 都取保守值 宁可不做
        internal const double EngagePressureShare = 0.85; // 已用物理 ≥ 85% 才算吃紧
        internal const int EngageSamples = 3;             // 连续三次采样都吃紧才动手
        internal const double PinFactor = 0.80;           // 只锁当前工作集的八成
        internal const double MaxPinShareOfTotal = 1.0 / 3; // 下限封顶在物理内存三分之一
        internal const ulong MinPinBytes = 128UL * 1024 * 1024; // 太小不值得动
        internal const int SampleIntervalSeconds = 10;
        // 小内存机器不参与 触发条件(压力 85%+)恰是系统最需要自由修剪的时刻
        //   在 8GB 这类机器上锁死几个 GB 会把修剪压力全部转嫁给 DWM 和外壳
        //   它们被换出的表现就是掉帧和输入卡顿 正是本功能要治的病 得不偿失
        internal const ulong MinTotalPhysBytes = 16UL * 1024 * 1024 * 1024;

        internal const uint HardMinEnable = 0x1;
        internal const uint HardMaxDisable = 0x8;

        // 两把锁分工与显存驻留一致 opLk 串行化动作 lk 只保护字段 短临界区
        private static readonly object opLk = new object();
        private static readonly object lk = new object();
        private static ShieldStage stage;
        private static long nextSampleTicks;
        private static int pressureRun;
        private static int shieldPid;
        private static long shieldCreation;
        private static ulong pinnedMin;
        private static ulong origMin, origMax;
        private static uint origFlags;
        private static ulong lastTotal;
        private static bool recoveryBlocked;
        private static int lastSkipPid;
        private static long lastSkipCreation;

        public static bool Fused { get { return Settings.Load(FuseKey, false); } }

        public static void ClearFuse()
        {
            if (Fused) Settings.Save(FuseKey, false);
            lock (lk) if (stage == ShieldStage.Fused) stage = ShieldStage.Idle;
        }

        public static ShieldStage Stage { get { lock (lk) return stage; } }

        public static string Summarize()
        {
            lock (lk)
            {
                if (stage != ShieldStage.Engaged || lastTotal == 0) return null;
                return Gb(pinnedMin) + " GB / " + Gb(lastTotal) + " GB";
            }
        }

        internal static string Gb(ulong bytes)
        {
            return (bytes / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture);
        }

        // 换局不经过 Deactivate 必须自己清尾 上一局的配额不能挂到下一局
        public static bool Begin()
        {
            lock (opLk)
            {
                if (!DoRelease(Lang.T("t.memshield.4"))) return false;
                lock (lk) nextSampleTicks = 0;
                return true;
            }
        }

        public static void SampleIfDue(bool want, int rendererPid, long rendererCreation)
        {
            lock (lk) if (recoveryBlocked) return;
            if (!want || rendererPid <= 0) { ReleaseIfAny(Lang.T("t.memshield.3")); return; }
            bool mismatch;
            lock (lk)
            {
                mismatch = shieldPid != 0
                    && (rendererPid != shieldPid || rendererCreation != shieldCreation);
                if (stage == ShieldStage.Fused) return;
                // 跳过只对当初那个进程粘滞 同局换手(启动器→真渲染进程)要重新评估
                //   pid 会被系统回收复用 代数一起比对 复用的新进程不吃旧进程的跳过
                if (stage == ShieldStage.Skipped && !mismatch
                    && rendererPid == lastSkipPid && rendererCreation == lastSkipCreation) return;
                long now = DateTime.UtcNow.Ticks;
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + SampleIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            lock (opLk)
            {
                try
                {
                    lock (lk) if (recoveryBlocked) return;
                    if (mismatch && !DoRelease(Lang.T("t.memshield.1"))) return;
                    Step(rendererPid, rendererCreation);
                }
                catch { }
            }
        }

        // 必须在 opLk 内调用
        private static void Step(int pid, long creation)
        {
            lock (lk) if (recoveryBlocked) return;
#if PAVISE_SELFTEST
            if (SampleStepForTest != null) { SampleStepForTest(pid, creation); return; }
#endif
            if (Fused) { lock (lk) stage = ShieldStage.Fused; return; }

            ulong total, avail;
            if (!TryMemoryStatus(out total, out avail) || total == 0) return;
            lock (lk) lastTotal = total;

            bool engaged;
            ulong pinned;
            lock (lk) { engaged = stage == ShieldStage.Engaged; pinned = pinnedMin; }

            // 小内存门槛只挡新的挂载 已挂载的监控不许被它覆写成 Skipped
            //   虚拟机热调内存这类场景里上报的总量会中途变小
            if (!engaged && total < MinTotalPhysBytes)
            {
                SkipOnce(pid, creation, Lang.T("log.memshield.8"));
                return;
            }

            bool gone;
            long liveCreation;
            ulong ws, min, max;
            uint flags;
            if (!TryQueryTarget(pid, out gone, out liveCreation, out ws, out min, out max, out flags))
            {
                if (gone) return;
                // 已挂载时的瞬时查询失败只跳过本轮 不许把 Engaged 覆写成 Skipped
                //   否则外力篡改的监控从此静默死亡 配额还锁着 界面却显示没有护盾
                if (engaged) return;
                SkipOnce(pid, creation, Lang.T("log.memshield.2"));
                return;
            }
            if (liveCreation != creation) return;

            if (engaged)
            {
                // 已经挂上 只做存活核实 配额被外力改掉就熔断 不反复重写
                if ((flags & HardMinEnable) == 0 || min != pinned)
                {
                    Settings.Save(FuseKey, true);
                    DoRelease(Lang.T("t.memshield.2"));
                    lock (lk) stage = ShieldStage.Fused;
                    Logger.Log(Lang.T("log.memshield.5"));
                }
                return;
            }

            if ((flags & HardMinEnable) != 0)
            {
                // 已有硬下限是别人的设置 不抢所有权
                SkipOnce(pid, creation, Lang.T("log.memshield.1"));
                return;
            }

            double pressure = (double)(total - avail) / total;
            if (pressure < EngagePressureShare)
            {
                lock (lk) pressureRun = 0;
                return;
            }
            int run;
            lock (lk) { pressureRun++; run = pressureRun; stage = ShieldStage.Observing; }
            if (run < EngageSamples) return;

            // 内核按页存配额 回读按页返 不对齐的字节值写下去再读回来必然不等
            //   会把自己的正确写入误判成外力篡改而自熔断 落账 写入 比对全用页对齐值
            ulong target = (ulong)(ws * PinFactor) & ~4095UL;
            ulong cap = (ulong)(total * MaxPinShareOfTotal) & ~4095UL;
            if (target > cap) target = cap;
            if (target < MinPinBytes || target <= min)
            {
                SkipOnce(pid, creation, Lang.T("log.memshield.7"));
                return;
            }

            // 与显存驻留同一套恢复纪律 先落非覆盖式恢复记录 再动系统
            string snapshot = EncodeSnapshot(pid, creation, min, max, flags);
            if (Settings.LoadStr(SnapKey, "").Length != 0)
            {
                lock (lk) recoveryBlocked = true;
                return;
            }
            if (!Settings.SaveStr(SnapKey, snapshot) || Settings.LoadStr(SnapKey, "") != snapshot) return;

            ulong newMax = max > target ? max : target;
            TrySetTarget(pid, creation, target, newMax, HardMinEnable | HardMaxDisable);

            // 返回码不作数 回读吻合才作数
            ulong minAfter, maxAfter, wsAfter;
            uint flagsAfter;
            bool goneAfter;
            long creationAfter;
            bool readBack = TryQueryTarget(pid, out goneAfter, out creationAfter, out wsAfter,
                out minAfter, out maxAfter, out flagsAfter);
            // 游戏恰在写入与回读之间退出或换代 配额随进程消亡 这是良性时序不熔断
            //   但快照必须当场清账 裸退会把自家快照留成下一个渲染进程眼里的"外账"
            //   同局换手的新进程一采样就会撞上它 recoveryBlocked 把护盾冻到局末
            if (goneAfter || (readBack && creationAfter != creation))
            {
                DoRelease(Lang.T("t.memshield.3"));
                return;
            }
            if (!readBack || (flagsAfter & HardMinEnable) == 0 || minAfter != target)
            {
                Settings.Save(FuseKey, true);
                DoRelease(Lang.T("t.memshield.2"));
                lock (lk) stage = ShieldStage.Fused;
                Logger.Log(Lang.T("log.memshield.4"));
                return;
            }

            lock (lk)
            {
                stage = ShieldStage.Engaged;
                shieldPid = pid;
                shieldCreation = creation;
                pinnedMin = target;
                origMin = min; origMax = max; origFlags = flags;
            }
            Logger.Log(Lang.F("log.memshield.3", Gb(target), Gb(ws), Gb(avail),
                ((int)(pressure * 100)).ToString(CultureInfo.InvariantCulture)));
        }

        private static void SkipOnce(int pid, long creation, string why)
        {
            bool first;
            lock (lk)
            {
                first = stage != ShieldStage.Skipped;
                stage = ShieldStage.Skipped;
                lastSkipPid = pid;
                lastSkipCreation = creation;
            }
            if (first && !string.IsNullOrEmpty(why)) Logger.Log(why);
        }

        public static bool Release()
        {
            lock (opLk) return DoRelease(Lang.T("t.memshield.3"));
        }

        // 无事可做时一个字节都不碰 对局中每秒被调一次
        private static bool ReleaseIfAny(string reason)
        {
            lock (lk)
                if (shieldPid == 0 && !recoveryBlocked
                    && (stage == ShieldStage.Idle || stage == ShieldStage.Fused))
                    return true;
            lock (opLk) return DoRelease(reason);
        }

        // 必须在 opLk 内调用
        private static bool DoRelease(string reason)
        {
            int pid;
            long creation;
            ulong rMin, rMax;
            uint rFlags;
            bool had;
            lock (lk)
            {
                pid = shieldPid; creation = shieldCreation;
                rMin = origMin; rMax = origMax; rFlags = origFlags;
                had = stage == ShieldStage.Engaged;
            }
            string snapshot = Settings.LoadStr(SnapKey, "");
            if (snapshot.Length != 0)
            {
                int recordedPid;
                long recordedCreation;
                ulong recordedMin, recordedMax;
                uint recordedFlags;
                if (!DecodeSnapshot(snapshot, out recordedPid, out recordedCreation,
                        out recordedMin, out recordedMax, out recordedFlags))
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                if (had && (pid != recordedPid || creation != recordedCreation))
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                pid = recordedPid; creation = recordedCreation;
                rMin = recordedMin; rMax = recordedMax; rFlags = recordedFlags;
            }
            if (snapshot.Length != 0 || had)
            {
                bool restored;
                try { restored = RestoreQuota(pid, creation, rMin, rMax, rFlags); }
                catch { restored = false; }
                if (!restored)
                {
                    lock (lk) recoveryBlocked = true;
                    if (snapshot.Length == 0 && pid > 0 && creation > 0)
                        Settings.SaveStr(SnapKey, EncodeSnapshot(pid, creation, rMin, rMax, rFlags));
                    return false;
                }
            }
            if (Settings.LoadStr(SnapKey, "") != snapshot
                || snapshot.Length != 0 && (!Settings.SaveStr(SnapKey, "") || Settings.LoadStr(SnapKey, "").Length != 0))
            {
                lock (lk) recoveryBlocked = true;
                return false;
            }
            lock (lk)
            {
                shieldPid = 0; shieldCreation = 0; pinnedMin = 0;
                origMin = origMax = 0; origFlags = 0;
                pressureRun = 0; lastTotal = 0;
                recoveryBlocked = false;
                if (stage != ShieldStage.Fused) stage = ShieldStage.Idle;
            }
            if (had) Logger.Log(Lang.T("log.memshield.6") + reason);
            return true;
        }

        // Pavise 异常退出时配额还挂在游戏进程上 下次启动补撤
        //   配额随进程退出自然消失 游戏没了记录直接清
        public static bool HealFromCrash()
        {
            lock (opLk) return DoRelease(Lang.T("t.memshield.3"));
        }

        public static bool HasResidue()
        {
            lock (lk) if (recoveryBlocked || stage == ShieldStage.Engaged) return true;
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        private static string EncodeSnapshot(int pid, long creation, ulong min, ulong max, uint flags)
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

        // 三个原语 隔离测试必须注入 生产走原生调用
        private static bool TryMemoryStatus(out ulong total, out ulong avail)
        {
            total = 0; avail = 0;
#if PAVISE_SELFTEST
            if (MemoryStatusForTest != null) return MemoryStatusForTest(out total, out avail);
            throw new InvalidOperationException("MemShield memory status requires an injected test double.");
#else
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (!GlobalMemoryStatusEx(ref status)) return false;
                total = status.TotalPhys; avail = status.AvailPhys;
                return true;
            }
            catch { return false; }
#endif
        }

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
        internal delegate bool MemoryStatusOverride(out ulong total, out ulong avail);
        internal delegate bool TargetQueryOverride(int pid, out bool gone, out long creation,
            out ulong workingSet, out ulong min, out ulong max, out uint flags);
        internal delegate void TargetSetOverride(int pid, long creation, ulong min, ulong max, uint flags);
        internal static MemoryStatusOverride MemoryStatusForTest;
        internal static TargetQueryOverride QueryForTest;
        internal static TargetSetOverride SetForTest;
        internal static Func<int, long, ulong, ulong, uint, bool> RestoreForTest;
        internal static Action<int, long> SampleStepForTest;
        internal static bool RecoveryBlockedForTest { get { lock (lk) return recoveryBlocked; } }

        internal static void StepForTest(int pid, long creation)
        {
            lock (opLk) Step(pid, creation);
        }

        internal static void ResetForTest()
        {
            lock (opLk)
            lock (lk)
            {
                stage = ShieldStage.Idle;
                nextSampleTicks = 0;
                pressureRun = shieldPid = 0;
                shieldCreation = 0;
                pinnedMin = origMin = origMax = lastTotal = 0;
                origFlags = 0;
                recoveryBlocked = false;
                lastSkipPid = 0;
                lastSkipCreation = 0;
                MemoryStatusForTest = null;
                QueryForTest = null;
                SetForTest = null;
                RestoreForTest = null;
                SampleStepForTest = null;
            }
        }
#endif

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

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
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
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
