// @author bdth 2074055628@qq.com
// 文件用途 显存驻留 实验功能 显存吃紧时给游戏声明一份最低显存预留 退局撤销
using System;
using System.Globalization;

namespace PaviseApp
{
    internal enum ShieldStage { Idle = 0, Observing = 1, Engaged = 2, Skipped = 3, Fused = 4 }

    // 显存驻留 面向显存预算吃紧时的长帧
    //   读 D3DKMTQueryVideoMemoryInfo 确认游戏持续贴着预算跑 再用
    //   D3DKMTChangeVideoMemoryReservation 声明一份保守的最低物理显存需求
    //   这不是增加显存 也不是把显存锁给游戏 Reservation 只是显存管理器判断
    //   进程最低工作集的提示 目标是减少纹理被挤出显存再调回造成的长帧
    //
    // 为什么必须回读核实
    //   ChangeVideoMemoryReservation 的正常用法是进程给自己声明
    //   跨进程给别人声明 API 形状上支持 但显存管理器采不采纳没有公开说明
    //   所以写完一定要回读 CurrentReservation 非零才算数 返回码成功不算数
    //   回读不到就说明这台机器上这条路不通 直接熔断 不再浪费对局
    //
    // 边界 全部直接跳过 不猜
    //   句柄被反作弊拒绝 渲染显卡无法唯一确认 游戏自己已有非零预留
    //   核显统一内存 预算读不到 可预留额度为零
    internal static class VramShield
    {
        internal const string EnabledKey = "GmVramShield";
        private const string SnapKey = "VramShieldSnap";
        private const string FuseKey = "VramShieldFuse";

        // 判据 都取保守值 宁可不做
        internal const double EngageUsageShare = 0.90;   // 占用贴到预算九成才算吃紧
        internal const int EngageSamples = 3;            // 连续三次采样都吃紧才动手
        internal const double ReserveFactor = 0.80;      // 只声明当前占用的八成 不是全要
        internal const int SampleIntervalSeconds = 10;
        internal const int AdapterResolveMs = 400;

        // 查询要 QUERY_INFORMATION 写入要 SET_INFORMATION 本机实测 QUERY_LIMITED 两样都被拒
        internal const int ShieldAccess =
            Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_INFORMATION;

        // 两把锁分工明确 不许颠倒
        //   opLk 串行化"动作"——采样 写入 撤销 三者互斥 因为它们共用适配器句柄
        //     没有它 UI 线程关开关时的撤销会和工作线程正在跑的采样打架
        //     撤销关掉句柄 而采样手里还攥着同一个句柄的副本 就是 use-after-close
        //   lk 只保护字段读写 一律短临界区 里面不许做文件或注册表 IO
        //     Stage 和 Summarize 会被 UI 线程读 持锁做 IO 会把界面拖住
        private static readonly object opLk = new object();
        private static readonly object lk = new object();
        private static Action mutationBegin;
        private static Action mutationEnd;
        private static ShieldStage stage;
        private static long nextSampleTicks;
        private static int pressureRun;
        private static int shieldPid;
        private static long shieldCreation;
        private static uint shieldAdapter;
        private static uint shieldPhys;
        private static ulong shieldBytes;
        private static ulong lastBudget;
        private static bool recoveryBlocked;

        public static bool Fused { get { return Settings.Load(FuseKey, false); } }

        public static void ConfigureMutationBoundary(Action begin, Action end)
        {
            lock (lk)
            {
                mutationBegin = begin;
                mutationEnd = end;
            }
        }

        private static void BeginMutation()
        {
            Action callback;
            lock (lk) callback = mutationBegin;
            if (callback != null) try { callback(); } catch { }
        }

        private static void EndMutation()
        {
            Action callback;
            lock (lk) callback = mutationEnd;
            if (callback != null) try { callback(); } catch { }
        }

