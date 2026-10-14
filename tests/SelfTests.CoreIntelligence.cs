#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static ThreadTimes IntelligenceSample(int step, long creation)
        {
            return new ThreadTimes { Creation = 7, Ticks = (step * 5L + 1) * TimeSpan.TicksPerSecond,
                Ids = new long[] { 101, 102, 103 }, Created = new long[] { creation, 22, 33 },
                Cpu = new long[] { step * 25000000L, step * 20000000L, step * 2000000L }, ProcessCpu = step * 47000000L };
        }
        private static void RunCoreIntelligenceTests(string root)
        {
            var tracker = new CoreThreadTracker();
            Eq(0, tracker.Observe(null, IntelligenceSample(0, 11)).Count);
            Eq(0, tracker.Observe(IntelligenceSample(0, 11), IntelligenceSample(1, 11)).Count);
            Eq(0, tracker.Observe(IntelligenceSample(1, 11), IntelligenceSample(2, 11)).Count);
            var candidates = tracker.Observe(IntelligenceSample(2, 11), IntelligenceSample(3, 11));
            Eq(2, candidates.Count); Eq(101, candidates[0].Tid);
            candidates = tracker.Observe(IntelligenceSample(3, 11), IntelligenceSample(4, 99));
            Eq(1, candidates.Count); Eq(102, candidates[0].Tid);
            var invalid = IntelligenceSample(5, 99); invalid.Creation = 8;
            Eq(0, tracker.Observe(IntelligenceSample(4, 99), invalid).Count);
            invalid = IntelligenceSample(5, 99); invalid.Created = null;
            Eq(0, tracker.Observe(IntelligenceSample(4, 99), invalid).Count);

            var budget = new CoreWorkBudget(); long now = AutoCores.NowTicks;
            Eq(true, budget.Admit(now)); budget.Record(now - 26 * TimeSpan.TicksPerMillisecond);
            Eq(false, budget.Admit(now + TimeSpan.TicksPerSecond)); Eq(1, budget.Deferrals);
            Eq(true, budget.Admit(now + 6 * TimeSpan.TicksPerSecond)); Eq(true, budget.TotalMs >= 26);

            var baseline = new CoreFrameWindow { Complete = true, WallMs = 20000, Load = 1 };
            var trial = new CoreFrameWindow { Complete = true, WallMs = 20000, Load = 1.1 };
            for (int i = 0; i < 600; i++) { baseline.Frames.Add(i % 20 == 0 ? 20 : 10); trial.Frames.Add(i % 20 == 0 ? 40 : 10); }
            Eq("regressed", CoreFrameWindow.Compare(baseline, trial));
            trial.Load = 3; Eq("phase-changed", CoreFrameWindow.Compare(baseline, trial));
            trial.Load = 1; trial.Failed = true; Eq("insufficient", CoreFrameWindow.Compare(baseline, trial));
            Eq("a,b", CoreFrameCapture.SplitCsv("\"a,b\",12")[0]);
            Eq("a\"b", CoreFrameCapture.SplitCsv("\"a\"\"b\",12")[0]);
            var parser = new CoreFrameCapture();
            typeof(CoreFrameCapture).GetField("pid", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(parser, 123);
            parser.Parse("Application,ProcessID,SwapChainAddress,MsBetweenPresents");
            parser.Parse("\"game, test.exe\",123,0x100,8.5");
            parser.Parse("foreign.exe,124,0x100,2");
            parser.Parse("game.exe,123,0x200,12.5");
            var chains = (Dictionary<string, List<double>>)typeof(CoreFrameCapture).GetField("chains", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parser);
            Eq(2, chains.Count); Eq(1, chains["0x100"].Count); Eq(8.5, chains["0x100"][0]);
            parser.Parse("game.exe,123,0x100,2000");
            Eq(true, (bool)typeof(CoreFrameCapture).GetField("invalid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(parser));

            string exe = Path.Combine(root, "learning-version.exe"); File.WriteAllText(exe, "version1");
            var learned = CoreGameLearning.Load(exe); Eq(false, learned.Loaded);
            learned.Save(4, 2.7, false); var read = CoreGameLearning.Load(exe);
            Eq(true, read.Loaded); Eq(4, read.Capacity); Eq(2.7, read.Peak);
            read.AccessRetryAt = DateTime.UtcNow.AddMinutes(30).Ticks; read.Save(4, 2.7, false);
            Eq(read.AccessRetryAt, CoreGameLearning.Load(exe).AccessRetryAt);
            Eq(false, CoreGameLearning.Load(exe, "Competitive").Loaded);
            Settings.SaveStr("CoreLearn_" + read.Key, "1|4|0|NaN|" + DateTime.UtcNow.Ticks + "|0");
            Eq(false, CoreGameLearning.Load(exe).Loaded);
            Settings.SaveStr("CoreLearn_" + read.Key, "1|4|0|2.7|" + DateTime.UtcNow.AddDays(-31).Ticks + "|0");
            Eq(false, CoreGameLearning.Load(exe).Loaded);
            File.AppendAllText(exe, "updated"); Eq(false, CoreGameLearning.Load(exe).Loaded);
            Eq(false, CoreGameLearning.Load(Path.Combine(root, "absent.exe")).Loaded);

            Settings.SaveStr(CoreThreadPlacement.JournalKey, "broken-receipt");
            Eq(true, new CoreThreadPlacement().Restore());
            Eq("broken-receipt", Settings.LoadStr(CoreThreadPlacement.JournalKey, ""));
            Eq(false, CoreThreadPlacement.Recover()); Eq(true, CrashGuard.HasPending());
            Settings.SaveStr(CoreThreadPlacement.JournalKey, "");
            Settings.BeforeStrictStringReadForTest = delegate(string key) { if (key == CoreThreadPlacement.JournalKey) throw new IOException("denied"); };
            try { Eq(true, CoreThreadPlacement.HasResidue); Eq(false, CoreThreadPlacement.Recover()); }
            finally { Settings.BeforeStrictStringReadForTest = null; }
            Console.WriteLine("PASS core intelligence: stable candidate identity, churn, budget, frame evidence, versioned learning, recovery ownership");
        }
        internal static void RunCoreThreadPlacementNative(int pid, long creation, IntPtr process)
        {
            ThreadTimes sample = ProcessSnapshotSource.SampleThreads(pid, creation);
            Eq(true, sample != null && sample.Ids.Length > 0);
            ulong mask = Native.QueryAffinity(process);
            var candidate = new CoreThreadCandidate { Tid = (int)sample.Ids[0], Creation = sample.Created[0], Windows = 3, Mean = .6 };
            var placement = new CoreThreadPlacement();
            uint[] original = Native.QueryCpuSets(process);
            try
            {
                Eq(true, placement.Apply(pid, creation, new[] { candidate }, mask, CpuTopology.PhysicalCoreMasks(), CpuTopology.FavoredMask));
                Eq(1, placement.Candidates); Eq(true, placement.Audit(sample));
                Eq(true, Settings.LoadStr(CoreThreadPlacement.JournalKey, "").Length > 0);
                Eq(true, placement.Restore()); Eq(true, Native.CpuSetsMatch(process, original));
                Eq(true, placement.Apply(pid, creation, new[] { candidate }, mask, CpuTopology.PhysicalCoreMasks(), CpuTopology.FavoredMask));
                Eq(true, CoreThreadPlacement.Recover()); Eq(true, Native.CpuSetsMatch(process, original));
                Eq(true, placement.Restore());
                Eq(true, placement.Apply(pid, creation, new[] { candidate }, mask, CpuTopology.PhysicalCoreMasks(), CpuTopology.FavoredMask));
                uint[] outside = CpuTopology.CpuSetIdsFor(mask);
                Eq(true, Native.RestoreCpuSets(process, outside));
                Eq(false, placement.Audit(sample)); Eq(true, placement.Restore());
                Eq(true, Native.CpuSetsMatch(process, outside)); Eq(true, Native.RestoreCpuSets(process, original));
                candidate.Creation++;
                Eq(false, placement.Apply(pid, creation, new[] { candidate }, mask, CpuTopology.PhysicalCoreMasks(), 0));
                Eq(true, Native.CpuSetsMatch(process, original));
                RunCoreFeedbackNative(pid, creation, process, candidate.Tid, candidate.Creation - 1, mask);
                Console.WriteLine("PASS native thread CPU Sets: apply/readback, workers separated, exact restore, durable crash recovery, external ownership, identity rejection");
            }
            finally { placement.Restore(); Native.RestoreCpuSets(process, original); }
        }
        private static void RunCoreFeedbackNative(int pid, long creation, IntPtr process, int tid, long born, ulong mask)
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseFrameGuard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var mode = new GameMode(root, new SuppressionCore());
            ulong targetMask = 0; int coreCount = 0;
            foreach (ulong c in CpuTopology.PhysicalCoreMasks())
                if ((c & mask) == c && (c & 1) == 0) { targetMask |= c; if (++coreCount == 4) break; }
            Eq(4, coreCount);
            var plan = new AutoCorePlan { Exclusive = targetMask, Region = targetMask, Cores = coreCount, Logical = AutoCores.CountBits(targetMask) };
            mode.ProbeBeginAutoCores(plan);
            FamilyPolicySetField(mode, "coreWatchPid", pid); FamilyPolicySetField(mode, "coreWatchCreation", creation);
            Eq(true, mode.ProbePinExclusive(process, pid, creation, mask, targetMask));
            FamilyPolicySetField(mode, "coreEngaged", true);
            int captures = 0;
            CoreFrameCapture.CaptureForTest = delegate(int target)
            {
                Eq(pid, target); captures++;
                var w = new CoreFrameWindow { Complete = true, WallMs = 20000, Load = .6, SwapChain = "fixture" };
                for (int i = 0; i < 600; i++) w.Frames.Add(i % 20 == 0 ? (captures == 1 ? 20 : 45) : 10);
                return w;
            };
            try
            {
                for (int step = 1; step <= 4; step++)
                {
                    var a = new ThreadTimes { Creation = creation, Ticks = ((step - 1) * 5L + 1) * TimeSpan.TicksPerSecond,
                        Ids = new long[] { tid }, Created = new long[] { born }, Cpu = new long[] { (step - 1) * 30000000L } };
                    var b = new ThreadTimes { Creation = creation, Ticks = (step * 5L + 1) * TimeSpan.TicksPerSecond,
                        Ids = new long[] { tid }, Created = new long[] { born }, Cpu = new long[] { step * 30000000L } };
                    FamilyPolicyInvoke(mode, "ObserveCoreThreads", a, b, .6);
                    if (step == 3) FamilyPolicyInvoke(mode, "PollCoreFeedback");
                }
                Eq(2, captures);
                Eq(1, ((CoreThreadPlacement)FamilyPolicyGetField(mode, "coreThreadPlacement")).Candidates);
                for (int i = 0; i < 2; i++)
                {
                    FamilyPolicySetField(mode, "coreIntelligenceNext", 0L);
                    FamilyPolicyInvoke(mode, "PollCoreFeedback");
                }
                Eq(3, captures);
                Eq(0, ((CoreThreadPlacement)FamilyPolicyGetField(mode, "coreThreadPlacement")).Candidates);
                Eq(true, mode.ProbeCoreEngaged); Eq(targetMask, Native.QueryAffinity(process));
                Eq("regressed", (string)FamilyPolicyGetField(mode, "coreFeedback"));
                Eq(true, Native.CpuSetsMatch(process, new uint[0]));
                Console.WriteLine("PASS automatic feedback lifecycle: fake frame baseline + two regressions withdraw real thread routing, base pin remains intact");
            }
            finally
            {
                CoreFrameCapture.CaptureForTest = null;
                FamilyPolicyInvoke(mode, "StopCoreRefinement"); mode.ProbeRestoreHardPin(pid); CrashGuard.ReleaseBoostProcess(pid, creation);
            }
        }
    }
}
#endif
