#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestCoreCardHeadTag()
        {
            Eq(CoreMatrix.HeadTag.Primary, CoreMatrix.HeadTagFor(false, true, false, false));
            Eq(CoreMatrix.HeadTag.Primary, CoreMatrix.HeadTagFor(false, true, false, true));
            foreach (bool multi in new[] { false, true })
                Eq(CoreMatrix.HeadTag.Exclusive, CoreMatrix.HeadTagFor(true, true, true, multi));
            Eq(CoreMatrix.HeadTag.Cache, CoreMatrix.HeadTagFor(false, false, true, true));
            Eq(CoreMatrix.HeadTag.Smt, CoreMatrix.HeadTagFor(false, false, false, true));
            Eq(CoreMatrix.HeadTag.None, CoreMatrix.HeadTagFor(false, false, false, false));

            Eq(true, CoreMatrix.HeadTagFits(46, 20, 23, 3));
            Eq(false, CoreMatrix.HeadTagFits(46, 20, 24, 3));
            Eq(true, CoreMatrix.HeadTagFits(92, 20, 23, 3));
            Eq(false, CoreMatrix.HeadTagFits(92, 20, 0, 3));
        }

        private static void TestAllMaskReconcile()
        {
            bool bad;
            Eq(0xFFFUL, CpuTopology.ReconcileAllMask(0xFFF, 0xFFF, out bad));
            Eq(false, bad);
            Eq(0xFFFUL, CpuTopology.ReconcileAllMask(0xFFF, 0, out bad));
            Eq(false, bad);
            Eq(0xFFFFUL, CpuTopology.ReconcileAllMask(0xFFFFFF, 0xFFFF, out bad));
            Eq(true, bad);
            Eq(0xFFFFUL, CpuTopology.ReconcileAllMask(0xFFFF, 0xFFFFFF, out bad));
            Eq(true, bad);
            Eq(0x0FUL, CpuTopology.ReconcileAllMask(0x0F, 0xF0, out bad));
            Eq(true, bad);
            Console.WriteLine("  本机 AllMask 对账 " + (CpuTopology.AllMaskReconciled ? "不一致 已收窄" : "一致"));
        }

        private static void TestTopologySourcesAgree()
        {
            Console.WriteLine("  本机拓扑 逻辑核 " + CpuTopology.CountSetBits(CpuTopology.AllMask)
                + " 物理核 GLPIE/CpuSet " + CpuTopology.TopologySourceCounts
                + " 混合架构 " + (CpuTopology.Hybrid ? "是" : "否"));
            Eq(true, CpuTopology.TopologySourcesAgree);
        }

        private static void FavoredCoresOnlyWithinTopEfficiencyClass()
        {
            Eq(0UL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Eq(0x30UL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Eq(0x0CUL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 1, 1, 3, 3 }));
            Eq(0UL, CpuTopology.FavoredMaskOf(new int[0], new byte[0], new byte[0]));
            Eq(0UL, CpuTopology.FavoredMaskOf(new[] { 0, 1 }, new byte[] { 0 }, new byte[] { 1, 2 }));
            Eq("0-7:1/1 8-11:1/2 12-15:0/0", CpuTopology.DescribeCoreClasses(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0 }));
            Eq("0:0/1 2-3:0/1", CpuTopology.DescribeCoreClasses(
                new[] { 3, 0, 2 }, new byte[] { 0, 0, 0 }, new byte[] { 1, 1, 1 }));
            Eq("", CpuTopology.DescribeCoreClasses(new int[0], new byte[0], new byte[0]));
            Console.WriteLine("PASS FavoredCores: ranked only within the top efficiency class");
        }

        internal static void RunCoreSchedulingTests(string output)
        {
            FavoredCoresOnlyWithinTopEfficiencyClass();
            AutoCorePlanFromTopology();
            AutoCoreEngineRange();
            AutoCoreThreadLoad();
            AutoCoreThreadParse();
            AutoCoreGovernorSteps();
            AutoCoreSessionPlan(output);
            AutoCoreRetiredKeys(output);
            AutoCoreYieldLedger();
            LegacyReservedCoreCleanup();
        }

        private static ulong[] AutoHybridCores(int perf, int eff, out ulong all, out ulong perfMask)
        {
            var cores = new List<ulong>();
            all = 0; perfMask = 0;
            int lp = 0;
            for (int i = 0; i < perf; i++) { ulong core = 3UL << lp; cores.Add(core); perfMask |= core; lp += 2; }
            for (int i = 0; i < eff; i++) { cores.Add(1UL << lp); lp++; }
            foreach (ulong core in cores) all |= core;
            return cores.ToArray();
        }

        private static ulong[] AutoSmtCores(int count, out ulong all)
        {
            var cores = new ulong[count];
            all = 0;
            for (int i = 0; i < count; i++) { cores[i] = 3UL << (i * 2); all |= cores[i]; }
            return cores;
        }

        private static void AutoCorePlanFromTopology()
        {
            ulong all, perf;
            ulong[] raptor = AutoHybridCores(8, 16, out all, out perf);
            AutoCorePlan p = AutoCores.Plan(all, raptor, true, perf, false, 0, null, true, false, 0);
            Eq(true, p.Available); Eq("hybrid", p.Kind); Eq(0xFFFFUL, p.Region);
            Eq(0xFFFCUL, p.Exclusive); Eq(7, p.Cores); Eq(null, p.Blocked);

            p = AutoCores.Plan(all, raptor, true, perf, false, 0, null, true, false, 0x3C);
            Eq(0xFFC0UL, p.Exclusive); Eq(5, p.Cores); Eq(0x3CUL, p.Allocated);
            p = AutoCores.Plan(all, raptor, true, perf, false, 0, null, true, false, 0xFFFF0000UL);
            Eq(0x3FFCUL, p.Exclusive); Eq(6, p.Cores);
            p = AutoCores.Plan(all, raptor, true, perf, false, 0, null, true, false, 0xFFFCUL);
            Eq("schedule.auto.fewcores", p.Blocked); Eq(0UL, p.Exclusive); Eq(false, p.Available);

            p = AutoCores.Plan(all, raptor, true, perf, false, 0, null, true, true, 0);
            Eq("schedule.auto.handheld", p.Blocked); Eq(0UL, p.Exclusive); Eq(0, p.Cores);
            p = AutoCores.Plan(all, raptor, true, perf, false, 0, null, false, false, 0);
            Eq(null, p.Blocked); Eq(true, p.Cooperative);
            Eq("schedule.auto.topology", AutoCores.Plan(0, raptor, true, perf, false, 0, null, true, false, 0).Blocked);
            Eq("schedule.auto.topology", AutoCores.Plan(all, new ulong[0], true, perf, false, 0, null, true, false, 0).Blocked);

            ulong laptopAll, laptopPerf;
            ulong[] laptop = AutoHybridCores(2, 8, out laptopAll, out laptopPerf);
            Eq("schedule.auto.fewcores", AutoCores.Plan(laptopAll, laptop, true, laptopPerf, false, 0, null, true, false, 0).Blocked);
            ulong midAll, midPerf;
            ulong[] mid = AutoHybridCores(4, 1, out midAll, out midPerf);
            p = AutoCores.Plan(midAll, mid, true, midPerf, false, 0, null, true, false, 0);
            Eq(0xFCUL, p.Exclusive); Eq(3, p.Cores);

            ulong uniformAll;
            ulong[] uniform = AutoSmtCores(8, out uniformAll);
            Eq(6, AutoCores.Plan(uniformAll, uniform, false, 0, false, 0, null, true, false, 0).Cores);
            Eq(true, AutoCores.Plan(uniformAll, uniform, false, 0, false, 0,
                new[] { uniformAll }, true, false, 0).Available);

            ulong x3dAll;
            ulong[] x3d = AutoSmtCores(16, out x3dAll);
            p = AutoCores.Plan(x3dAll, x3d, false, 0, true, 0xFFFFUL, null, true, false, 0);
            Eq("cache", p.Kind); Eq(0xFFFCUL, p.Exclusive); Eq(7, p.Cores);
            p = AutoCores.Plan(x3dAll, x3d, false, 0, true, 0xFFFF0000UL, null, true, false, 0);
            Eq(0xFFFF0000UL, p.Exclusive); Eq(8, p.Cores);
            p = AutoCores.Plan(x3dAll, x3d, false, 0, false, 0, new[] { 0xFFFF0000UL, 0xFFFFUL }, true, false, 0);
            Eq("ccd", p.Kind); Eq(0xFFFF0000UL, p.Region); Eq(0xFFFF0000UL, p.Exclusive);
            p = AutoCores.Plan(x3dAll, x3d, false, 0, false, 0, new[] { 0xFFFFUL, 0xFFFF0000UL }, true, false, 0, 0xFFFCUL);
            Eq(0xFFFFUL, p.Region);

            ulong[] lopsided = { 1, 6, 24, 96, 384 };
            p = AutoCores.Plan(0x1FF, lopsided, false, 0, true, 0x1FE, null, true, false, 0);
            Eq(0x7EUL, p.Exclusive); Eq(3, p.Cores);
            p = AutoCores.Plan(0x1FF, lopsided, false, 0, true, 0x1FE, null, true, false, 0x180);
            Eq(0x1EUL, p.Exclusive); Eq(2, p.Cores);
            foreach (string kind in new[] { "hybrid", "cache", "ccd" })
                Eq(true, AutoCores.KindText(kind).Length > 0);
            Console.WriteLine("PASS AutoCorePlan: P-cores, cache CCD, first CCD, CPU 0 core skipped, reserved cores skipped, two spare cores kept");
        }

        private static void AutoCoreEngineRange()
        {
            ulong[] cores = { 3, 12, 48, 192 };
            Eq(null, AutoCores.ValidateRange(12, 12, 255, cores, 0));
            Eq(null, AutoCores.ValidateRange(60, 60, 255, cores, 0));
            Eq("whole", AutoCores.ValidateRange(3, 3, 255, cores, 0));
            Eq("whole", AutoCores.ValidateRange(4, 12, 255, cores, 0));
            Eq("whole", AutoCores.ValidateRange(0, 12, 255, cores, 0));
            Eq("whole", AutoCores.ValidateRange(1024, 12, 255, cores, 0));
            Eq("spare", AutoCores.ValidateRange(252, 252, 255, cores, 0));
            Eq("game", AutoCores.ValidateRange(12, 4, 255, cores, 0));
            Eq("game", AutoCores.ValidateRange(12, 0, 255, cores, 0));
            Eq("game", AutoCores.ValidateRange(12, 1024 | 12, 255, cores, 0));
            Eq("topology", AutoCores.ValidateRange(12, 12, 255, new ulong[] { 3, 12, 48 }, 0));
            Eq("topology", AutoCores.ValidateRange(12, 12, 255, new ulong[] { 3, 6, 48, 192 }, 0));
            Eq("topology", AutoCores.ValidateRange(12, 12, 255, new ulong[0], 0));
            Eq("topology", AutoCores.ValidateRange(12, 12, 0, cores, 0));
            Eq("spare", AutoCores.ValidateRange(0x3C, 0x3C, 255, cores, 0xC0));
            Eq(null, AutoCores.ValidateRange(12, 12, 255, cores, 0x30));
            Eq(null, AutoCores.ValidateRange(0x3C, 0x3C, 255, cores, 0x80));
            Console.WriteLine("PASS AutoCoreEngineRange: whole cores, never CPU 0, two spare cores, sane topology");
        }

        private static ThreadTimes AutoTimes(long ticks, long creation, long[] ids, long[] cpu)
        {
            return new ThreadTimes { Ticks = ticks, Creation = creation, Ids = ids, Cpu = cpu };
        }

        private static ThreadTimes AutoTimes(long ticks, long creation, long processCpu, long[] ids, long[] created, long[] cpu)
        {
            return new ThreadTimes { Ticks = ticks, Creation = creation, ProcessCpu = processCpu, Ids = ids, Created = created, Cpu = cpu };
        }

        private static void AutoCoreThreadLoad()
        {
            long window = 5 * TimeSpan.TicksPerSecond, second = TimeSpan.TicksPerSecond;
            ThreadTimes before = AutoTimes(1000, 7, new long[] { 1, 2, 3, 9 }, new long[] { 0, 0, 0, 900 });
            ThreadTimes after = AutoTimes(1000 + window, 7, new long[] { 1, 2, 3, 4, 9 },
                new long[] { window, window / 4, window / 10, window * 3 / 10, 800 });
            int active; double load;
            Eq(true, AutoCores.Measure(before, after, out active, out load));
            Eq(3, active); Eq(true, Math.Abs(load - 1.65) < 1e-9);

            after = AutoTimes(1000 + window, 7, new long[] { 1 }, new long[] { window * 3 });
            Eq(true, AutoCores.Measure(before, after, out active, out load));
            Eq(1, active); Eq(true, Math.Abs(load - 1.0) < 1e-9);

            ThreadTimes reusedBefore = AutoTimes(0, 7, 1, new long[] { 5 }, new long[] { 100 }, new long[] { 15 * second / 10 });
            ThreadTimes reusedAfter = AutoTimes(window, 7, 1 + 2 * second, new long[] { 5 }, new long[] { 200 }, new long[] { 2 * second });
            Eq(true, AutoCores.Measure(reusedBefore, reusedAfter, out active, out load));
            Eq(1, active); Eq(true, Math.Abs(load - 0.4) < 1e-9);

            ThreadTimes exitBefore = AutoTimes(0, 7, 1000, new long[] { 1, 2 }, new long[] { 10, 20 }, new long[] { 0, 0 });
            ThreadTimes exitAfter = AutoTimes(window, 7, 1000 + window / 2 + window * 9 / 10, new long[] { 1 }, new long[] { 10 }, new long[] { window / 2 });
            Eq(false, AutoCores.Measure(exitBefore, exitAfter, out active, out load));
            Eq(true, Math.Abs(load - 1.4) < 1e-9);
            exitAfter = AutoTimes(window, 7, 1000 + window / 2 + window * 3 / 10, new long[] { 1 }, new long[] { 10 }, new long[] { window / 2 });
            Eq(true, AutoCores.Measure(exitBefore, exitAfter, out active, out load));
            Eq(1, active); Eq(true, Math.Abs(load - 0.8) < 1e-9);

            Eq(false, AutoCores.Measure(before, AutoTimes(1000 + window, 8, new long[] { 1 }, new long[] { window }), out active, out load));
            Eq(0, active); Eq(0.0, load);
            Eq(false, AutoCores.Measure(before, AutoTimes(1000, 7, new long[] { 1 }, new long[] { window }), out active, out load));
            Eq(false, AutoCores.Measure(null, after, out active, out load));

            Eq(true, AutoCores.WindowUsable(before, AutoTimes(1000 + window, 7, null, null)));
            Eq(true, AutoCores.WindowUsable(before, AutoTimes(1000 + 4 * TimeSpan.TicksPerSecond, 7, null, null)));
            Eq(false, AutoCores.WindowUsable(before, AutoTimes(1000 + 39 * TimeSpan.TicksPerSecond / 10, 7, null, null)));
            Eq(true, AutoCores.WindowUsable(before, AutoTimes(1000 + 30 * TimeSpan.TicksPerSecond, 7, null, null)));
            Eq(false, AutoCores.WindowUsable(before, AutoTimes(1000 + 61 * TimeSpan.TicksPerSecond, 7, null, null)));
            Eq(false, AutoCores.WindowUsable(before, AutoTimes(1000 + window, 8, null, null)));
            Eq(false, AutoCores.WindowUsable(null, before));
            Console.WriteLine("PASS AutoCoreThreadLoad: threads keyed by id and creation time, exited threads make a window untrusted, capped share, window bounds");
        }

        private static void AutoThreadRecord(IntPtr buffer, int offset, int next, int pid, long creation, long processCpu, long[][] threads)
        {
            Marshal.WriteInt32(buffer, offset, next);
            Marshal.WriteInt32(buffer, offset + 0x04, threads.Length);
            Marshal.WriteInt64(buffer, offset + 0x20, creation);
            Marshal.WriteInt64(buffer, offset + 0x28, processCpu);
            Marshal.WriteInt64(buffer, offset + 0x30, 0);
            Marshal.WriteInt64(buffer, offset + 0x50, pid);
            for (int i = 0; i < threads.Length; i++)
            {
                int t = offset + 0x100 + i * 0x50;
                Marshal.WriteInt64(buffer, t, threads[i][0]);
                Marshal.WriteInt64(buffer, t + 0x08, threads[i][1]);
                Marshal.WriteInt64(buffer, t + 0x10, threads[i][3]);
                Marshal.WriteInt64(buffer, t + 0x28, pid);
                Marshal.WriteInt64(buffer, t + 0x30, threads[i][2]);
            }
        }

        private static void AutoCoreThreadParse()
        {
            if (IntPtr.Size != 8) { Console.WriteLine("SKIP AutoCoreThreadParse: 32-bit"); return; }
            int used = 0x150 + 0x100 + 2 * 0x50;
            IntPtr buffer = Marshal.AllocHGlobal(used);
            try
            {
                for (int i = 0; i < used; i++) Marshal.WriteByte(buffer, i, 0);
                AutoThreadRecord(buffer, 0, 0x150, 10, 111, 12, new[] { new long[] { 5, 7, 1001, 51 } });
                AutoThreadRecord(buffer, 0x150, 0, 20, 222, 10, new[] { new long[] { 1, 2, 2001, 61 }, new long[] { 3, 4, 2002, 62 } });
                ThreadTimes t = ProcessSnapshotSource.ParseThreads(buffer, used, 20, 222, 99);
                Eq(99L, t.Ticks); Eq(222L, t.Creation); Eq(10L, t.ProcessCpu); Eq(2, t.Ids.Length);
                Eq(2001L, t.Ids[0]); Eq(2002L, t.Ids[1]); Eq(3L, t.Cpu[0]); Eq(7L, t.Cpu[1]);
                Eq(61L, t.Created[0]); Eq(62L, t.Created[1]);
                t = ProcessSnapshotSource.ParseThreads(buffer, used, 10, 111, 5);
                Eq(1, t.Ids.Length); Eq(1001L, t.Ids[0]); Eq(12L, t.Cpu[0]);
                Eq(null, ProcessSnapshotSource.ParseThreads(buffer, used, 20, 223, 99));
                Eq(null, ProcessSnapshotSource.ParseThreads(buffer, used, 30, 222, 99));
                Eq(null, ProcessSnapshotSource.ParseThreads(buffer, used - 1, 20, 222, 99));
                Marshal.WriteInt64(buffer, 0x150 + 0x100 + 0x50 + 0x28, 21);
                Eq(null, ProcessSnapshotSource.ParseThreads(buffer, used, 20, 222, 99));
                Marshal.WriteInt32(buffer, 0x04, 2);
                Eq(null, ProcessSnapshotSource.ParseThreads(buffer, used, 10, 111, 5));
            }
            finally { Marshal.FreeHGlobal(buffer); }

            long creation, cpu; ulong io;
            Eq(true, Native.QueryProcessSample(Native.GetCurrentProcess(), out creation, out cpu, out io));
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            ThreadTimes live = ProcessSnapshotSource.SampleThreads(self, creation);
            Eq(true, live != null && live.Ids.Length >= 1 && live.Ids.Length == live.Cpu.Length && live.ProcessCpu > 0);
            Eq(true, Array.IndexOf(live.Ids, (long)AutoCurrentThreadId()) >= 0);
            Eq(null, ProcessSnapshotSource.SampleThreads(self, creation + 1));
            Console.WriteLine("PASS AutoCoreThreadParse: thread records bound-checked, identity checked, live self sample");
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint AutoCurrentThreadId();

        private static void AutoCoreGovernorSteps()
        {
            long w = AutoCoreGovernor.WindowMs * TimeSpan.TicksPerMillisecond, t = w;
            var g = new AutoCoreGovernor(5, 10);
            Eq(false, AutoCoreGovernor.Fits(0, 0, 5, 10));
            Eq(true, AutoCoreGovernor.Fits(20, 3.5, 5, 10));
            Eq(false, AutoCoreGovernor.Fits(20, 5.5, 5, 10));
            Eq(AutoCoreStep.None, g.Observe(3, 2, t += w));
            g.Interrupt();
            Eq(AutoCoreStep.None, g.Observe(3, 2, t += w));
            Eq(AutoCoreStep.Engage, g.Observe(3, 2, t += w));
            for (int i = 0; i < 100; i++)
            {
                Eq(AutoCoreStep.None, g.Observe(i % 2 == 0 ? 0 : 20, i % 2 == 0 ? 0 : 16, t += w));
                Eq(true, g.Exclusive);
            }
            Eq(AutoCoreStep.None, g.CheckLease(t + 86400 * TimeSpan.TicksPerSecond));
            Eq(true, g.Exclusive); Eq(0, g.Releases);
            g.Grow(7, 14); Eq(7, g.Capacity); g.Grow(2, 4); Eq(7, g.Capacity);
            Eq(AutoCoreStep.Release, g.Block("schedule.auto.external"));
            Eq(false, g.Exclusive); Eq(AutoCoreStep.None, g.Observe(1, .5, t += w));
            Eq(AutoCoreStep.None, new AutoCoreGovernor(0, 0).Observe(1, .5, t));

            ulong all;
            ulong[] cores = AutoSmtCores(6, out all);
            AutoCorePlan plan = AutoCores.Plan(all, cores, false, 0, false, 0, null, true, false, 0);
            Eq(4, plan.Cores); Eq(0x3FCUL, plan.Exclusive); Eq(false, plan.Cooperative);
            AutoCorePlan favored = AutoCores.Plan(all, cores, false, 0, false, 0, null, true, false, 0, 0xC00);
            Eq(0xC00UL, favored.Exclusive & 0xC00); Eq(4, favored.Cores);
            Eq(Lang.T("schedule.auto.kind.uniform"), AutoCores.KindText(plan.Kind));
            ulong expanded = AutoCores.Expand(plan, plan.Exclusive, all, cores, 0, 100);
            Eq(0xFFCUL, expanded); Eq(0x3FCUL, plan.Exclusive);
            Eq(0UL, expanded & 3UL); Eq(0UL, plan.Exclusive & ~expanded);
            plan.Allocated = 0xC00;
            Eq(plan.Exclusive, AutoCores.Expand(plan, plan.Exclusive, all, cores, 0, 100));
            Eq("schedule.auto.topology", AutoCores.Plan(all, new ulong[] { 3, 6 }, false, 0, false, 0, null, true, false, 0).Blocked);
            var sampling = new AutoCoreSampling();
            t = w;
            for (int i = 0; i < 4; i++) { sampling.Record(t, true, 2, 1.0, 100, true); t += w; }
            Eq(15000, sampling.IntervalMs); Eq(4, sampling.Reused);
            sampling.Burst(t - w + 1); Eq(false, sampling.Due(t - w + 1)); Eq(true, sampling.Due(t));
            for (int i = 0; i < 3; i++) sampling.Record(t += w, false, 0, 0, 0, false);
            Eq(30000, sampling.IntervalMs);
            sampling.Burst(t + 1); Eq(30000, sampling.IntervalMs); Eq(false, sampling.Due(t + w));
            sampling.Record(t += w, true, 2, 1, 5 * TimeSpan.TicksPerMillisecond, false);
            Eq(15000, sampling.IntervalMs); Eq(5.0, sampling.MaxMs);
            var guard = new AutoCoreLoadGuard();
            Eq(false, guard.Observe(0, w, 3, true));
            Eq(false, guard.Observe(4 * w, 2 * w, 3, true));
            Eq(true, guard.Observe(8 * w, 3 * w, 3, true));
            Eq(true, guard.Burst);
            Eq(false, guard.Observe(8 * w, 4 * w, 3, true));
            Console.WriteLine("PASS persistent reservation: 100 idle/overload windows + 24-hour gap retain base; expansion keeps whole cores and CPU0 spare; adaptive sampling is bounded and backs off");
        }

        private static void AutoCoreSessionPlan(string output)
        {
            Settings.UseTransientStoreForCurrentProcess();
            var topology = CpuTopology.CaptureTopologyForTest();
            var allocated = AutoCores.AllocatedForTest;
            try
            {
                ulong all; ulong[] cores = AutoSmtCores(16, out all);
                CpuTopology.InjectTopologyForTest(all, cores, new ulong[0], 0, 0, 0, 0, false, false);
                AutoCores.AllocatedForTest = delegate { return 0; };
                using (var f = new FamilyPolicyFixture(output, "manual-only-legacy"))
                {
                    Settings.Save(AutomaticPolicy.VersionKey, true);
                    var profile = f.First.Clone();
                    profile.Overrides[PolicyCatalog.KeyAutoCores] = "1";
                    FamilyPolicySetField(f.Mode, "allMask", all);
                    FamilyPolicySetField(f.Mode, "activeDetection", new GameDetection { Profile = profile,
                        RendererPid = 424242, RendererCreation = 12345, RendererName = "legacy-auto", RendererCandidateSelected = true });
                    f.Mode.RendererTestForeground = delegate { return 424242; };
                    f.Mode.CorePlacementOpenForTest = delegate { throw new Exception("Legacy auto must not open a placement handle"); };
                    f.Mode.CoreEngageForTest = delegate { throw new Exception("Legacy auto must not reserve cores"); };
                    foreach (bool automaticPolicy in new[] { true, false })
                    foreach (string preset in new[] { "0", "1", "6", "4" })
                    {
                        Settings.Save(AutomaticPolicy.VersionKey, automaticPolicy);
                        Settings.SaveStr(PolicyCatalog.KeyPreset, preset);
                        profile.Overrides[PolicyCatalog.KeyPreset] = preset;
                        var policy = PolicyResolver.For(profile);
                        Eq(false, policy.AutoCoresAllowed);
                        FamilyPolicySetField(f.Mode, "sessionPolicy", policy);
                        FamilyPolicyInvoke(f.Mode, "BeginAutoCores", policy, true);
                        for (int i = 0; i < 10; i++)
                        {
                            object pass = FamilyPolicyInvoke(f.Mode, "PrepareBoostPass");
                            Eq(false, AutoPassField<bool>(pass, "Exclusive"));
                            Eq(all, AutoPassField<ulong>(pass, "DesiredMask"));
                            FamilyPolicyInvoke(f.Mode, "RunIndependentCorePlacement", pass);
                        }
                        Eq(null, f.Mode.ProbeGovernor); Eq(false, f.Mode.ProbeCoreEngaged);
                        Eq(0, AutoCores.Status.SampleCount); Eq(0, AutoCores.Status.Windows);
                        Eq(0UL, (ulong)FamilyPolicyInvoke(f.Mode, "CoreBackgroundTarget"));
                    }
                    var bad = new ManualCorePlan(0xFFFF, true, "wrong-topology");
                    profile.Overrides[ManualCorePlan.Key] = bad.Encode();
                    FamilyPolicySetField(f.Mode, "sessionPolicy", PolicyResolver.For(profile));
                    FamilyPolicyInvoke(f.Mode, "BeginAutoCores", PolicyResolver.For(profile), true);
                    Eq(false, AutoPassField<bool>(FamilyPolicyInvoke(f.Mode, "PrepareBoostPass"), "Exclusive"));
                }
            }
            finally
            {
                AutoCores.AllocatedForTest = allocated;
                CpuTopology.RestoreTopologyForTest(topology); AutoCores.Publish(null);
                Settings.UseTransientStoreForCurrentProcess();
            }
            Console.WriteLine("PASS ManualOnly: legacy auto disabled in every mode; no sampling, affinity or reservation; invalid manual plans stay shared");
        }

        private static T AutoPassField<T>(object pass, string name)
        {
            return (T)pass.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public).GetValue(pass);
        }

        private static void AutoCoreRetiredKeys(string output)
        {
            string[] policyKeys = { "GmManualCorePlanV1", "GmCoreMask", "GmStrictCores", "GmCoreDomainAlt",
                "GmHeavySqueezeV2", "GmHeavyCoreMaskV1" };
            foreach (string key in AutoCores.RetiredSettings)
            {
                Eq(null, PolicyCatalog.ItemOf(key));
                Eq(null, PolicyCatalog.Canonical(key, "1"));
            }
            foreach (string key in policyKeys)
            {
                Eq(true, Array.IndexOf(AutoCores.RetiredSettings, key) >= 0);
                Eq(true, GameProfile.IsRetiredOverrideKey(key));
                Eq(false, PolicyResolver.SetOverride(new GameProfile(), key, "1"));
            }
            string dir = Path.Combine(output, "auto-core-retired-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            GameProfile profile = FamilyPolicyProfile("retired-core", dir);
            Eq(true, PolicyResolver.SetOverride(profile, PolicyCatalog.KeyBoost, "0"));
            Eq(true, new GameProfileStore(dir).Save(new[] { profile }));
            string file = Path.Combine(dir, GameProfileStore.FileName);
            var extra = new StringBuilder();
            foreach (string key in policyKeys)
                extra.Append("O|").Append(AutoB64(profile.Id)).Append('|').Append(AutoB64(key)).Append('|')
                    .Append(AutoB64(key == "GmManualCorePlanV1" ? "2|x|C|1|C" : "1")).Append(Environment.NewLine);
            File.AppendAllText(file, extra.ToString());
            List<GameProfile> loaded = new GameProfileStore(dir).LoadProfiles();
            Eq(1, loaded.Count); Eq(1, loaded[0].Overrides.Count); Eq("0", loaded[0].Overrides[PolicyCatalog.KeyBoost]);

            Settings.UseTransientStoreForCurrentProcess();
            foreach (string key in AutoCores.RetiredSettings) Settings.SaveStr(key, "1");
            var mode = new GameMode(dir, new SuppressionCore());
            foreach (string key in AutoCores.RetiredSettings) Eq("absent", Settings.LoadStr(key, "absent"));
            GC.KeepAlive(mode);
            Settings.UseTransientStoreForCurrentProcess();
            Console.WriteLine("PASS AutoCoreRetiredKeys: manual core keys dropped from settings, catalog and saved libraries without corruption");
        }

        private static void AutoCoreYieldLedger()
        {
            Settings.UseTransientStoreForCurrentProcess();
            try
            {
                Eq(true, CrashGuard.MarkBoostProcess(9001, 77, "yield-test", Native.NORMAL_PRIORITY_CLASS, 0xFF, 2, 5, -1, new uint[0]));
                Eq(false, CrashGuard.ProbeYielded(9001, 77));
                Eq(false, CrashGuard.TrackBoostAffinity(9001, 78, 12));
                Eq(true, CrashGuard.TrackBoostAffinity(9001, 77, 12));
                string receipt = Settings.LoadStr("Crash_BoostEntriesV2", "");
                Eq(true, receipt.EndsWith("|0|12|owned|0"));
                Eq("0", CrashGuard.ProbeParse(receipt.Substring(0, receipt.IndexOf("|owned"))));
                Eq("0", CrashGuard.ProbeParse(receipt.Replace("|owned", "|broken")));
                CrashGuard.YieldBoostPlacement(9001, 78);
                Eq(false, CrashGuard.ProbeYielded(9001, 77));
                CrashGuard.YieldBoostPlacement(9001, 77);
                Eq(true, CrashGuard.ProbeYielded(9001, 77));
                Eq(false, CrashGuard.TrackBoostAffinity(9001, 77, 48));
                Eq("1|-1|-1", CrashGuard.ProbeParse(Settings.LoadStr("Crash_BoostEntriesV2", "")));
                CrashGuard.ReleaseBoostProcess(9001, 77);
                Eq(false, CrashGuard.HasPending());
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); }
            Console.WriteLine("PASS AutoCoreYieldLedger: a yielded placement survives in the crash ledger so recovery keeps the external affinity");
        }

        private static string AutoB64(string s)
        {
            return Convert.ToBase64String(new UTF8Encoding(false, true).GetBytes(s));
        }

        private static void LegacyReservedCoreCleanup()
        {
            ulong all, perf;
            ulong[] raptor = AutoHybridCores(8, 16, out all, out perf);
            Eq(0x3CUL, LegacyReservedCores.LegacyMask(raptor, perf, true, all, perf));
            Eq(0UL, LegacyReservedCores.LegacyMask(new ulong[] { 3, 12, 48, 192 }, 0, false, 255, 0));
            ulong uniformAll;
            ulong[] uniform = AutoSmtCores(8, out uniformAll);
            Eq(0x3CUL, LegacyReservedCores.LegacyMask(uniform, 0, false, uniformAll, 0));
            Eq(0UL, LegacyReservedCores.LegacyMask(uniform, 0, false, uniformAll, 0xF000));
            Eq(0x3CUL, LegacyReservedCores.BytesMask(new byte[] { 0x3C, 0, 0, 0, 0, 0, 0, 0 }));
            Eq(0UL, LegacyReservedCores.BytesMask(null));
            byte[] decoded;
            Eq(false, LegacyReservedCores.TryDecodeSnapshot("", out decoded)); Eq(null, decoded);
            Eq(true, LegacyReservedCores.TryDecodeSnapshot(ReversibleReg.Absent, out decoded)); Eq(null, decoded);
            Eq(true, LegacyReservedCores.TryDecodeSnapshot("0C-00", out decoded)); Eq(0x0CUL, LegacyReservedCores.BytesMask(decoded));
            foreach (string corrupt in new[] { "zz", "0C-", "C-00", "0C_00", "0C-0G" })
                Eq(false, LegacyReservedCores.TryDecodeSnapshot(corrupt, out decoded));

            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            ulong strict = CpuTopology.StrictBoostMask;
            byte[] current = null;
            var writes = new List<string>();
            bool writeOk = true;
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                CpuTopology.InjectTopologyForTest(all, raptor, new ulong[0], perf, all & ~perf, 0, 0, true, false);
                CpuTopology.StrictBoostMask = perf;
                LegacyReservedCores.ReadForTest = delegate { return current; };
                LegacyReservedCores.WriteForTest = delegate(byte[] value)
                {
                    writes.Add(value == null ? "delete" : LegacyReservedCores.BytesMask(value).ToString("X"));
                    if (writeOk) current = value;
                    return writeOk;
                };

                byte[] legacyBytes = { 0x3C, 0, 0, 0, 0, 0, 0, 0 };
                current = legacyBytes;
                Eq(true, LegacyReservedCores.Heal());
                Eq(0, writes.Count); Eq(0x3CUL, LegacyReservedCores.BytesMask(current));
                Eq(0x3CUL, LegacyReservedCores.Suspect());
                Eq(true, LegacyReservedCores.ClearSuspect());
                Eq("delete", string.Join(",", writes.ToArray())); Eq(null, current);
                Eq(0UL, LegacyReservedCores.Suspect());
                Eq("3C", Settings.LoadStr(LegacyReservedCores.ClearedKey, ""));
                Eq(0x3CUL, LegacyReservedCores.PendingReboot(0x3C));
                Eq(0x0CUL, LegacyReservedCores.PendingReboot(0x0C));
                Eq(0UL, LegacyReservedCores.PendingReboot(0));
                Eq("", Settings.LoadStr(LegacyReservedCores.ClearedKey, ""));

                writes.Clear();
                current = new byte[] { 0x30, 0, 0, 0, 0, 0, 0, 0 };
                Eq(true, LegacyReservedCores.Heal());
                Eq(0, writes.Count); Eq(0UL, LegacyReservedCores.Suspect());
                current = legacyBytes;
                Eq(true, LegacyReservedCores.Heal());
                current = new byte[] { 0x30, 0, 0, 0, 0, 0, 0, 0 };
                Eq(false, LegacyReservedCores.ClearSuspect());
                Eq(0, writes.Count); Eq(0UL, LegacyReservedCores.Suspect());

                Settings.SaveStr("PrevReservedCpuSets", ReversibleReg.Absent);
                Settings.Save("ReservedCoresByPavise", true);
                current = legacyBytes;
                Eq(true, LegacyReservedCores.Heal());
                Eq("delete", string.Join(",", writes.ToArray())); Eq(null, current);
                Eq("", Settings.LoadStr("PrevReservedCpuSets", ""));
                Eq(false, Settings.Load("ReservedCoresByPavise", false));
                Eq("3C", Settings.LoadStr(LegacyReservedCores.ClearedKey, ""));

                writes.Clear();
                Settings.SaveStr("PrevReservedCpuSets", "0C-00-00-00-00-00-00-00");
                current = legacyBytes;
                Eq(true, LegacyReservedCores.Heal());
                Eq("C", string.Join(",", writes.ToArray())); Eq(0x0CUL, LegacyReservedCores.BytesMask(current));
                Eq("30", Settings.LoadStr(LegacyReservedCores.ClearedKey, ""));

                writes.Clear();
                Settings.SaveStr("PrevReservedCpuSets", "0C-00-00-00-00-00-00-00");
                current = new byte[] { 0x0C, 0, 0, 0, 0, 0, 0, 0 };
                Eq(true, LegacyReservedCores.Heal());
                Eq(0, writes.Count); Eq("", Settings.LoadStr("PrevReservedCpuSets", ""));

                Settings.SaveStr("PrevReservedCpuSets", ReversibleReg.Absent);
                current = new byte[] { 0x30, 0, 0, 0, 0, 0, 0, 0 };
                Eq(true, LegacyReservedCores.Heal());
                Eq(0, writes.Count); Eq(0x30UL, LegacyReservedCores.BytesMask(current));
                Eq("", Settings.LoadStr("PrevReservedCpuSets", ""));

                Settings.SaveStr("PrevReservedCpuSets", "zz");
                current = legacyBytes;
                Eq(false, LegacyReservedCores.Heal());
                Eq(0, writes.Count); Eq("zz", Settings.LoadStr("PrevReservedCpuSets", ""));

                Settings.Remove("PrevReservedCpuSets");
                current = new byte[] { 0x3C, 0, 0, 0, 0, 0, 0, 0, 1 };
                Eq(true, LegacyReservedCores.Heal()); Eq(0UL, LegacyReservedCores.Suspect()); Eq(0, writes.Count);
                Settings.SaveStr("PrevReservedCpuSets", ReversibleReg.Absent);
                Eq(true, LegacyReservedCores.Heal()); Eq(0, writes.Count); Eq(9, current.Length);
                current = legacyBytes;
                Settings.Remove("PrevReservedCpuSets");
                Settings.Save("ReservedCoresByPavise", true);
                Eq(false, LegacyReservedCores.Heal());
                Eq(0, writes.Count); Eq(true, Settings.Load("ReservedCoresByPavise", false));

                Settings.Remove(LegacyReservedCores.ClearedKey);
                Settings.SaveStr("PrevReservedCpuSets", ReversibleReg.Absent);
                writeOk = false;
                current = legacyBytes;
                Eq(false, LegacyReservedCores.Heal());
                Eq("delete", string.Join(",", writes.ToArray())); Eq(0x3CUL, LegacyReservedCores.BytesMask(current));
                Eq("", Settings.LoadStr(LegacyReservedCores.ClearedKey, ""));
                Eq(ReversibleReg.Absent, Settings.LoadStr("PrevReservedCpuSets", ""));
            }
            finally
            {
                LegacyReservedCores.ReadForTest = null;
                LegacyReservedCores.WriteForTest = null;
                CpuTopology.RestoreTopologyForTest(topology);
                CpuTopology.StrictBoostMask = strict;
                Settings.UseTransientStoreForCurrentProcess();
            }
            Console.WriteLine("PASS LegacyReservedCores: receipts restore only the value Pavise wrote, corrupt receipts stop, unowned equal values wait for the user");
        }
    }
}
#endif
