// File purpose Pure decision checks, no process handles, no window enumeration, no registry
// Suspected malicious process regression, verifies only the CPU delta verdict, random-name detection and the alert ledger, touches no process
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int rogueChecks;

        internal static int RunRogueProcessRegressionTests()
        {
            Action[] tests =
            {
                RogueHogNeedsTwoMinutesWithoutWindow,
                RogueVisibleOrTrustedNeverFlagged,
                RogueEscapeFlagsConfinedProcessAfterOneMinute,
                RogueEscapeNeedsPartition,
                RogueRandomNameHalvesThreshold,
                RogueReportsEachNameOnceAndResetsOnNewInstance,
                RogueRandomNameHeuristic,
                RogueLedgerRoundTrips
            };
            rogueChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS rogue-process assertions=" + rogueChecks
                + " handles=untouched windows_enumerated=false registry=untouched");
            return tests.Length;
        }

        private static void RogueCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("rogue process regression: " + message);
            rogueChecks++;
        }

        private static RogueProcessWatch.Sample RogueSample(int pid, long cpu, string name, bool confined, bool visible, bool trusted)
        {
            return new RogueProcessWatch.Sample
            {
                Pid = pid, Creation = 1000, Cpu = cpu, Name = name, Path = @"D:\stuff\" + name + ".exe",
                Confined = confined, Visible = visible, Trusted = trusted
            };
        }

        // One step per 5 seconds, the process accumulates CPU at the rate of cores full cores, returns the second of the first verdict, -1 if none
        private static int RogueRunUntilVerdict(RogueProcessWatch watch, string name, double cores, bool confined,
            bool visible, bool trusted, int logical, int confinedCpus, int seconds, out RogueProcessWatch.Verdict verdict)
        {
            verdict = null;
            long step = TimeSpan.TicksPerSecond * 5;
            long cpu = 0;
            for (int t = 0; t <= seconds; t += 5)
            {
                long now = TimeSpan.TicksPerHour + step * (t / 5);
                var samples = new List<RogueProcessWatch.Sample> { RogueSample(7, cpu, name, confined, visible, trusted) };
                List<RogueProcessWatch.Verdict> got = watch.Step(samples, now, logical, confinedCpus);
                if (got.Count > 0) { verdict = got[0]; return t; }
                cpu += (long)(step * cores);
            }
            return -1;
        }

        private static void RogueHogNeedsTwoMinutesWithoutWindow()
        {
            RogueProcessWatch.Verdict v;
            int at = RogueRunUntilVerdict(new RogueProcessWatch(), "worker", 5.0, false, false, false, 16, 4, 300, out v);
            RogueCheck(at >= 120 && at <= 130 && v != null && !v.Escaped && v.Cores > 4.9 && v.Cores < 5.1,
                "five cores with no window is reported once two minutes have passed, not before");
            at = RogueRunUntilVerdict(new RogueProcessWatch(), "worker", 3.0, false, false, false, 16, 4, 300, out v);
            RogueCheck(at < 0, "three cores on a sixteen-thread machine stays under the hog floor");
            RogueCheck(RogueProcessWatch.HogCores(16) == 4 && RogueProcessWatch.HogCores(4) == 2
                && RogueProcessWatch.HogCores(32) == 8, "the hog floor is a quarter of the logical cpus, never below two");
        }

        private static void RogueVisibleOrTrustedNeverFlagged()
        {
            RogueProcessWatch.Verdict v;
            RogueCheck(RogueRunUntilVerdict(new RogueProcessWatch(), "encoder", 12.0, false, true, false, 16, 4, 600, out v) < 0,
                "a process with a visible window is never a hog");
            RogueCheck(RogueRunUntilVerdict(new RogueProcessWatch(), "MsMpEng", 12.0, false, false, true, 16, 4, 600, out v) < 0,
                "a trusted process is never a hog");
        }

        private static void RogueEscapeFlagsConfinedProcessAfterOneMinute()
        {
            RogueProcessWatch.Verdict v;
            int at = RogueRunUntilVerdict(new RogueProcessWatch(), "MsMpEng", 6.0, true, true, true, 16, 4, 300, out v);
            RogueCheck(at >= 60 && at <= 70 && v != null && v.Escaped,
                "a confined process using more than the background cores is reported after a minute regardless of trust");
            at = RogueRunUntilVerdict(new RogueProcessWatch(), "svc", 4.5, true, true, true, 16, 4, 300, out v);
            RogueCheck(at < 0, "a confined process inside the slack band is not an escape");
        }

        private static void RogueEscapeNeedsPartition()
        {
            RogueProcessWatch.Verdict v;
            RogueCheck(RogueRunUntilVerdict(new RogueProcessWatch(), "svc", 10.0, true, true, true, 16, 16, 300, out v) < 0,
                "without a background partition there is nothing to escape from");
            RogueCheck(RogueRunUntilVerdict(new RogueProcessWatch(), "svc", 10.0, true, true, true, 16, 0, 300, out v) < 0,
                "an unknown partition size disables the escape rule");
        }

        private static void RogueRandomNameHalvesThreshold()
        {
            RogueProcessWatch.Verdict v;
            int at = RogueRunUntilVerdict(new RogueProcessWatch(), "yIgaJZfC", 2.5, false, false, false, 16, 4, 300, out v);
            RogueCheck(at >= 120 && v != null && v.RandomName, "a random-looking name is reported from half the hog floor");
            RogueCheck(RogueRunUntilVerdict(new RogueProcessWatch(), "updater", 2.5, false, false, false, 16, 4, 300, out v) < 0,
                "an ordinary name at the same load is not reported");
        }

        private static void RogueReportsEachNameOnceAndResetsOnNewInstance()
        {
            var watch = new RogueProcessWatch();
            long step = TimeSpan.TicksPerSecond * 5;
            long now = TimeSpan.TicksPerHour;
            long cpu = 0;
            int verdicts = 0;
            for (int t = 0; t <= 400; t += 5)
            {
                var s = RogueSample(9, cpu, "1TNnr", false, false, false);
                if (t >= 200) { s.Pid = 10; s.Creation = 2000; }
                verdicts += watch.Step(new List<RogueProcessWatch.Sample> { s }, now, 16, 4).Count;
                now += step;
                cpu += (long)(step * 6.0);
            }
            RogueCheck(verdicts == 1, "the same name is reported once even across a respawn");
            var fresh = new RogueProcessWatch();
            long a = 0, b = 0;
            int got = 0;
            now = TimeSpan.TicksPerHour;
            for (int t = 0; t <= 130; t += 5)
            {
                var first = RogueSample(11, a, "abc", false, false, false);
                var second = RogueSample(11, b, "abc", false, false, false);
                second.Creation = t >= 60 ? 3000 : 1000;
                got += fresh.Step(new List<RogueProcessWatch.Sample> { t >= 60 ? second : first }, now, 16, 4).Count;
                now += step;
                a += (long)(step * 6.0); b = a;
            }
            RogueCheck(got == 0, "a new instance under the same pid restarts the hold window");
        }

        private static void RogueRandomNameHeuristic()
        {
            string[] random = { "yIgaJZfC", "1TNnr", "Hnvw40", "aB3x" };
            string[] plain = { "OneDrive", "WerFault", "SDXHelper", "QyFragment", "msedge", "EOSBootStrapper", "df_helper_main", "svchost", "cs2", "ab" };
            foreach (string n in random) RogueCheck(RogueProcessWatch.LooksRandom(n), n + " looks random");
            foreach (string n in plain) RogueCheck(!RogueProcessWatch.LooksRandom(n), n + " looks ordinary");
            RogueCheck(RogueProcessWatch.LooksRandom("1TNnr.exe"), "the exe suffix is ignored");
        }

        private static void RogueLedgerRoundTrips()
        {
            long day = TimeSpan.TicksPerDay;
            long now = day * 10;
            string ledger = RogueProcessWatch.LedgerAppend("", "yIgaJZfC", now, day);
            RogueCheck(RogueProcessWatch.LedgerRecent(ledger, "yIgaJZfC", now + day / 2, day), "a name alerted half a day ago is recent");
            RogueCheck(!RogueProcessWatch.LedgerRecent(ledger, "yIgaJZfC", now + day + 1, day), "after a day the name may be alerted again");
            RogueCheck(!RogueProcessWatch.LedgerRecent(ledger, "other", now, day), "another name is not recent");
            string later = RogueProcessWatch.LedgerAppend(ledger, "1TNnr", now + day + 1, day);
            RogueCheck(later == "1TNnr|" + (now + day + 1), "expired entries are pruned on append");
            RogueCheck(!RogueProcessWatch.LedgerRecent("junk;;a|x", "a", now, day), "malformed entries are ignored");
        }
    }
}
#endif
