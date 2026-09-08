// @author bdth 2074055628@qq.com
// 文件用途 系统级中断观测和已证明游戏核归因的边界回归 全是合成数据 不起 ETW
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestIrqObservationVerdictProvenance()
        {
            TestIrqObservationRawRecordsStayVisible();
            TestIrqObservationCannotSupplyMissingScopedSession();
            TestIrqObservationCannotChangeScopedEvidence();
        }

        private static void TestIrqObservationRawRecordsStayVisible()
        {
            // 新的系统观测拿 0 表示游戏核未知 旧的全核记录也只能展示 不能提挪核建议
            foreach (ulong observedMask in new ulong[] { 0UL, 0xFFFFUL })
            {
                var records = new List<IrqSessionRecord>();
                for (int i = 0; i < 3; i++)
                    records.Add(SessionWithMask(300, observedMask,
                        Rec("observation.sys", 10000, 9000000, 4000, 900, 0x1UL)));

                int used;
                List<IrqDriverVerdict> verdicts = IrqVerdict.Evaluate(records, 60, out used);
                Eq(3, used);
                Eq(1, verdicts.Count);
                IrqDriverVerdict verdict = verdicts[0];
                Eq("observation.sys", verdict.Driver);
                Eq(3, verdict.SessionsSeen);
                Eq(2700L, verdict.TotalOver500);
                Eq(4000.0, verdict.WorstMaxUs);
                Eq(2000.0, verdict.DpcPerMinute);
                Eq(0x1UL, verdict.CpuMask);
                Eq(0, verdict.SessionsOverThreshold);
                Eq(0L, verdict.OverlapOver500);
                Eq(0UL, verdict.OverlapCpuMask);
                Eq(0.0, verdict.OverlapWorstMaxUs);
                Eq(0.0, verdict.ScoredSeconds);
                Eq(0.0, verdict.Collisions);
                Eq(false, verdict.StructuralConflict);
                Eq(false, verdict.Worth);

                string summary = IrqVerdict.SummarizeSession(records[0]);
                if (string.IsNullOrEmpty(summary)
                    || summary.IndexOf("observation.sys", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new Exception("Unscoped IRQ observation lost its visible driver summary");
            }
        }

        private static void TestIrqObservationCannotSupplyMissingScopedSession()
        {
            // 手上只有两局严格证据 全系统慢 DPC 再多也凑不出三局游戏核冲突
            var records = new List<IrqSessionRecord>();
            for (int i = 0; i < 2; i++)
                records.Add(SessionWithMask(300, 0xFFUL,
                    Rec("mixed-observation.sys", 100000, 9000000, 4000, 900, 0x1UL)));
            foreach (ulong observedMask in new ulong[] { 0UL, 0xFFFFUL, 0UL })
                records.Add(SessionWithMask(3600, observedMask,
                    Rec("mixed-observation.sys", 1000000, 1000000000, 40000, 50000, 0x1UL)));

            int used;
            List<IrqDriverVerdict> verdicts = IrqVerdict.Evaluate(records, 60, out used);
            Eq(5, used);
            Eq(1, verdicts.Count);
            IrqDriverVerdict verdict = verdicts[0];
            Eq(5, verdict.SessionsSeen);
            Eq(151800L, verdict.TotalOver500);
            Eq(40000.0, verdict.WorstMaxUs);
            Eq(2, verdict.SessionsOverThreshold);
            Eq(1800L, verdict.OverlapOver500);
            Eq(4000.0, verdict.OverlapWorstMaxUs);
            Eq(600.0, verdict.ScoredSeconds);
            Eq(false, verdict.StructuralConflict);
            Eq(false, verdict.Worth);
        }

        private static void TestIrqObservationCannotChangeScopedEvidence()
        {
            var scoped = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                scoped.Add(SessionWithMask(300, 0xFFUL,
                    Rec("scoped-observation.sys", 100000, 9000000, 4000, 900, 0x1UL)));

            int used;
            List<IrqDriverVerdict> baseline = IrqVerdict.Evaluate(scoped, 60, out used);
            Eq(3, used);
            Eq(1, baseline.Count);
            Eq(true, baseline[0].Worth);
            Eq(true, baseline[0].StructuralConflict);

            // 系统记录可以进展示窗口 但它那些长时长 慢尖峰和更多 CPU 不许污染归因统计
            var mixed = new List<IrqSessionRecord>(scoped);
            foreach (ulong observedMask in new ulong[] { 0UL, 0xFFFFUL })
                mixed.Add(SessionWithMask(3600, observedMask,
                    Rec("scoped-observation.sys", 1000000, 1000000000, 80000, 50000, 0xFFFFUL)));
            List<IrqDriverVerdict> verdicts = IrqVerdict.Evaluate(mixed, 60, out used);
            Eq(5, used);
            Eq(1, verdicts.Count);
            IrqDriverVerdict verdict = verdicts[0];
            Eq(5, verdict.SessionsSeen);
            Eq(102700L, verdict.TotalOver500);
            Eq(80000.0, verdict.WorstMaxUs);
            Eq(0xFFFFUL, verdict.CpuMask);
            Eq(baseline[0].SessionsOverThreshold, verdict.SessionsOverThreshold);
            Eq(baseline[0].OverlapOver500, verdict.OverlapOver500);
            Eq(baseline[0].OverlapWorstMaxUs, verdict.OverlapWorstMaxUs);
            Eq(baseline[0].OverlapCpuMask, verdict.OverlapCpuMask);
            Eq(baseline[0].ScoredSeconds, verdict.ScoredSeconds);
            Eq(baseline[0].Collisions, verdict.Collisions);
            Eq(true, verdict.StructuralConflict);
            Eq(true, verdict.Worth);
        }

        private const int ObservationRendererPid = 7107;
        private const long ObservationRendererCreation = 1001;
        private const ulong ObservationSystemMask = 0xFFFFUL;
        private const ulong ObservationStrictMask = 0xFUL;

        private sealed class ObservationTestCapture : IIrqSessionCapture
        {
            internal bool StartResult = true;
            internal bool BusyValue;
            internal string Failure = "";
            internal int StartCalls;
            internal int StopCalls;
            internal bool Truncated;
            internal bool TimelineRequested;
            internal Action OnStop;
            internal readonly InterruptAttributionResult Result = new InterruptAttributionResult();
            internal readonly List<InterruptAttribution.DpcTimelineEntry> Timeline =
                new List<InterruptAttribution.DpcTimelineEntry>();

            internal ObservationTestCapture()
            {
                Result.Ok = true;
                Result.Drivers.Add(new DriverInterrupt
                {
                    Driver = "observation.sys",
                    Dpc = 10000,
                    DpcTotalUs = 9000000,
                    DpcMaxUs = 4000,
                    DpcOver500Us = 900,
                    DpcOver1Ms = 100,
                    CpuMask = 0x1UL
                });
                Timeline.Add(new InterruptAttribution.DpcTimelineEntry
                {
                    StartQpc = 100,
                    EndQpc = 200,
                    Module = "observation.sys",
                    Cpu = 0,
                    DpcUs = 100
                });
            }

            public bool Busy { get { return BusyValue; } }
            public string FailDetail { get { return Failure; } }
            public bool Start() { StartCalls++; return StartResult; }
            public InterruptAttributionResult Stop()
            {
                StopCalls++;
                if (OnStop != null) OnStop();
                return Result;
            }
            public List<InterruptAttribution.DpcTimelineEntry> DpcTimeline
            { get { return TimelineRequested ? Timeline : null; } }
            public bool DpcTimelineTruncated { get { return TimelineRequested && Truncated; } }
        }

        private sealed class ObservationTestPlatform : IIrqSessionPlatform
        {
            internal bool EnabledValue = true;
            internal bool ElevatedValue = true;
            internal bool AppendResult = true;
            internal long Now = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc).Ticks;
            internal string LastResult = "";
            internal int AppendCalls;
            internal readonly Queue<ObservationTestCapture> Queued = new Queue<ObservationTestCapture>();
            internal readonly List<ObservationTestCapture> Captures = new List<ObservationTestCapture>();
            internal readonly List<IrqSessionRecord> Records = new List<IrqSessionRecord>();
            internal readonly List<string> Messages = new List<string>();

            public bool Enabled { get { return EnabledValue; } }
            public bool IsElevated { get { return ElevatedValue; } }
            public long UtcTicks { get { return Now; } }
            public string BootStamp { get { return "test-boot"; } }
            public string TopologyStamp { get { return "test-topology"; } }
            internal Func<ICoreLoadSource> CoreLoadFactory;
            public ICoreLoadSource OpenCoreLoadSource() { return CoreLoadFactory == null ? null : CoreLoadFactory(); }
            public IIrqSessionCapture CreateCapture(bool captureTimeline)
            {
                ObservationTestCapture capture = Queued.Count == 0
                    ? new ObservationTestCapture() : Queued.Dequeue();
                capture.TimelineRequested = captureTimeline;
                Captures.Add(capture);
                return capture;
            }
            public bool Append(IrqSessionRecord record)
            {
                AppendCalls++;
                if (!AppendResult) return false;
                Records.Add(record);
                return true;
            }
            public Func<string, string> DriverVersionReader() { return delegate { return "test-version"; }; }
            public void Log(string message) { Messages.Add(message); }
            public string LoadLastResult() { return LastResult; }
            public void SaveLastResult(string result) { LastResult = result; }
        }

        private static void TestIrqObservationLifecycle()
        {
            RunIrqObservationCase("observation routing", TestIrqObservationRouting);
            RunIrqObservationCase("capture and delayed commit", TestIrqObservationDelayedCommit);
            RunIrqObservationCase("grace rearm", TestIrqObservationGraceRearm);
            RunIrqObservationCase("identity guards", TestIrqObservationIdentityGuards);
            RunIrqObservationCase("external mutation", TestIrqObservationMutationBoundary);
            RunIrqObservationCase("stale proof", TestIrqObservationStaleProof);
            RunIrqObservationCase("mode separation", TestIrqObservationModeSeparation);
            RunIrqObservationCase("start and capture failures", TestIrqObservationFailures);
            RunIrqObservationCase("disable and dispose", TestIrqObservationDisableAndDispose);
            RunIrqObservationCase("rearm during stop", TestIrqObservationRearmDuringStop);
        }

        private static void TestIrqEveryMatchFallback()
        {
            RunIrqObservationCase("bounded placement initialization", TestIrqFallbackInitialization);
            RunIrqObservationCase("strict success resets initialization", TestIrqFallbackPreservesStrict);
            RunIrqObservationCase("invalidated strict epoch becomes unscoped", TestIrqFallbackInvalidatedEpoch);
            RunIrqObservationCase("sealed and disabled fallback guards", TestIrqFallbackGuards);
            RunIrqObservationCase("nested mutations do not consume fallback scans", TestIrqFallbackNestedMutations);
            RunIrqObservationCase("fallback cannot race stop", TestIrqFallbackDuringStop);
            RunIrqObservationCase("short matches keep real measurements", TestIrqShortMatchMeasurements);
        }

        private static void TestIrqFallbackInitialization()
        {
            Eq(3, IrqSessionProbe.PlacementInitializationScans);
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("initializing", ObservationSystemMask);
                for (int i = 1; i < IrqSessionProbe.PlacementInitializationScans; i++)
                {
                    Eq(false, probe.TryFallbackToSystemObservation("initializing", ObservationSystemMask));
                    Eq(true, probe.RequiresPlacementAudit);
                    Eq(false, probe.IsSystemObservation);
                    Eq(0, platform.Captures.Count);
                }

                // 新局不继承上一局的等待次数 重新给一次完整的初始化机会
                probe.Arm("new match", ObservationSystemMask);
                for (int i = 1; i < IrqSessionProbe.PlacementInitializationScans; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("new match", ObservationSystemMask));
                long strictEpoch = probe.CaptureEpoch;
                Eq(true, probe.TryFallbackToSystemObservation("new match", ObservationSystemMask));
                Eq(true, probe.CaptureEpoch > strictEpoch);
                Eq(true, probe.IsSystemObservation);
                Eq(false, probe.RequiresPlacementAudit);
                Eq(true, probe.CanObserveSystemNow);
                Eq(0, platform.Captures.Count);

                // 降级只是换证据级别 照样要真实 renderer 身份确认过才开始采
                Eq(false, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                for (int i = 0; i < 6; i++)
                {
                    Eq(false, probe.TryFallbackToSystemObservation("new match", ObservationSystemMask));
                    AdvanceIrqObservation(platform, probe, 1, false);
                }
                Eq(1, platform.Captures.Count);
                Eq(false, platform.Captures[0].TimelineRequested);
                Eq(false, string.IsNullOrEmpty(probe.TakeSummary()));
                Eq(1, platform.Records.Count);
                Eq("new match", platform.Records[0].GameName);
                Eq(0UL, platform.Records[0].GameMask);
                Eq(6, platform.Records[0].DurationSeconds);
            }
        }

        private static void TestIrqFallbackPreservesStrict()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("strict proof available", ObservationSystemMask);
                // 每次成功起采都把等待计数清零 短暂重布防几次不能攒成超时降级
                for (int restart = 0; restart < 4; restart++)
                {
                    for (int scan = 1; scan < IrqSessionProbe.PlacementInitializationScans; scan++)
                        Eq(false, probe.TryFallbackToSystemObservation(
                            "strict proof available", ObservationSystemMask));
                    Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                        ObservationRendererPid, ObservationRendererCreation));
                    Eq(true, probe.IsPlacementCapturing);
                    Eq(false, probe.IsSystemObservation);
                    for (int liveScan = 0; liveScan < 4; liveScan++)
                    {
                        Eq(false, probe.TryFallbackToSystemObservation(
                            "strict proof available", ObservationSystemMask));
                        AdvanceIrqObservation(platform, probe, 1, true);
                    }
                    if (restart < 3)
                    {
                        probe.BeginExternalMutation();
                        probe.EndExternalMutation();
                        Eq(false, probe.IsCapturing);
                    }
                }
                AdvanceIrqObservation(platform, probe, 60, true);
                Eq(false, string.IsNullOrEmpty(probe.TakeSummary()));
                Eq(4, platform.Captures.Count);
                Eq(1, platform.Records.Count);
                Eq(ObservationStrictMask, platform.Records[0].GameMask);
                Eq(64, platform.Records[0].DurationSeconds);
                Eq(false, probe.TryFallbackToSystemObservation("saved strict", ObservationSystemMask));
                bool truncated;
                Eq(false, probe.TakeDpcTimeline(out truncated) == null);
                Eq(false, truncated);
            }
        }

        private static void TestIrqFallbackInvalidatedEpoch()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("proof later unavailable", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 61, true);
                Eq(true, platform.Captures[0].TimelineRequested);
                probe.InvalidateGameMask();
                Eq(1, platform.Captures[0].StopCalls);
                Eq(0, platform.AppendCalls);
                long discardedEpoch = probe.CaptureEpoch;
                // 已经知道严格证据失效就不用再等三轮 旧的 61 秒和逐事件时间线不能补写成系统记录
                Eq(true, probe.TryFallbackToSystemObservation("proof later unavailable", ObservationSystemMask));
                Eq(true, probe.CaptureEpoch > discardedEpoch);
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(false, platform.Captures[1].TimelineRequested);
                AdvanceIrqObservation(platform, probe, 7, false);
                Eq(false, string.IsNullOrEmpty(probe.TakeSummary()));
                Eq(1, platform.Records.Count);
                Eq(7, platform.Records[0].DurationSeconds);
                Eq(platform.Now - 7 * TimeSpan.TicksPerSecond, platform.Records[0].StartUtcTicks);
                Eq(0UL, platform.Records[0].GameMask);
                bool truncated;
                Eq(true, probe.TakeDpcTimeline(out truncated) == null);
                Eq(false, truncated);
                Eq(2, platform.Captures.Count);
            }
        }

        private static void TestIrqFallbackGuards()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("sealed strict", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 5, true);
                probe.Seal();
                for (int i = 0; i < 6; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("sealed strict", ObservationSystemMask));
                Eq(true, probe.HasSealedPending);
                Eq(false, probe.IsSystemObservation);
                Eq(0, platform.AppendCalls);
                Eq(false, string.IsNullOrEmpty(probe.TakeSummary()));
                Eq(ObservationStrictMask, platform.Records[0].GameMask);
                Eq(false, probe.TryFallbackToSystemObservation("already saved", ObservationSystemMask));
            }

            var disabled = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(disabled))
            {
                probe.Arm("disabled while waiting", ObservationSystemMask);
                disabled.EnabledValue = false;
                for (int i = 0; i < 6; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("disabled while waiting", ObservationSystemMask));
                Eq(0, disabled.Captures.Count);
                Eq(0, disabled.AppendCalls);
                Eq(true, probe.TakeSummary() == null);
            }

            var unavailable = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(unavailable))
            {
                probe.Arm("missing system mask", ObservationSystemMask);
                for (int i = 0; i < 6; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("missing system mask", 0UL));
                Eq(false, probe.IsSystemObservation);
                probe.Dispose();
                Eq(false, probe.TryFallbackToSystemObservation("disposed", ObservationSystemMask));
                Eq(0, unavailable.Captures.Count);
            }
        }

        private static void TestIrqFallbackDuringStop()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("stop in progress", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 5, true);
                platform.Captures[0].OnStop = delegate
                {
                    // 模拟 Stop 还没返回 异步调优就把旧 epoch 弄失效了
                    // 这时候虽然回到 waiting 也不能再起第二个系统采集器
                    probe.BeginExternalMutation();
                    probe.EndExternalMutation();
                    for (int i = 0; i < 6; i++)
                        Eq(false, probe.TryFallbackToSystemObservation("stop in progress", ObservationSystemMask));
                };
                probe.Seal();
                Eq(false, probe.HasSealedPending);
                Eq(0, platform.AppendCalls);
                Eq(1, platform.Captures.Count);
                for (int i = 1; i < IrqSessionProbe.PlacementInitializationScans; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("stop completed", ObservationSystemMask));
                Eq(true, probe.TryFallbackToSystemObservation("stop completed", ObservationSystemMask));
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 2, false);
                probe.TakeSummary();
                Eq(1, platform.Records.Count);
                Eq(2, platform.Records[0].DurationSeconds);
                Eq(0UL, platform.Records[0].GameMask);
            }
        }

        private static void TestIrqFallbackNestedMutations()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("nested strict mutation", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 4, true);
                probe.BeginExternalMutation();
                probe.BeginExternalMutation();
                Eq(1, platform.Captures[0].StopCalls);
                for (int i = 0; i < 6; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("nested strict mutation", ObservationSystemMask));
                probe.EndExternalMutation();
                for (int i = 0; i < 6; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("nested strict mutation", ObservationSystemMask));
                Eq(false, probe.IsSystemObservation);
                Eq(1, platform.Captures.Count);
                probe.EndExternalMutation();
                // 写区里的扫描不吃配额 最外层写区结束之后还得连续三轮没起采才降级
                for (int i = 1; i < IrqSessionProbe.PlacementInitializationScans; i++)
                    Eq(false, probe.TryFallbackToSystemObservation("nested strict mutation", ObservationSystemMask));
                Eq(true, probe.TryFallbackToSystemObservation("nested strict mutation", ObservationSystemMask));
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                probe.BeginExternalMutation();
                probe.BeginExternalMutation();
                AdvanceIrqObservation(platform, probe, 4, false);
                Eq(true, probe.IsCapturing);
                Eq(0, platform.Captures[1].StopCalls);
                probe.EndExternalMutation();
                probe.EndExternalMutation();
                probe.TakeSummary();
                Eq(2, platform.Captures.Count);
                Eq(1, platform.Records.Count);
                Eq(4, platform.Records[0].DurationSeconds);
                Eq(0UL, platform.Records[0].GameMask);
            }
        }

        private static void TestIrqShortMatchMeasurements()
        {
            foreach (bool strict in new bool[] { false, true })
            foreach (int seconds in new int[] { 1, 17, 59 })
            {
                var platform = new ObservationTestPlatform();
                var capture = new ObservationTestCapture();
                capture.Result.Drivers[0].Dpc = 23;
                capture.Result.Drivers[0].DpcTotalUs = 4567;
                capture.Result.Drivers[0].DpcMaxUs = 1234;
                capture.Result.Drivers[0].DpcOver500Us = 2;
                capture.Result.Drivers[0].DpcOver1Ms = 1;
                platform.Queued.Enqueue(capture);
                using (var probe = new IrqSessionProbe(platform))
                {
                    probe.Arm("short real capture", ObservationSystemMask, !strict);
                    Eq(true, strict
                        ? probe.ConfirmGameMask(ObservationStrictMask,
                            ObservationRendererPid, ObservationRendererCreation)
                        : probe.ConfirmSystemObservation(
                            ObservationRendererPid, ObservationRendererCreation));
                    AdvanceIrqObservation(platform, probe, seconds, strict);
                    probe.Seal();
                    platform.Now += 8 * TimeSpan.TicksPerSecond;
                    string summary = probe.TakeSummary();
                    Eq(false, string.IsNullOrEmpty(summary));
                    Eq(1, platform.AppendCalls);
                    Eq(1, platform.Records.Count);
                    IrqSessionRecord record = platform.Records[0];
                    Eq(seconds, record.DurationSeconds);
                    Eq(strict ? ObservationStrictMask : 0UL, record.GameMask);
                    Eq(23L, record.Drivers[0].Dpc);
                    Eq(4567000L, record.Drivers[0].DpcTotalNs);
                    Eq(1234000L, record.Drivers[0].DpcMaxNs);
                    Eq(2L, record.Drivers[0].Over500Us);
                    Eq(1L, record.Drivers[0].Over1Ms);
                    // 存下真实短局不代表归因门槛可以放松 更不能给它补成 60 秒
                    Eq(IrqSessionExclusion.TooShort,
                        record.VerdictExclusion(platform.BootStamp, platform.TopologyStamp));
                    Eq(summary, GameMode.FormatIrqSessionResult(summary, true, "must not replace measurements"));
                }
            }
        }

        private static void TestIrqSessionFailureSummary()
        {
            Eq("", GameMode.FormatIrqSessionResult(null, false, "previous match failure"));
            Eq("", GameMode.FormatIrqSessionResult("", false, "previous match failure"));
            Eq("", GameMode.FormatIrqSessionResult(null, true, null));
            Eq("", GameMode.FormatIrqSessionResult(null, true, ""));
            Eq("actual measurements", GameMode.FormatIrqSessionResult(
                "actual measurements", false, "must not replace successful summary"));

            for (int failure = 0; failure < 10; failure++)
            {
                int caseId = failure;
                RunIrqObservationCase("per-match failure summary " + caseId, delegate
                { TestIrqSessionFailureSummaryCase(caseId); });
            }
        }

        private static void TestIrqSessionFailureSummaryCase(int failure)
        {
            var platform = new ObservationTestPlatform();
            var capture = new ObservationTestCapture();
            if (failure == 1) platform.ElevatedValue = false;
            if (failure == 2 || failure == 3)
            {
                capture.StartResult = false;
                capture.BusyValue = failure == 3;
                capture.Failure = "fake ETW start failure";
            }
            if (failure == 4) capture.Result.EventsLost = 1;
            if (failure == 5) capture.Result.Incomplete = true;
            if (failure == 6) capture.Result.Drivers.Clear();
            if (failure == 7) platform.AppendResult = false;
            platform.Queued.Enqueue(capture);
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("failed game", ObservationSystemMask);
                if (failure != 0)
                {
                    bool started = probe.ConfirmGameMask(ObservationStrictMask,
                        ObservationRendererPid, ObservationRendererCreation);
                    Eq(failure >= 4, started);
                    if (started) AdvanceIrqObservation(platform, probe, 5, true);
                }
                if (failure == 8) probe.InvalidateGameMask();
                if (failure == 9) platform.EnabledValue = false;
                string summary = probe.TakeSummary();
                Eq(true, summary == null);
                Eq(0, platform.Records.Count);
                Eq(failure == 7 ? 1 : 0, platform.AppendCalls);
                string status = probe.StatusText;
                Eq(false, string.IsNullOrEmpty(status));
                string rendered = GameMode.FormatIrqSessionResult(summary, true, status);
                Eq(Lang.F("rep.irq.result", status), rendered);
                // 不能只吐一个缺失的本地化 key 更不能把默认 fake 指标包装成成功摘要
                Eq(true, rendered.IndexOf(status, StringComparison.Ordinal) >= 0);
                Eq(false, rendered.IndexOf("observation.sys", StringComparison.OrdinalIgnoreCase) >= 0);
                Eq("", GameMode.FormatIrqSessionResult(summary, false, status));

                // invalidated 属于允许转系统的那种证明失败
                // 其余终止状态都不能靠 fallback 每轮重新去要 ETW 权限不足 已占用 丢事件 保存失败都算
                if (failure != 8)
                {
                    int captures = platform.Captures.Count;
                    for (int i = 0; i < 6; i++)
                    {
                        Eq(false, probe.TryFallbackToSystemObservation("failed game", ObservationSystemMask));
                        Eq(false, probe.ConfirmGameMask(ObservationStrictMask,
                            ObservationRendererPid, ObservationRendererCreation));
                        Eq(false, probe.ConfirmSystemObservation(
                            ObservationRendererPid, ObservationRendererCreation));
                    }
                    Eq(captures, platform.Captures.Count);
                    Eq(0, platform.Records.Count);
                    Eq(failure == 7 ? 1 : 0, platform.AppendCalls);
                    Eq(true, probe.TakeSummary() == null);
                }
            }
        }

        private static void TestIrqObservationRouting()
        {
            Eq(true, GameMode.IrqLaneNeedsInitialization(LaneState.Idle));
            Eq(false, GameMode.IrqLaneNeedsInitialization(LaneState.Trying));
            Eq(false, GameMode.IrqLaneNeedsInitialization(LaneState.Engaged));
            Eq(false, GameMode.IrqLaneNeedsInitialization(LaneState.Unavailable));
            Eq(false, GameMode.NeedsSystemIrqObservation(true, false, false,
                ObservationStrictMask, ObservationSystemMask));
            Eq(true, GameMode.NeedsSystemIrqObservation(false, false, false,
                ObservationStrictMask, ObservationSystemMask));
            Eq(true, GameMode.NeedsSystemIrqObservation(true, true, false,
                ObservationStrictMask, ObservationSystemMask));
            Eq(true, GameMode.NeedsSystemIrqObservation(true, false, true,
                ObservationStrictMask, ObservationSystemMask));
            Eq(true, GameMode.NeedsSystemIrqObservation(true, false, false,
                0UL, ObservationSystemMask));
            Eq(true, GameMode.NeedsSystemIrqObservation(true, false, false,
                0x10000UL, ObservationSystemMask));
            Eq(false, IrqSessionProbe.CanConfirmMask(ObservationSystemMask, ObservationSystemMask,
                ObservationRendererPid, ObservationRendererCreation));

            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                bool observeSystem = GameMode.NeedsSystemIrqObservation(true, false, false,
                    ObservationSystemMask, ObservationSystemMask);
                Eq(true, observeSystem);
                probe.Arm("unlimited cores route", ObservationSystemMask, observeSystem);
                Eq(false, probe.RequiresPlacementAudit);
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(1, platform.Captures.Count);
                Eq(false, platform.Captures[0].TimelineRequested);
                Eq(true, probe.IsCapturing);
                Eq(false, probe.IsPlacementCapturing);
            }
        }

        private static void RunIrqObservationCase(string name, Action test)
        {
            try { test(); }
            catch (Exception ex) { throw new Exception("IRQ observation " + name + ": " + ex.Message, ex); }
        }

        private static void AdvanceIrqObservation(ObservationTestPlatform platform,
            IrqSessionProbe probe, int seconds, bool strict)
        {
            // 虚拟时钟续证 不等真实时间 也不枚举线程和读真实进程
            for (int i = 0; i < seconds; i++)
            {
                platform.Now += TimeSpan.TicksPerSecond;
                bool accepted = strict
                    ? probe.ConfirmGameMask(ObservationStrictMask,
                        ObservationRendererPid, ObservationRendererCreation)
                    : probe.ConfirmSystemObservation(
                        ObservationRendererPid, ObservationRendererCreation);
                if (!accepted) throw new Exception("Fresh fake renderer proof was rejected at second " + i);
            }
        }

        private static void StartIrqObservation(ObservationTestPlatform platform,
            IrqSessionProbe probe, string game)
        {
            probe.Arm(game, ObservationSystemMask, true);
            Eq(true, probe.IsSystemObservation);
            Eq(false, probe.RequiresPlacementAudit);
            Eq(false, probe.IsCapturing);
            Eq(true, probe.ConfirmSystemObservation(
                ObservationRendererPid, ObservationRendererCreation));
            Eq(true, probe.IsCapturing);
            Eq(false, probe.IsPlacementCapturing);
            Eq(false, platform.Captures[platform.Captures.Count - 1].TimelineRequested);
            if (string.IsNullOrEmpty(probe.StatusText))
                throw new Exception("An active system observation had no status text");
        }

        private static void TestIrqObservationDelayedCommit()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("all cores, no render lane", ObservationSystemMask, true);
                Eq(0, platform.Captures.Count);
                Eq(false, probe.RequiresPlacementAudit);
                Eq(false, probe.IsCapturing);
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(1, platform.Captures.Count);
                Eq(1, platform.Captures[0].StartCalls);
                Eq(false, platform.Captures[0].TimelineRequested);
                Eq(true, probe.IsCapturing);
                Eq(false, probe.IsPlacementCapturing);
                AdvanceIrqObservation(platform, probe, 65, false);

                probe.Seal();
                Eq(false, probe.IsCapturing);
                Eq(true, probe.HasSealedPending);
                Eq(1, platform.Captures[0].StopCalls);
                Eq(0, platform.AppendCalls);
                platform.Now += 8 * TimeSpan.TicksPerSecond;
                string summary = probe.TakeSummary();
                if (string.IsNullOrEmpty(summary)) throw new Exception("A complete observation had no summary");
                Eq(1, platform.AppendCalls);
                Eq(1, platform.Records.Count);
                Eq(65, platform.Records[0].DurationSeconds);
                Eq("all cores, no render lane", platform.Records[0].GameName);
                Eq(0UL, platform.Records[0].GameMask);
                Eq(ObservationSystemMask, platform.Records[0].SystemMask);
                Eq(platform.BootStamp, platform.Records[0].BootStamp);
                Eq(platform.TopologyStamp, platform.Records[0].TopologyStamp);
                Eq("test-version", platform.Records[0].Drivers[0].DriverVersion);
                Eq(false, probe.HasSealedPending);

                bool truncated;
                List<InterruptAttribution.DpcTimelineEntry> timeline = probe.TakeDpcTimeline(out truncated);
                Eq(true, timeline == null);
                Eq(false, truncated);
                Eq(true, probe.TakeDpcTimeline(out truncated) == null);
                Eq(true, probe.TakeSummary() == null);
                Eq(1, platform.AppendCalls);
                Eq(1, platform.Captures[0].StopCalls);
            }
        }

        private static void TestIrqObservationGraceRearm()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                StartIrqObservation(platform, probe, "same game");
                AdvanceIrqObservation(platform, probe, 62, false);
                probe.Seal();
                Eq(true, probe.HasSealedPending);
                Eq(0, platform.Records.Count);
                platform.Now += 2 * TimeSpan.TicksPerSecond;
                StartIrqObservation(platform, probe, "same game");
                Eq(false, probe.HasSealedPending);
                AdvanceIrqObservation(platform, probe, 63, false);
                probe.Seal();
                probe.TakeSummary();
                Eq(2, platform.Captures.Count);
                Eq(1, platform.Captures[0].StopCalls);
                Eq(1, platform.Captures[1].StopCalls);
                Eq(1, platform.Records.Count);
                Eq(63, platform.Records[0].DurationSeconds);
            }
        }

        private static void TestIrqObservationIdentityGuards()
        {
            foreach (int invalid in new int[] { 0, 1, 2 })
            {
                var platform = new ObservationTestPlatform();
                using (var probe = new IrqSessionProbe(platform))
                {
                    probe.Arm("unknown identity", invalid == 0 ? 0UL : ObservationSystemMask, true);
                    Eq(false, probe.ConfirmSystemObservation(
                        invalid == 1 ? 0 : ObservationRendererPid,
                        invalid == 2 ? 0 : ObservationRendererCreation));
                    Eq(0, platform.Captures.Count);
                    Eq(0, platform.AppendCalls);
                }
            }
            foreach (bool pidReused in new bool[] { false, true })
            {
                var platform = new ObservationTestPlatform();
                using (var probe = new IrqSessionProbe(platform))
                {
                    StartIrqObservation(platform, probe, "identity must stay bound");
                    AdvanceIrqObservation(platform, probe, 62, false);
                    Eq(false, probe.ConfirmSystemObservation(
                        pidReused ? ObservationRendererPid : ObservationRendererPid + 1,
                        pidReused ? ObservationRendererCreation + 1 : ObservationRendererCreation));
                    Eq(false, probe.IsCapturing);
                    probe.TakeSummary();
                    Eq(1, platform.Captures[0].StopCalls);
                    Eq(0, platform.AppendCalls);
                }
            }
        }

        private static void TestIrqObservationMutationBoundary()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                // 系统记录描述的是整段对局期间的中断 不能把正常后台和调优活动伪装成归因证据
                StartIrqObservation(platform, probe, "system activity stays in observation");
                AdvanceIrqObservation(platform, probe, 10, false);
                probe.BeginExternalMutation();
                probe.BeginExternalMutation();
                Eq(true, probe.IsCapturing);
                Eq(0, platform.Captures[0].StopCalls);
                AdvanceIrqObservation(platform, probe, 3, false);
                probe.EndExternalMutation();
                AdvanceIrqObservation(platform, probe, 2, false);
                Eq(1, platform.Captures.Count);
                probe.EndExternalMutation();
                AdvanceIrqObservation(platform, probe, 56, false);
                probe.TakeSummary();
                Eq(1, platform.Captures.Count);
                Eq(1, platform.Captures[0].StopCalls);
                Eq(1, platform.Records.Count);
                Eq(71, platform.Records[0].DurationSeconds);
                Eq(0UL, platform.Records[0].GameMask);
            }

            var strictPlatform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(strictPlatform))
            {
                // 严格落核归因不能跨过 Pavise 自己的 setter 嵌套写区没结束也不能起采
                probe.Arm("strict mutation boundary", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(true, strictPlatform.Captures[0].TimelineRequested);
                AdvanceIrqObservation(strictPlatform, probe, 10, true);
                probe.BeginExternalMutation();
                probe.BeginExternalMutation();
                Eq(false, probe.IsCapturing);
                Eq(1, strictPlatform.Captures[0].StopCalls);
                Eq(false, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                probe.EndExternalMutation();
                Eq(false, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(1, strictPlatform.Captures.Count);
                probe.EndExternalMutation();
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(2, strictPlatform.Captures.Count);
                AdvanceIrqObservation(strictPlatform, probe, 61, true);
                probe.TakeSummary();
                Eq(1, strictPlatform.Records.Count);
                Eq(61, strictPlatform.Records[0].DurationSeconds);
                Eq(ObservationStrictMask, strictPlatform.Records[0].GameMask);
            }
        }

        private static void TestIrqObservationStaleProof()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                StartIrqObservation(platform, probe, "stale proof");
                AdvanceIrqObservation(platform, probe, 61, false);
                platform.Now += 2 * TimeSpan.TicksPerSecond;
                Eq(false, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(false, probe.IsCapturing);
                Eq(1, platform.Captures[0].StopCalls);
                Eq(0, platform.AppendCalls);
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 62, false);
                probe.TakeSummary();
                Eq(1, platform.Records.Count);
                Eq(62, platform.Records[0].DurationSeconds);
            }
        }

        private static void TestIrqObservationModeSeparation()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("strict source", ObservationSystemMask);
                Eq(false, probe.IsSystemObservation);
                Eq(true, probe.RequiresPlacementAudit);
                Eq(false, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(0, platform.Captures.Count);
                probe.Arm("strict source", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(true, probe.IsPlacementCapturing);
                Eq(true, platform.Captures[0].TimelineRequested);
                AdvanceIrqObservation(platform, probe, 62, true);

                // 等级一变就得明确重新布防 旧的严格片段不能混进系统观测记录
                StartIrqObservation(platform, probe, "system source");
                AdvanceIrqObservation(platform, probe, 63, false);
                probe.TakeSummary();
                Eq(1, platform.Records.Count);
                Eq("system source", platform.Records[0].GameName);
                Eq(0UL, platform.Records[0].GameMask);
                Eq(63, platform.Records[0].DurationSeconds);

                probe.Arm("strict again", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(true, platform.Captures[platform.Captures.Count - 1].TimelineRequested);
                AdvanceIrqObservation(platform, probe, 64, true);
                probe.TakeSummary();
                Eq(2, platform.Records.Count);
                Eq(ObservationStrictMask, platform.Records[1].GameMask);
                Eq(64, platform.Records[1].DurationSeconds);
            }
        }

        private static void TestIrqObservationFailures()
        {
            foreach (bool busy in new bool[] { false, true })
            {
                var platform = new ObservationTestPlatform();
                var capture = new ObservationTestCapture
                { StartResult = false, BusyValue = busy, Failure = "fake ETW start failure" };
                platform.Queued.Enqueue(capture);
                using (var probe = new IrqSessionProbe(platform))
                {
                    probe.Arm("failed start", ObservationSystemMask, true);
                    Eq(false, probe.ConfirmSystemObservation(
                        ObservationRendererPid, ObservationRendererCreation));
                    Eq(false, probe.IsCapturing);
                    Eq(1, capture.StartCalls);
                    Eq(0, platform.AppendCalls);
                    Eq(true, probe.StatusWarning);
                    if (string.IsNullOrEmpty(probe.StatusText))
                        throw new Exception("A failed ETW start had no status explanation");
                }
            }
            for (int failure = 0; failure < 4; failure++)
            {
                var platform = new ObservationTestPlatform();
                var capture = new ObservationTestCapture();
                if (failure == 0) capture.Result.EventsLost = 1;
                if (failure == 1) capture.Result.BuffersLost = 1;
                if (failure == 2) capture.Result.Incomplete = true;
                if (failure == 3) capture.Result.Drivers.Clear();
                platform.Queued.Enqueue(capture);
                using (var probe = new IrqSessionProbe(platform))
                {
                    StartIrqObservation(platform, probe, "unusable result");
                    AdvanceIrqObservation(platform, probe, 61, false);
                    Eq(true, probe.TakeSummary() == null);
                    Eq(0, platform.AppendCalls);
                    bool truncated;
                    List<InterruptAttribution.DpcTimelineEntry> timeline = probe.TakeDpcTimeline(out truncated);
                    if (failure < 3 && timeline != null) Eq(true, truncated);
                }
            }
            // 逐事件时间线到容量上限 不代表聚合结果丢了事件 原始统计照样能存
            var cappedTimeline = new ObservationTestPlatform();
            cappedTimeline.Queued.Enqueue(new ObservationTestCapture { Truncated = true });
            using (var probe = new IrqSessionProbe(cappedTimeline))
            {
                probe.Arm("strict timeline capped", ObservationSystemMask);
                Eq(true, probe.ConfirmGameMask(ObservationStrictMask,
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(true, cappedTimeline.Captures[0].TimelineRequested);
                AdvanceIrqObservation(cappedTimeline, probe, 61, true);
                probe.TakeSummary();
                Eq(1, cappedTimeline.Records.Count);
                Eq(ObservationStrictMask, cappedTimeline.Records[0].GameMask);
                bool truncated;
                Eq(false, probe.TakeDpcTimeline(out truncated) == null);
                Eq(true, truncated);
            }
            var failedAppend = new ObservationTestPlatform { AppendResult = false };
            using (var probe = new IrqSessionProbe(failedAppend))
            {
                StartIrqObservation(failedAppend, probe, "failed append");
                AdvanceIrqObservation(failedAppend, probe, 61, false);
                Eq(true, probe.TakeSummary() == null);
                Eq(1, failedAppend.AppendCalls);
                Eq(0, failedAppend.Records.Count);
                Eq(true, probe.TakeSummary() == null);
                Eq(1, failedAppend.AppendCalls);
            }
        }

        private static void TestIrqObservationDisableAndDispose()
        {
            foreach (bool elevated in new bool[] { false, true })
            {
                var platform = new ObservationTestPlatform
                { EnabledValue = !elevated, ElevatedValue = elevated };
                using (var probe = new IrqSessionProbe(platform))
                {
                    probe.Arm("not allowed", ObservationSystemMask, true);
                    Eq(false, probe.ConfirmSystemObservation(
                        ObservationRendererPid, ObservationRendererCreation));
                    Eq(0, platform.Captures.Count);
                    Eq(0, platform.AppendCalls);
                    if (!elevated) Eq(true, probe.StatusWarning);
                }
            }
            var disabled = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(disabled))
            {
                StartIrqObservation(disabled, probe, "disabled before commit");
                AdvanceIrqObservation(disabled, probe, 61, false);
                probe.Seal();
                disabled.EnabledValue = false;
                Eq(true, probe.TakeSummary() == null);
                Eq(0, disabled.AppendCalls);
                Eq(false, probe.HasSealedPending);
            }
            var disposed = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(disposed))
            {
                StartIrqObservation(disposed, probe, "disposed before commit");
                AdvanceIrqObservation(disposed, probe, 61, false);
                probe.Dispose();
                Eq(1, disposed.Captures[0].StopCalls);
                Eq(0, disposed.AppendCalls);
                probe.Arm("must not resurrect", ObservationSystemMask, true);
                Eq(false, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                Eq(1, disposed.Captures.Count);
            }
        }

        private static void TestIrqObservationRearmDuringStop()
        {
            var platform = new ObservationTestPlatform();
            using (var probe = new IrqSessionProbe(platform))
            {
                StartIrqObservation(platform, probe, "old game");
                AdvanceIrqObservation(platform, probe, 62, false);
                platform.Captures[0].OnStop = delegate
                { probe.Arm("replacement game", ObservationSystemMask, true); };
                Eq(true, probe.TakeSummary() == null);
                Eq(0, platform.AppendCalls);
                Eq(true, probe.ConfirmSystemObservation(
                    ObservationRendererPid, ObservationRendererCreation));
                AdvanceIrqObservation(platform, probe, 63, false);
                probe.TakeSummary();
                Eq(1, platform.Records.Count);
                Eq("replacement game", platform.Records[0].GameName);
                Eq(63, platform.Records[0].DurationSeconds);
            }
        }
    }
}
