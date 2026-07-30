// @author bdth 2074055628@qq.com
// Present thread attribution and thread-handle probe reporting tests.

using System;

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestPresentThreadAttribution()
        {
            Lang.Init();

            // 单一帧关键线程：一条线程提交绝大多数帧，每个窗口都是它领先
            var single = new PresentThreadTracker();
            long us = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                // 每 16.7ms 一帧，主线程 9 帧里占 8 帧，另一条线程偶发提交
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
            // 10 秒的帧按 2 秒窗口切，应得到 5 个窗口
            Eq(5, s.Windows);
            Eq(600L, s.Samples);

            // 多线程均匀提交：每个窗口的领先者都在换，不该被当成有单一关键线程
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

            // 窗口太少不给结论：一个 2 秒窗口内的"稳定"没有意义
            var brief = new PresentThreadTracker();
            for (int frame = 0; frame < 60; frame++) brief.Add(7, frame * 16700);
            brief.Seal();
            PresentThreadSummary b;
            Eq(false, brief.TryDescribe(out b));

            // 加载停顿不该稀释稳定性：跨过 60 秒空档只推进一个窗口，而不是 30 个空窗口
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

            // 平票取较小 TID，保证同样输入总得到同样结论
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

            // 探针描述：读写都可 / 只读 / 全拒 / 枚举失败，四种都要有可读结论
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

            // 探针对本进程必须成功：自己的线程一定打得开，否则说明探针本身有问题
            ThreadAccessProbe.Result self;
            Eq(true, ThreadAccessProbe.TryProbe(
                System.Diagnostics.Process.GetCurrentProcess().Id, out self));
            Eq(true, self.Enumerated);
            Eq(true, self.CanQuery);
            Eq(true, self.CanSet);
        }
    }
}
