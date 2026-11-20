using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    // 汇总放置前的渲染线程读数 供日志与调度窗口对照
    // 仅反映单个采集窗口 不参与准入或收益判定
    internal sealed class SchedulingBottleneckEvidence
    {
        // 每秒换核少于两次时按低迁移频率提示
        internal const double FewMigrationsPerSecond = 2;
        // 主要落点按线程运行时间占比筛选
        internal const double ProcessorShareFloor = .05;

        internal readonly bool Known, Queued;
        internal readonly bool NotCollected;
        internal readonly string Unknown;
        internal readonly double WindowMs, ReadyShare, ReadyP95Ms, RunningShare, MigrationsPerSecond;
        internal readonly int Processors;
        internal readonly DateTime ObservedLocal;

        private SchedulingBottleneckEvidence(string unknown, DateTime observedLocal, bool notCollected = false)
        { Unknown = unknown; ObservedLocal = observedLocal; NotCollected = notCollected; }

        private SchedulingBottleneckEvidence(ThreadRelationSnapshot relation, ThreadRelationMetrics role, DateTime observedLocal)
        {
            Known = true; ObservedLocal = observedLocal; WindowMs = relation.DurationMs;
            string ignored;
            Queued = UnifiedThreadPressurePolicy.Accept(relation, role, out ignored);
            ReadyShare = role.ReadyMs / relation.DurationMs;
            ReadyP95Ms = role.ReadyIntervals > 0 ? role.ReadyP95Ms : 0;
            RunningShare = role.RunningMs / relation.DurationMs;
            MigrationsPerSecond = role.ProcessorMigrations * 1000.0 / relation.DurationMs;
            if (role.CpuRunningMs != null && role.RunningMs > 0)
                foreach (KeyValuePair<int, double> cpu in role.CpuRunningMs)
                    if (cpu.Value >= role.RunningMs * ProcessorShareFloor) Processors++;
        }

        internal static SchedulingBottleneckEvidence From(ThreadRelationSnapshot relation, HardThreadIdentity presenter, DateTime observedLocal, bool presentIdentityOnly = false)
        {
            if (presentIdentityOnly)
                return new SchedulingBottleneckEvidence(Logger.Text("本次仅识别呈现角色，调度关系、CPU排队与换核分布未采集", "this capture identified the presenting role only; scheduling relationships, CPU queueing and core-migration distribution were not collected"), observedLocal, true);
            if (relation == null || presenter == null)
                return new SchedulingBottleneckEvidence(Logger.Text("首次采集没有可用的线程关系读数", "the first capture has no usable thread-relation readings"), observedLocal);
            if (!relation.Valid || relation.LostEvents != 0)
                return new SchedulingBottleneckEvidence(Logger.Text("线程关系采样无效或有事件丢失", "thread-relation sample invalid or events lost"), observedLocal);
            ThreadRelationMetrics role = relation.Presenter;
            if (role == null || role.Tid != presenter.Tid || role.Creation != presenter.Creation)
                return new SchedulingBottleneckEvidence(Logger.Text("采样中的呈现线程与准入身份不一致", "presenting thread in the sample does not match the admitted identity"), observedLocal);
            if (role.StateConflicts != 0)
                return new SchedulingBottleneckEvidence(Logger.Text("渲染线程的调度状态存在歧义", "render thread scheduling state is ambiguous"), observedLocal);
            if (!Finite(relation.DurationMs) || relation.DurationMs <= 0 || !Finite(role.KnownMs) || !Finite(role.ReadyMs) || !Finite(role.RunningMs)
                || role.ReadyMs < 0 || role.RunningMs < 0 || role.ProcessorMigrations < 0
                || role.ReadyMs + role.RunningMs > role.KnownMs + .000001 || role.KnownMs > relation.DurationMs + .000001
                || role.ReadyIntervals > 0 && (!Finite(role.ReadyP95Ms) || role.ReadyP95Ms < 0))
                return new SchedulingBottleneckEvidence(Logger.Text("渲染线程读数不是可核验的有限值", "render thread readings are not verifiable finite values"), observedLocal);
            if (role.KnownMs / relation.DurationMs < .9)
                return new SchedulingBottleneckEvidence(Logger.Text("渲染线程的状态覆盖不足窗口的九成", "render thread state covers less than 90% of the window"), observedLocal);
            return new SchedulingBottleneckEvidence(relation, role, observedLocal);
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Percent(double value) { return (100 * value).ToString("F1", CultureInfo.InvariantCulture) + "%"; }
        private static string Number(double value, string format) { return value.ToString(format, CultureInfo.InvariantCulture); }

        // 日志保留完整读数与判据结果
        internal string Describe()
        {
            if (NotCollected) return Logger.Text("放置前观察：", "pre-placement observation: ") + Unknown;
            if (!Known) return Logger.Text("放置前观察不可用：", "pre-placement observation unavailable: ") + Unknown;
            return Logger.Text("放置前观察：渲染线程排队占窗口 ", "pre-placement observation: render thread queueing share of window ") + Percent(ReadyShare) + Logger.Text("，P95 ", ", P95 ") + Number(ReadyP95Ms, "F3") + Logger.Text(" 毫秒，", " ms, ")
                + (Queued ? Logger.Text("已达到", "sustained queueing threshold reached") : Logger.Text("未达到", "sustained queueing threshold not reached")) + Logger.Text("持续排队门槛；占用单核 ", "; single-core usage ") + Percent(RunningShare)
                + Logger.Text("；换核 ", "; migrations ") + Number(MigrationsPerSecond, "F1") + Logger.Text(" 次/秒，主要落在 ", " per second, mostly on ") + Processors + Logger.Text(" 个逻辑处理器", " logical processors")
                + Logger.Text("；窗口 ", "; window ") + Number(WindowMs / 1000, "F1") + Logger.Text(" 秒；仅为首次采集的读数，不代表整局，也不是收益结论", " s; first-capture readings only, not representative of the whole match and not a benefit conclusion");
        }

        // 界面按所选方案显示读数说明
        internal string Hint(SchedulingStrategy strategy)
        {
            string time = ObservedLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (NotCollected)
                return PerformancePolicy.Text("最近一次对局（" + time + "）仅识别呈现角色；调度关系、CPU 排队和换核分布未采集，不能据此判断有无 CPU 瓶颈。",
                    "The most recent session (" + time + ") identified the presenting thread only. Scheduling relationships, CPU queueing and processor migration were not collected; CPU bottlenecks cannot be assessed from this sample.");
            if (!Known)
                return PerformancePolicy.Text("最近一次对局（" + time + "）的首次采样没有可用的渲染线程读数。",
                    "The first sample of the most recent session (" + time + ") has no usable render thread readings.");
            string chinese, english;
            if (strategy == SchedulingStrategy.GamePriority)
            {
                chinese = (Queued ? "渲染线程排队达到持续门槛，这类等待出现过；" : "渲染线程排队没有达到持续门槛；") + "游戏其他线程是否等待 CPU 不在这次读数里。";
                english = (Queued ? "Render-thread queueing reached the sustained threshold, so this kind of wait did occur." : "Render-thread queueing stayed below the sustained threshold.")
                    + " Whether other game threads waited for CPU is not part of this reading.";
            }
            else if (strategy == SchedulingStrategy.RenderBoost || strategy == SchedulingStrategy.RenderCoreTimeCritical)
            {
                chinese = Queued ? "排队达到持续门槛，这类等待出现过。" : "排队没有达到持续门槛，提高优先级可能看不出差别。";
                english = Queued ? "Queueing reached the sustained threshold, so this kind of wait did occur."
                    : "Queueing stayed below the sustained threshold, so higher priority may make no visible difference.";
            }
            else if (strategy == SchedulingStrategy.RenderCore)
            {
                bool few = MigrationsPerSecond < FewMigrationsPerSecond;
                chinese = few ? "换核本来就很少，固定核心能减少的迁移有限。" : "换核次数供对照，是否影响帧时间需实测。";
                english = few ? "Migration was already rare, so a fixed core has little left to remove."
                    : "Use the migration rate for comparison; its effect on frame time needs testing.";
            }
            else
            {
                chinese = "这种方案另按全线程工作图判断，以上只是渲染线程的读数。";
                english = "This strategy is judged from the full thread graph; the figures above cover the render thread only.";
            }
            return PerformancePolicy.Text(
                "最近一次对局首次采样（" + time + "）：渲染线程排队 " + Percent(ReadyShare) + "，P95 " + Number(ReadyP95Ms, "F2") + " 毫秒；占用单核 "
                    + Percent(RunningShare) + "；换核 " + Number(MigrationsPerSecond, "F1") + " 次/秒。" + chinese + "仅供对照，不代表整局。",
                "First sample of the most recent session (" + time + "): render thread queued " + Percent(ReadyShare) + ", P95 " + Number(ReadyP95Ms, "F2")
                    + " ms; used " + Percent(RunningShare) + " of one core; " + Number(MigrationsPerSecond, "F1") + " migrations/s. "
                    + english + " For comparison only; not the whole session.");
        }
    }
}
