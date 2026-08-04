// @author bdth 2074055628@qq.com
// Present thread attribution and thread-handle probe reporting tests.

using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestPresentThreadAttribution()
        {
            Lang.Init();

            var single = new PresentThreadTracker();
            long us = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                single.Add(frame % 9 == 0 ? 4242 : 1001, us);
                us += 16700;
            }
            single.Seal();
            PresentThreadSummary s;
            Eq(true, single.TryDescribe(out s));
            Eq(1001, s.DominantTid);
            Eq(2, s.ThreadCount);
            Eq(100, s.StabilityPercent);
            if (s.DominantSharePercent < 85)
                throw new Exception("dominant share too low: " + s.DominantSharePercent);
            Eq(true, PresentThreadTracker.LooksSingleThreaded(s));
            Eq(5, s.Windows);
            Eq(600L, s.Samples);

            var spread = new PresentThreadTracker();
            us = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                spread.Add(2000 + frame % 5, us);
                us += 16700;
            }
            spread.Seal();
            PresentThreadSummary p;
            Eq(true, spread.TryDescribe(out p));
            Eq(5, p.ThreadCount);
            if (p.DominantSharePercent > 25)
                throw new Exception("spread share unexpectedly high: " + p.DominantSharePercent);
            Eq(false, PresentThreadTracker.LooksSingleThreaded(p));

            var brief = new PresentThreadTracker();
            for (int frame = 0; frame < 60; frame++) brief.Add(7, frame * 16700);
            brief.Seal();
            PresentThreadSummary b;
            Eq(false, brief.TryDescribe(out b));

            var gap = new PresentThreadTracker();
            gap.Add(9, 0);
            gap.Add(9, 1000);
            gap.Add(9, 60L * 1000000);
            gap.Add(9, 60L * 1000000 + 1000);
            gap.Add(9, 62L * 1000000 + 2000);
            gap.Seal();
            PresentThreadSummary g;
            Eq(true, gap.TryDescribe(out g));
            Eq(3, g.Windows);
            Eq(100, g.StabilityPercent);

            var tie = new PresentThreadTracker();
            us = 0;
            for (int frame = 0; frame < 480; frame++)
            {
                tie.Add(frame % 2 == 0 ? 900 : 800, us);
                us += 16700;
            }
            tie.Seal();
            PresentThreadSummary t;
            Eq(true, tie.TryDescribe(out t));
            Eq(800, t.DominantTid);

            Eq(true, PresentThreadTracker.Describe(s).Length > 0);
            Eq(true, PresentThreadTracker.Describe(p).Length > 0);

            var ok = new ThreadAccessProbe.Result
            { Enumerated = true, ThreadCount = 30, CanQuery = true, CanSet = true };
            var readonlyCase = new ThreadAccessProbe.Result
            { Enumerated = true, ThreadCount = 30, CanQuery = true, CanSet = false, SetError = 5 };
            var denied = new ThreadAccessProbe.Result
            { Enumerated = true, ThreadCount = 30, CanQuery = false, CanSet = false, QueryError = 5, SetError = 5 };
            var noEnum = new ThreadAccessProbe.Result();
            foreach (ThreadAccessProbe.Result r in new[] { ok, readonlyCase, denied, noEnum })
                if (string.IsNullOrEmpty(ThreadAccessProbe.Describe(r)))
                    throw new Exception("probe description missing");
            if (ThreadAccessProbe.Describe(readonlyCase) == ThreadAccessProbe.Describe(denied))
                throw new Exception("read-only and fully denied must be distinguishable");

            ThreadAccessProbe.Result self;
            Eq(true, ThreadAccessProbe.TryProbe(
                System.Diagnostics.Process.GetCurrentProcess().Id, out self));
            Eq(true, self.Enumerated);
            Eq(true, self.CanQuery);
            Eq(true, self.CanSet);
        }
    }
}