        // 熔断同时记在注册表和 stage 上 只清注册表的话 stage 里那个 Fused 出不来
        //   DoRelease 特意不动 Fused 免得换局把本机级结论冲掉 所以只能在这里清
        //   不清就等于日志里那句"关掉开关再打开可重试"是假的 得重启进程才复活
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
                if (stage != ShieldStage.Engaged || lastBudget == 0) return null;
                return VidMmProbe.Gb(shieldBytes) + " GB / "
                    + VidMmProbe.Gb(lastBudget) + " GB";
            }
        }

        // 换局走的是 ReportFinish + ReportBegin 不经过 Deactivate 所以这里必须自己撤干净
        //   只重置 stage 是不够的 上一局的 shieldPid 和适配器句柄会留下来
        //   留下来的后果有两个 旧预留一直挂在上一个游戏进程上撤不掉
        //   以及下一局看到 shieldAdapter 非零直接复用 双显卡机器上会去查错的那块卡
        public static bool Begin()
        {
            lock (opLk)
            {
                if (!DoRelease(Lang.T("t.vramshield.4"))) return false;
                lock (lk) nextSampleTicks = 0;
                return true;
            }
        }

        public static void SampleIfDue(bool want, int rendererPid, long rendererCreation)
        {
            // Do not overwrite the only original when a previous restoration was
            // denied or could not be verified. Explicit release/begin can retry.
            lock (lk) if (recoveryBlocked) return;
            if (!want || rendererPid <= 0) { ReleaseIfAny(Lang.T("t.vramshield.3")); return; }
            bool mismatch;
            lock (lk)
            {
                // shieldPid 记的是当前适配器句柄和观察计数属于哪个进程 解析出句柄时就写
                //   不能只在 engage 成功后才认 换渲染进程不一定发生在已挂上的时候
                //   同一个游戏内部换渲染进程时不会走 Begin 若这里漏判
                //   旧句柄会被新进程接着用 双显卡机器上就是查错那块卡
                mismatch = shieldPid != 0
                    && (rendererPid != shieldPid || rendererCreation != shieldCreation);
                if (stage == ShieldStage.Fused) return;
                // 跳过是"这个进程本局跳过" 换了进程就该重新判一次
                if (stage == ShieldStage.Skipped && !mismatch) return;
                long now = DateTime.UtcNow.Ticks;
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + SampleIntervalSeconds * TimeSpan.TicksPerSecond;
            }
            lock (opLk)
            {
                try
                {
                    lock (lk) if (recoveryBlocked) return;
                    // 渲染进程换了 先把旧的撤掉再重新观察
                    if (mismatch && !DoRelease(Lang.T("t.vramshield.1"))) return;
                    Step(rendererPid, rendererCreation);
                }
                catch { }
            }
        }

        private static void Step(int pid, long creation)
        {
            lock (lk) if (recoveryBlocked) return;
#if PAVISE_SELFTEST
            if (SampleStepForTest != null) { SampleStepForTest(pid, creation); return; }
#endif
            if (Fused) { lock (lk) stage = ShieldStage.Fused; return; }
            if (GpuInventory.IntegratedOnly)
            {
                SkipOnce(Lang.T("log.vramshield.1"));
                return;
            }

            IntPtr h = Native.OpenProcess(ShieldAccess, false, pid);
            if (h == IntPtr.Zero)
            {
                SkipOnce(Lang.T("log.vramshield.2"));
                return;
            }
            try
            {
                uint adapter;
                uint phys;
                bool engaged;
                lock (lk) { adapter = shieldAdapter; phys = shieldPhys; engaged = stage == ShieldStage.Engaged; }

                if (adapter == 0)
                {
                    RenderAdapter ra = GpuEvidence.ResolveRenderAdapter(pid, AdapterResolveMs);
                    if (ra == null)
                    {
                        // 这一轮没采到 3D 占用 不是错误 下一轮再看
                        return;
                    }
                    if (ra.Ambiguous)
                    {
                        SkipOnce(Lang.T("log.vramshield.3"));
                        return;
                    }
                    if (!VidMmProbe.TryOpenAdapter(ra.LuidHigh, ra.LuidLow, out adapter))
                    {
                        SkipOnce(Lang.T("log.vramshield.4"));
                        return;
                    }
                    phys = ra.PhysIndex;
                    lock (lk)
                    {
                        shieldAdapter = adapter; shieldPhys = phys;
                        shieldPid = pid; shieldCreation = creation;
                    }
                }

                VramStatus st = VidMmProbe.Query(h, adapter, phys);
                if (!st.Ok || st.Budget == 0)
                {
                    // 驱动重置 TDR 之后适配器句柄失效 这不是"本机不采纳" 绝不能熔断
                    //   已经挂上时必须把状态一并归零 只丢句柄是不够的
                    //   否则下一轮重开句柄后仍是 Engaged 而 TDR 已经把预留清了
                    //   就会把一次瞬时驱动重置当成"预留被外力清除"去熔断
                    if (engaged) DoRelease(Lang.T("t.vramshield.5"));
                    else
                    {
                        lock (lk) { shieldAdapter = 0; shieldPhys = 0; }
                        VidMmProbe.CloseAdapter(adapter);
                    }
                    return;
                }
                lock (lk) lastBudget = st.Budget;

                if (engaged)
                {
                    // 已经挂上 只做存活核实 预留被外力清掉就熔断 不反复重写
                    if (st.CurrentReservation == 0)
                    {
                        Settings.Save(FuseKey, true);
                        DoRelease(Lang.T("t.vramshield.2"));
                        lock (lk) stage = ShieldStage.Fused;
                        Logger.Log(Lang.T("log.vramshield.5"));
                    }
                    return;
                }

                if (st.CurrentReservation > 0)
                {
                    SkipOnce(Lang.F("log.vramshield.6", VidMmProbe.Gb(st.CurrentReservation)));
                    return;
                }
                if (st.AvailableForReservation == 0)
                {
                    SkipOnce(Lang.T("log.vramshield.7"));
                    return;
                }

                if (st.UsageShare < EngageUsageShare)
                {
                    lock (lk) pressureRun = 0;
                    return;
                }
                int run;
                lock (lk) { pressureRun++; run = pressureRun; stage = ShieldStage.Observing; }
                if (run < EngageSamples) return;

                ulong target = (ulong)(st.CurrentUsage * ReserveFactor);
                if (target > st.AvailableForReservation) target = st.AvailableForReservation;
                if (target == 0)
                {
                    SkipOnce(Lang.T("log.vramshield.7"));
                    return;
                }

                VramStatus after;
                BeginMutation();
                try
                {
                    // Reservation writes require a confirmed, non-overwriting
                    // recovery record, just like the process suppression journal.
                    string snapshot = pid.ToString(CultureInfo.InvariantCulture)
                        + ":" + creation.ToString(CultureInfo.InvariantCulture);
                    if (Settings.LoadStr(SnapKey, "").Length != 0)
                    {
                        lock (lk) recoveryBlocked = true;
                        return;
                    }
                    if (!Settings.SaveStr(SnapKey, snapshot) || Settings.LoadStr(SnapKey, "") != snapshot) return;
                    VidMmProbe.SetReservation(h, adapter, phys, target);

                    // 返回码不作数 回读 CurrentReservation 才作数
                    after = VidMmProbe.Query(h, adapter, phys);
                    if (!after.Ok || after.CurrentReservation == 0)
                    {
                        Settings.Save(FuseKey, true);
                        // 此刻仍是 Observing，但写入可能已经生效。
                        // DoRelease 依据快照撤销；未确认还原时保留记录。
                        DoRelease(Lang.T("t.vramshield.2"));
                        lock (lk) stage = ShieldStage.Fused;
                        Logger.Log(Lang.T("log.vramshield.8"));
                        return;
                    }
                }
                finally { EndMutation(); }

                lock (lk)
                {
                    stage = ShieldStage.Engaged;
                    shieldPid = pid;
                    shieldCreation = creation;
                    shieldBytes = after.CurrentReservation;
                }
                Logger.Log(Lang.F("log.vramshield.9",
                    VidMmProbe.Gb(after.CurrentReservation),
                    VidMmProbe.Gb(st.CurrentUsage),
                    VidMmProbe.Gb(st.Budget),
                    ((int)(st.UsageShare * 100)).ToString(CultureInfo.InvariantCulture)));
            }
            finally { Native.CloseHandle(h); }
        }

        // 跳过之后本局不会再进 Step 适配器句柄留着没有意义 顺手关掉
        private static void SkipOnce(string why)
        {
            bool first;
            uint adapter;
            lock (lk)
            {
                first = stage != ShieldStage.Skipped;
                stage = ShieldStage.Skipped;
                adapter = shieldAdapter;
                shieldAdapter = 0; shieldPhys = 0;
            }
            if (adapter != 0) VidMmProbe.CloseAdapter(adapter);
            if (first && !string.IsNullOrEmpty(why)) Logger.Log(why);
        }

        public static bool Release()
        {
            lock (opLk) return DoRelease(Lang.T("t.vramshield.3"));
        }

        // 无事可做时一个字节都不碰 这个函数在对局中每秒被调一次
        //   功能默认关闭 若不先快速判空 就是每秒白读一次注册表
        private static bool ReleaseIfAny(string reason)
        {
            lock (lk)
                if (shieldAdapter == 0 && shieldPid == 0
                    && !recoveryBlocked && (stage == ShieldStage.Idle || stage == ShieldStage.Fused))
                    return true;
            lock (opLk) return DoRelease(reason);
        }

        // 必须在 opLk 内调用 IO 一律放在 lk 之外
        private static bool DoRelease(string reason)
        {
            int pid;
            long creation;
            uint adapter, phys;
            bool had;
            lock (lk)
            {
                pid = shieldPid; creation = shieldCreation;
                adapter = shieldAdapter; phys = shieldPhys;
                had = stage == ShieldStage.Engaged;
            }
            string snapshot = Settings.LoadStr(SnapKey, "");
            if (snapshot.Length != 0)
            {
                int recordedPid;
                long recordedCreation;
                if (!ParseSnapshot(snapshot, out recordedPid, out recordedCreation))
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                if (had && (pid != recordedPid || creation != recordedCreation))
                {
                    lock (lk) recoveryBlocked = true;
                    return false;
                }
                if (pid != recordedPid || creation != recordedCreation) { adapter = 0; phys = 0; }
                pid = recordedPid; creation = recordedCreation;
            }
            if (snapshot.Length != 0 || had)
            {
                bool restored;
                BeginMutation();
                try { restored = RestoreReservation(pid, creation, adapter, phys); }
                catch { restored = false; }
                finally { EndMutation(); }
                if (!restored)
                {
                    lock (lk) recoveryBlocked = true;
                    // Normally the record already exists. Retain an in-memory
                    // original too if an external writer removed it unexpectedly.
                    if (snapshot.Length == 0 && pid > 0 && creation > 0)
                        Settings.SaveStr(SnapKey, pid.ToString(CultureInfo.InvariantCulture)
                            + ":" + creation.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
            }
            // A changed or unwritable snapshot is not permission to clear a new
            // recovery target. Leave it visible to the final reset verification.
            if (Settings.LoadStr(SnapKey, "") != snapshot
                || snapshot.Length != 0 && (!Settings.SaveStr(SnapKey, "") || Settings.LoadStr(SnapKey, "").Length != 0))
            {
                lock (lk) recoveryBlocked = true;
                return false;
            }
            uint closeAdapter;
            lock (lk)
            {
                closeAdapter = shieldAdapter;
                shieldPid = 0; shieldCreation = 0; shieldBytes = 0;
                shieldAdapter = 0; shieldPhys = 0;
                pressureRun = 0; lastBudget = 0;
                recoveryBlocked = false;
                if (stage != ShieldStage.Fused) stage = ShieldStage.Idle;
            }
            if (closeAdapter != 0) VidMmProbe.CloseAdapter(closeAdapter);
            if (had) Logger.Log(Lang.T("log.vramshield.10") + reason);
            return true;
        }

        // Pavise 异常退出时预留还挂在游戏进程上 下次启动补撤
        //   预留随进程退出自然消失 所以只有游戏仍在跑且身份吻合才需要动手
        public static bool HealFromCrash()
        {
            lock (opLk) return DoRelease(Lang.T("t.vramshield.3"));
        }

        public static bool HasResidue()
        {
            lock (lk) if (recoveryBlocked || stage == ShieldStage.Engaged) return true;
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        private static bool ParseSnapshot(string snap, out int pid, out long creation)
        {
            pid = 0;
            creation = 0;
            string[] parts = snap.Split(':');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out creation)
                && pid > 0 && creation > 0;
        }

        private static bool RestoreReservation(int pid, long creation, uint adapter, uint phys)
        {
#if PAVISE_SELFTEST
            if (RestoreReservationForTest != null) return RestoreReservationForTest(pid, creation, adapter, phys);
#endif
            if (pid <= 0 || creation <= 0) return false;
            IntPtr h = Native.OpenProcess(ShieldAccess, false, pid);
            if (h == IntPtr.Zero)
            {
                if (Native.LastOpenProcessFailureWasNoSuchProcess()) return true;
                IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (query == IntPtr.Zero) return Native.LastOpenProcessFailureWasNoSuchProcess();
                try
                {
                    long currentCreation, cpu; ulong io;
                    return Native.QueryProcessSample(query, out currentCreation, out cpu, out io)
                        && currentCreation != creation;
                }
                finally { Native.CloseHandle(query); }
            }
            try
            {
                long cr, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out cr, out cpu, out io)) return false;
                if (cr != creation) return true;
                // The legacy snapshot stores no adapter identity. After a crash,
                // today's busiest GPU does not prove which adapter was reserved.
                // Preserve the record until that process exits instead of guessing.
                if (adapter == 0) return false;
                VramStatus before = VidMmProbe.Query(h, adapter, phys);
                if (before == null || !before.Ok) return false;
                if (before.CurrentReservation == 0) return true;
                if (!VidMmProbe.SetReservation(h, adapter, phys, 0)) return false;
                VramStatus after = VidMmProbe.Query(h, adapter, phys);
                return after != null && after.Ok && after.CurrentReservation == 0;
            }
            catch { return false; }
            finally { Native.CloseHandle(h); }
        }

#if PAVISE_SELFTEST
        internal static Func<int, long, uint, uint, bool> RestoreReservationForTest;
        internal static Action<int, long> SampleStepForTest;
        internal static bool RecoveryBlockedForTest { get { lock (lk) return recoveryBlocked; } }

        internal static void ResetRecoveryForTest()
        {
            lock (opLk)
            lock (lk)
            {
                if (shieldAdapter != 0) throw new InvalidOperationException("Cannot discard a live native VRAM adapter in an isolated test");
                stage = ShieldStage.Idle;
                nextSampleTicks = 0;
                pressureRun = shieldPid = 0;
                shieldCreation = 0;
                shieldPhys = 0;
                shieldBytes = lastBudget = 0;
                recoveryBlocked = false;
                RestoreReservationForTest = null;
                SampleStepForTest = null;
            }
        }
#endif
    }
}
