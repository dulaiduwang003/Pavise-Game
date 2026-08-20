// @author bdth 2074055628@qq.com
// 文件用途 对局期间的帧率检测 只在有渲染进程时开会话 自身开销超预算就停掉自己
using System;
using System.Diagnostics;

namespace PaviseApp
{
    internal static class FrameRateMonitor
    {
        private const int SampleIntervalMs = 1000;
        private const int WidenAfterMs = 2500;
        private const int MinSessionSeconds = 20;
        private const int SnapshotFrames = 1024;

        private static readonly object lk = new object();

        private static FrameClock clock;
        private static ObserverBudget budget;
        private static int activePid;
        private static long startTicks;
        private static long baseFrames;
        private static long nextSampleTicks;
        private static long widenAtTicks;
        private static bool widenTried;

        private static FrameWindow current;
        private static double worstLowFps;
        private static double worstOverBudgetShare;
        private static bool multiPresenter;
        private static bool degraded;
        private static bool startFailed;
        private static double budgetMs;

        public static void Reset()
        {
            lock (lk)
            {
                StopLocked();
                current = null;
                worstLowFps = 0;
                worstOverBudgetShare = 0;
                multiPresenter = false;
                degraded = false;
                startFailed = false;
                budgetMs = 0;
            }
        }

        public static void SampleIfDue(int rendererPid)
        {
            lock (lk)
            {
                if (rendererPid <= 0) { StopLocked(); return; }
                if (clock != null && activePid != rendererPid) StopLocked();
                if (degraded) return;
                if (clock == null)
                {
                    if (startFailed) return;
                    StartLocked(rendererPid);
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                if (startTicks == 0)
                {
                    // 会话建起来到第一帧到达之间有一秒左右的空窗 算进分母会让均值偏低
                    long seen = clock.TotalFrames;
                    if (seen > 0) { startTicks = now; baseFrames = seen; }
                }
                if (!widenTried && now >= widenAtTicks)
                {
                    widenTried = true;
                    clock.WidenIfSilent();
                }
                if (now < nextSampleTicks) return;
                nextSampleTicks = now + Stopwatch.Frequency * SampleIntervalMs / 1000;

                FrameWindow w = clock.Snapshot(SnapshotFrames);
                if (w.Enough)
                {
                    current = w;
                    if (w.OnePercentLowFps > 0
                        && (worstLowFps <= 0 || w.OnePercentLowFps < worstLowFps))
                        worstLowFps = w.OnePercentLowFps;
                    if (w.OverBudgetShare > worstOverBudgetShare)
                        worstOverBudgetShare = w.OverBudgetShare;
                    if (w.MultiPresenter) multiPresenter = true;
                }

                if (budget != null)
                {
                    budget.Sample();
                    if (budget.ShouldDegrade)
                    {
                        degraded = true;
                        Logger.Log(Lang.T("log.framerate.2")
                            + ObserverBudget.Describe(budget.Last));
                        StopLocked();
                    }
                }
            }
        }

        public static FrameWindow Current { get { lock (lk) return current; } }

        public static bool Running { get { lock (lk) return clock != null; } }

        public static int ActivePid { get { lock (lk) return clock != null ? activePid : 0; } }

        public static string InstantText()
        {
            lock (lk)
            {
                if (degraded) return Lang.T("t.framerate.degraded");
                FrameWindow w = current;
                if (w == null || !w.Enough) return null;
                string s = w.Fps.ToString("F0") + " fps · "
                    + w.MedianMs.ToString("F1") + " ms";
                if (multiPresenter) s += " " + Lang.T("t.framerate.multi");
                return s;
            }
        }

        public static string Summarize()
        {
            lock (lk)
            {
                if (degraded) return Lang.T("rep.framerate.degraded");
                if (clock == null && current == null) return null;
                double seconds = startTicks > 0
                    ? (double)(Stopwatch.GetTimestamp() - startTicks) / Stopwatch.Frequency : 0;
                long frames = clock != null ? clock.TotalFrames - baseFrames : 0;
                if (seconds < MinSessionSeconds || frames < 100) return null;

                string s = Lang.F("rep.framerate.avg", (frames / seconds).ToString("F0"));
                if (worstLowFps > 0)
                    s += Lang.F("rep.framerate.low", worstLowFps.ToString("F0"));
                // 不足半个百分点会被四舍五入成 0 报一句"最高占 0%"是纯噪音
                if (worstOverBudgetShare >= 0.005)
                    s += Lang.F("rep.framerate.over",
                        (worstOverBudgetShare * 100).ToString("F0"));
                if (multiPresenter) s += Lang.T("rep.framerate.multi");
                return s;
            }
        }

        public static void Stop()
        {
            lock (lk) StopLocked();
        }

        private static void StartLocked(int pid)
        {
            double hz = 0;
            try { hz = DisplayGuard.CurrentRefreshRate(); }
            catch { hz = 0; }
            budgetMs = hz > 1 ? 1000.0 / hz : 0;

            var c = new FrameClock();
            if (!c.Start(pid, hz))
            {
                startFailed = true;
                Logger.Log(Lang.T("log.framerate.1"));
                return;
            }
            clock = c;
            activePid = pid;
            budget = new ObserverBudget("PaviseFrameClock");
            budget.Reset();
            long now = Stopwatch.GetTimestamp();
            startTicks = 0;
            baseFrames = 0;
            nextSampleTicks = now + Stopwatch.Frequency * SampleIntervalMs / 1000;
            widenAtTicks = now + Stopwatch.Frequency * WidenAfterMs / 1000;
            widenTried = false;
        }

        private static void StopLocked()
        {
            if (clock != null)
            {
                try { clock.Stop(); } catch { }
                clock = null;
            }
            budget = null;
            activePid = 0;
            startTicks = 0;
            baseFrames = 0;
            widenTried = false;
        }

        public static double BudgetMs { get { lock (lk) return budgetMs; } }

        public static ObserverCost Cost { get { lock (lk) return budget != null ? budget.Last : null; } }

        public static void HealFromCrash() { FrameClock.HealFromCrash(); }
    }
}
