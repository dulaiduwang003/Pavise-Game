// @author bdth 2074055628@qq.com
// 文件用途 核心隔离 核心分配 中断硬钉的联合台架 非特权 不写系统范围 不启动正常运行时 无窗口
//   隔离的系统写入需要管理员 本台架不提权 该部分只跑逻辑层 真实系统写入由 Test-CoreScheduling.ps1 -LiveIsolation 覆盖
#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 只记录调用序列与掩码 不碰真实系统 CPU 范围
    internal sealed class JointIsolationPlatform : ICoreIsolationPlatform, ICoreIsolationStore
    {
        internal string Boot = "aabbccddaabbccddaabbccddaabbccdd", Receipt = "";
        internal ulong All, Allocated, Allowed;
        internal ulong[] Physical;
        internal int TargetPid;
        internal long TargetCreation;
        internal readonly List<string> Operations = new List<string>();

        public string BootIdentity() { return Boot; }
        public IsolationCpuState ReadSystem(IntPtr p)
        {
            return new IsolationCpuState { All = All, Allocated = Allocated,
                Admitted = p == IntPtr.Zero ? 0 : Allocated & Allowed, Physical = Physical };
        }
        public bool SetSystemAllowed(ulong mask)
        { Operations.Add("system:" + Hex(mask)); Allocated = All & ~mask; return true; }
        public IntPtr Open(int pid, long creation)
        { return pid == TargetPid && creation == TargetCreation ? new IntPtr(7) : IntPtr.Zero; }
        public bool Exited(IntPtr h) { return false; }
        public bool Gone(int pid, long creation) { return pid != TargetPid || creation != TargetCreation; }
        public bool ReadAllowed(IntPtr h, out ulong mask) { mask = Allowed; return true; }
        public bool SetAllowed(IntPtr h, ulong mask)
        { Operations.Add("process:" + Hex(mask)); Allowed = mask; return true; }
        public void Close(IntPtr h) { }
        public bool Read(out string value) { value = Receipt; return true; }
        public bool Write(string value) { Operations.Add("journal"); Receipt = value; return true; }
        internal CoreIsolationEngine Engine() { return new CoreIsolationEngine(this, this); }
        private static string Hex(ulong v) { return v.ToString("X"); }
    }

    internal static class CoreJointBench
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
        private static string Exe { get { return typeof(CoreJointBench).Assembly.Location; } }
        private static int checks, failures;
        private static TextWriter report;

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--sample") { Sample(); return 0; }
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Init();
            string path = args.Length >= 1 ? args[0]
                : Path.Combine(Path.GetDirectoryName(Exe), "core-joint-bench.txt");
            using (var writer = new StreamWriter(path, false))
            {
                writer.AutoFlush = true;
                report = writer;
                Line("CORE JOINT BENCH  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Line("privileged=false  system_cpu_range_written=false  normal_runtime_started=false  windows_shown=0");
                Line("");
                try
                {
                    Topology();
                    ValidationOnRealTopology();
                    IsolationEngineOnRealTopology();
                    JointPlacement();
                }
                catch (Exception e) { Fail("bench aborted: " + e.Message); Line(e.ToString()); }
                Line("");
                Line("TOTAL checks=" + checks + " failed=" + failures);
                return failures == 0 ? 0 : 1;
            }
        }

        internal static void Line(string text) { report.WriteLine(text); Console.WriteLine(text); }
        private static void Ok(string name) { checks++; Line("  PASS " + name); }
        private static void Fail(string name) { checks++; failures++; Line("  FAIL " + name); }
        internal static void Check(bool value, string name) { if (value) Ok(name); else Fail(name); }
        internal static string Hex(ulong v) { return "0x" + v.ToString("X"); }
        internal static int Bits(ulong v) { return CpuTopology.CountSetBits(v); }
        internal static ulong[] Cores { get { return cores; } }

        // ---- 1 真实拓扑 ----
        private static ulong[] cores;
        private static ulong all;

        private static void Topology()
        {
            Line("[1] REAL TOPOLOGY");
            all = CpuTopology.AllMask;
            cores = CpuTopology.PhysicalCoreMasks();
            Line("  all=" + Hex(all) + " logical=" + Bits(all) + " physical=" + cores.Length);
            Line("  hybrid=" + CpuTopology.Hybrid + " multiGroup=" + CpuTopology.MultiGroup);
            Line("  perf=" + Hex(CpuTopology.PerfMask) + " (" + Bits(CpuTopology.PerfMask) + " lp)"
                + "  eff=" + Hex(CpuTopology.EffMask) + " (" + Bits(CpuTopology.EffMask) + " lp)");
            int smt = 0, single = 0;
            foreach (ulong core in cores) { if (Bits(core) > 1) smt++; else single++; }
            Line("  physical cores: smt=" + smt + " non-smt=" + single);
            Line("  isolationSupported=" + CoreScheduling.IsolationSupported);
            Check(!CpuTopology.MultiGroup, "single processor group");
            Check(cores.Length >= 4, "at least four physical cores");
            ulong covered = 0;
            bool disjoint = true;
            foreach (ulong core in cores) { if ((covered & core) != 0) disjoint = false; covered |= core; }
            Check(disjoint && covered == all, "physical core masks partition the machine");
            // 混合架构上 P 核带 SMT E 核不带 两种宽度必须同时存在才算真的覆盖到混合拓扑
            if (CpuTopology.Hybrid) Check(smt > 0 && single > 0, "hybrid machine exposes both SMT and non-SMT cores");
            Line("");
        }

        private static ulong WholeCore(int index) { return cores[index]; }

        private static CoreSchedulingPlan Plan(ulong game, bool on, ulong isolation)
        {
            return new CoreSchedulingPlan { GameMask = game, IsolationOn = on, IsolationMask = isolation,
                Topology = CoreScheduling.Stamp(all, cores) };
        }

        // ---- 2 计划校验 ----
        private static void ValidationOnRealTopology()
        {
            Line("[2] PLAN VALIDATION ON REAL TOPOLOGY");
            ulong cpu0Core = 0;
            foreach (ulong core in cores) if ((core & 1UL) != 0) { cpu0Core = core; break; }
            Line("  cpu0 core=" + Hex(cpu0Core) + " width=" + Bits(cpu0Core));

            // 挑两颗不含 CPU0 的整核做隔离 优先挑带 SMT 的 好验证联动
            var picked = new List<ulong>();
            foreach (ulong core in cores)
            { if (core != cpu0Core && Bits(core) > 1 && picked.Count < 2) picked.Add(core); }
            foreach (ulong core in cores)
            { if (core != cpu0Core && !picked.Contains(core) && picked.Count < 2) picked.Add(core); }
            ulong isolated = 0;
            foreach (ulong core in picked) isolated |= core;
            Line("  isolation candidate=" + Hex(isolated) + " cores=" + picked.Count + " lp=" + Bits(isolated));

            ulong game = isolated;
            Check(CoreScheduling.Validate(Plan(game, true, isolated), all, cores, false, true) == null,
                "whole-core isolation plan accepted");
            // 半颗核不行 混合架构下要挑真的带 SMT 的核才测得到
            ulong half = 0;
            foreach (ulong core in picked)
                if (Bits(core) > 1) { half = core & (~core + 1); break; }
            if (half != 0)
                Check(CoreScheduling.Validate(Plan(game, true, (isolated & ~LowestCore(picked)) | half), all, cores, false, true)
                    == "schedule.error.whole", "half of an SMT core refused");
            else Line("  SKIP half-core check: no SMT core available outside cpu0");
            Check(CoreScheduling.Validate(Plan(game, true, cpu0Core), all, cores, false, true) == "schedule.error.cpu0",
                "cpu0 physical core refused");
            Check(CoreScheduling.Validate(Plan(game, true, isolated | (1UL << 63)), all, cores, false, true) != null,
                "out-of-range isolation bit refused");
            // 只剩 cpu0 那一颗完整物理核时必须拒绝 隔离之外至少要留两颗
            ulong greedy = 0;
            foreach (ulong core in cores) { if (core == cpu0Core) continue; greedy |= core; }
            Check(CoreScheduling.Validate(Plan(game, true, greedy), all, cores, false, true) == "schedule.error.spare",
                "fewer than two spare physical cores refused");
            // 正好留两颗要放行 边界不能连正确的方案一起拒了
            ulong spareTwo = 0;
            bool skipped = false;
            foreach (ulong core in cores)
            {
                if (core == cpu0Core) continue;
                if (!skipped) { skipped = true; continue; }
                spareTwo |= core;
            }
            Check(CoreScheduling.Validate(Plan(game, true, spareTwo), all, cores, false, true) == null,
                "exactly two spare physical cores accepted");
            Check(CoreScheduling.Validate(Plan(game, true, 0), all, cores, false, true) == "schedule.error.isolationempty",
                "isolation on with empty range refused");
            Check(CoreScheduling.Validate(Plan(game, true, isolated), all, cores, false, false) == "schedule.error.isolationunsupported",
                "unsupported platform refused");
            var stale = Plan(game, true, isolated);
            stale.Topology = "stale-topology-stamp";
            Check(CoreScheduling.Validate(stale, all, cores, false, true) == "schedule.error.topology",
                "stale topology stamp refused");
            Line("");
        }

        private static ulong LowestCore(List<ulong> picked)
        {
            foreach (ulong core in picked) if (Bits(core) > 1) return core;
            return picked.Count > 0 ? picked[0] : 0;
        }

        // ---- 3 隔离引擎 真实拓扑 假平台 ----
        private static void IsolationEngineOnRealTopology()
        {
            Line("[3] ISOLATION ENGINE ON REAL TOPOLOGY (fake platform, no system write)");
            ulong cpu0Core = 0;
            foreach (ulong core in cores) if ((core & 1UL) != 0) { cpu0Core = core; break; }
            ulong isolated = 0;
            int taken = 0;
            foreach (ulong core in cores)
            { if (core == cpu0Core) continue; isolated |= core; if (++taken == 2) break; }

            var os = new JointIsolationPlatform { All = all, Physical = cores, Allowed = all,
                TargetPid = 7, TargetCreation = 100 };
            using (CoreIsolationEngine engine = os.Engine())
            {
                bool began = engine.Begin(isolated, 7, 100, isolated);
                Check(began, "Begin accepted on real 32-lp topology");
                if (!began) { Line("  error=" + Describe(engine)); Line(""); return; }
                Check(os.Allocated == isolated, "system allocated equals isolation range " + Hex(os.Allocated));
                Check(os.Allowed == (all | isolated), "game admission keeps original allowed and adds isolation");
                Check(engine.Active && engine.Pending, "engine reports active lease");
                Check(engine.Audit(), "audit passes with matching state");
                // PID 复用不能继承准入
                Check(!engine.Allow(7, 101, isolated), "pid reuse refused");
                Check(engine.Restore(), "restore succeeds");
                Check(os.Allocated == 0, "system range fully released");
                Check(os.Allowed == all, "process allowed restored to original");
                Check(os.Receipt == "", "receipt cleared");
            }
            Line("  operations=" + string.Join(" -> ", os.Operations.ToArray()));

            // 系统范围被外部占用时必须拒绝启用 不覆盖别人的掩码
            var busy = new JointIsolationPlatform { All = all, Physical = cores, Allowed = all,
                Allocated = isolated, TargetPid = 7, TargetCreation = 100 };
            using (CoreIsolationEngine engine = busy.Engine())
            {
                Check(!engine.Begin(isolated, 7, 100, isolated), "external allocation refuses to start");
                Check(busy.Operations.Count == 0, "nothing written when refusing external allocation");
            }
            Line("");
        }

        private static string Describe(CoreIsolationEngine engine)
        {
            try
            {
                System.Reflection.PropertyInfo p = typeof(CoreIsolationEngine).GetProperty("Error",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public);
                object v = p == null ? null : p.GetValue(engine, null);
                return v == null ? "(none)" : v.ToString();
            }
            catch { return "(unreadable)"; }
        }

        // ---- 4 联合 真实子进程 手动分配 + 中断硬钉 ----
        //   夹具走和 CoreSchedulingIntegrationRunner 相同的 FamilyPolicyFixture 不新开一条构造路径
        private static void JointPlacement()
        {
            Line("[4] JOINT: MANUAL PLACEMENT + IRQ HARD PIN (real child processes, unprivileged)");
            SelfTests.RunJointPlacementBench();
            Line("");
        }

        // 本机实测子进程收不到重定向 stdin 的行 命令式采样会静默卡死在 ReadLine
        //   改成只用 stdout 子进程自己按节奏连续采样 父进程丢掉跨越改动时刻的那一行再取下一行
        internal static ulong SampleChild(Process child)
        {
            ReadSampleLine(child); // 这一行可能横跨亲和性改动的瞬间 丢掉
            return ReadSampleLine(child);
        }

        private static ulong ReadSampleLine(Process child)
        {
            var read = System.Threading.Tasks.Task.Factory.StartNew(() => child.StandardOutput.ReadLine());
            if (!read.Wait(15000)) { Line("  WARN sample timed out"); return 0; }
            string raw = read.Result;
            if (raw == null) { Line("  WARN sample stream closed"); return 0; }
            raw = raw.Trim().Trim('﻿');
            ulong value;
            if (ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return value;
            Line("  WARN unparsed sample line [" + raw + "]");
            return 0;
        }

        internal static Process Child(string arguments)
        {
            return Process.Start(new ProcessStartInfo(Exe, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true });
        }

        // 父进程不发命令 结束就是直接 Kill 子进程只写 stdout 没有可等的输入
        internal static void Finish(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } }
            catch { }
            finally { p.Dispose(); }
        }

        // 子进程 八个线程一轮转 1.2 秒 记录自己实际落在哪些逻辑 CPU 上 连续输出直到被结束
        private static void Sample()
        {
            Console.WriteLine("READY");
            while (true)
            {
                long observed = 0;
                DateTime end = DateTime.UtcNow.AddMilliseconds(1200);
                var threads = new Thread[8];
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(delegate()
                    {
                        while (DateTime.UtcNow < end)
                        {
                            long bit = unchecked((long)(1UL << (int)GetCurrentProcessorNumber())), old;
                            do { old = Interlocked.Read(ref observed); }
                            while (Interlocked.CompareExchange(ref observed, old | bit, old) != old);
                            Thread.SpinWait(100);
                        }
                    });
                    threads[i].IsBackground = true;
                    threads[i].Start();
                }
                foreach (Thread t in threads) t.Join();
                Console.WriteLine(unchecked((ulong)observed).ToString("X"));
                Console.Out.Flush();
            }
        }
    }

    internal static partial class SelfTests
    {
        // 放在 SelfTests 里是为了复用既有的 FamilyPolicyFixture 与放置集成同一条夹具路径
        //   GameMode 只构造不 Start 运行时循环在 Start 里 这里不启动它
        internal static void RunJointPlacementBench()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseJointBench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Logger.LogPath = Path.Combine(root, "joint.log");
            Process game = null;
            try
            {
                game = CoreJointBench.Child("--sample");
                if (game.StandardOutput.ReadLine() != "READY")
                { CoreJointBench.Check(false, "child ready"); return; }
                IntPtr h = game.Handle;
                ulong original = Native.QueryAffinity(h);
                long creation, cpu; ulong io;
                CoreJointBench.Check(Native.QueryProcessSample(h, out creation, out cpu, out io),
                    "child identity readable");
                CoreJointBench.Line("  child pid=" + game.Id + " original affinity="
                    + CoreJointBench.Hex(original) + " lp=" + CoreJointBench.Bits(original));

                // 手动选核 取两颗不含 CPU0 的整物理核
                ulong[] cores = CoreJointBench.Cores;
                ulong cpu0Core = 0;
                foreach (ulong core in cores) if ((core & 1UL) != 0) { cpu0Core = core; break; }
                ulong target = 0;
                int taken = 0;
                foreach (ulong core in cores)
                { if (core == cpu0Core) continue; target |= core; if (++taken == 2) break; }
                CoreJointBench.Line("  manual target=" + CoreJointBench.Hex(target)
                    + " lp=" + CoreJointBench.Bits(target));

                using (var fixture = new FamilyPolicyFixture(root, "joint-bench"))
                {
                    GameMode mode = fixture.Mode;
                    CoreJointBench.Check(mode.ProbeManualPlacement(h, game.Id, creation, original, target),
                        "manual placement applied");
                    CoreJointBench.Check(Native.QueryAffinity(h) == target,
                        "hard affinity equals manual target");

                    // 实测落核 隔离没开 这里证明的是手动分配本身把进程关住了
                    ulong observed = CoreJointBench.SampleChild(game);
                    CoreJointBench.Line("  observed game cpus=" + CoreJointBench.Hex(observed)
                        + " lp=" + CoreJointBench.Bits(observed));
                    CoreJointBench.Check(observed != 0 && (observed & ~target) == 0,
                        "game ran only on manually assigned cpus");

                    // 撤中断观测 手动绑定必须留着 这是两套恢复归属的分界
                    CoreJointBench.Check(mode.ProbeRestoreManualPlacement(game.Id, false),
                        "IRQ observation cleanup accepted");
                    CoreJointBench.Check(Native.QueryAffinity(h) == target,
                        "stopping IRQ observation preserves manual placement");
                    ulong afterIrq = CoreJointBench.SampleChild(game);
                    CoreJointBench.Line("  observed after irq cleanup=" + CoreJointBench.Hex(afterIrq));
                    CoreJointBench.Check(afterIrq != 0 && (afterIrq & ~target) == 0,
                        "placement still effective after IRQ cleanup");

                    CoreJointBench.Check(mode.ProbeRestoreManualPlacement(game.Id, true),
                        "manual restore accepted");
                    CoreJointBench.Check(Native.QueryAffinity(h) == original,
                        "original affinity restored exactly");
                    ulong afterRestore = CoreJointBench.SampleChild(game);
                    CoreJointBench.Line("  observed after full restore=" + CoreJointBench.Hex(afterRestore)
                        + " lp=" + CoreJointBench.Bits(afterRestore));
                    CoreJointBench.Check(afterRestore != 0 && (afterRestore & ~original) == 0,
                        "restored process runs within its original mask");
                    CoreJointBench.Check(CoreJointBench.Bits(afterRestore) > CoreJointBench.Bits(target),
                        "restored process reaches more cpus than the manual target");

                    // 不得扩大进程原有的受限范围
                    CoreJointBench.Check(Native.SetProcessAffinityMask(h, (UIntPtr)target),
                        "apply pre-existing restriction");
                    CoreJointBench.Check(!mode.ProbeManualPlacement(h, game.Id, creation, target, original),
                        "refuses to widen a pre-existing restriction");
                    CoreJointBench.Check(Native.QueryAffinity(h) == target,
                        "restricted affinity preserved on refusal");
                    CoreJointBench.Check(Native.SetProcessAffinityMask(h, (UIntPtr)original),
                        "restriction cleaned up");
                }
            }
            finally
            {
                CoreJointBench.Finish(game);
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
#endif
