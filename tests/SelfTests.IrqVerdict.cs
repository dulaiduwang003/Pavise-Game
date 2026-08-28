// @author bdth 2074055628@qq.com
// 文件用途 中断判据与对局台账的自测 数据取自真机五分钟负载实测
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static IrqDriverRecord Rec(string drv, long dpc, double totalUs,
            double maxUs, long over500, ulong mask)
        {
            var d = new IrqDriverRecord();
            d.Driver = drv;
            d.Dpc = dpc;
            d.DpcTotalNs = (long)(totalUs * 1000.0);
            d.DpcMaxNs = (long)(maxUs * 1000.0);
            d.Over500Us = over500;
            d.CpuMask = mask;
            return d;
        }

        private static IrqSessionRecord Session(int seconds, params IrqDriverRecord[] drivers)
        {
            return SessionWithMask(seconds, 0xFFUL, drivers);
        }

        private static IrqSessionRecord SessionWithMask(int seconds, ulong gameMask,
            params IrqDriverRecord[] drivers)
        {
            var s = new IrqSessionRecord();
            s.StartUtcTicks = 1;
            s.DurationSeconds = seconds;
            s.GameName = "T";
            s.BootStamp = IrqAffinityEngine.BootStamp();
            s.GameMask = gameMask;
            s.SystemMask = 0xFFFFUL;
            foreach (IrqDriverRecord d in drivers) s.Drivers.Add(d);
            return s;
        }

        private static void TestIrqVerdictRanking()
        {
            // 真机五分钟实测数据 游戏核假设为低八位
            var all = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                all.Add(Session(300,
                    Rec("dxgkrnl.sys", 405995, 21374785, 3643, 621, 0xFFFUL),
                    Rec("ndis.sys", 71379, 7382165, 1271, 576, 0x1UL),
                    Rec("ntoskrnl.exe", 205271, 5397311, 588, 5, 0xFFFUL),
                    Rec("ACPI.sys", 28, 17354, 5714, 13, 0x1UL)));

            int used;
            List<IrqDriverVerdict> v = IrqVerdict.Evaluate(all, 60, out used);
            Eq(3, used);

            var by = new Dictionary<string, IrqDriverVerdict>(StringComparer.OrdinalIgnoreCase);
            foreach (IrqDriverVerdict x in v) by[x.Driver] = x;

            // ndis 单核绑定又落在游戏核上 必撞 属于结构性冲突
            Eq(true, by["ndis.sys"].StructuralConflict);
            Eq(true, by["ndis.sys"].Worth);
            // dxgkrnl 同时散到游戏核外，聚合数据无法证明慢 DPC 发生在游戏核，不下建议
            Eq(false, by["dxgkrnl.sys"].StructuralConflict);
            Eq(false, by["dxgkrnl.sys"].Worth);
            // ACPI 单次最长全场第一 但五分钟才 28 次 不值得动 它还是系统关键驱动
            Eq(false, by["ACPI.sys"].Worth);
            // ntoskrnl 超阈值太少
            Eq(false, by["ntoskrnl.exe"].Worth);
            // ACPI 单核绑定且落在游戏核上 但频率太低 结构性成立不代表值得动
            Eq(true, by["ACPI.sys"].StructuralConflict);

            // 排序必须把值得动的放前面 不能像旧版那样按单次最长排
            // 旧版会让 ACPI 的 5714us 排第一 把用户引到最不该动的那台
            Eq(true, v[0].Worth);
            Eq(false, v[1].Worth);
            Eq(false, v[2].Worth);
            Eq(false, v[3].Worth);
            // ACPI 单次最长全场第一 也不能挤到唯一有充分证据的建议前面
            if (string.Equals(v[0].Driver, "ACPI.sys", StringComparison.OrdinalIgnoreCase))
                throw new Exception("ACPI ranked into the actionable slots on max duration alone");
        }

        private static void TestIrqSessionSummaryPicksByImpact()
        {
            // 取自真机一局 114 秒的实测数据
            //   ACPI 单次 9335us 全场最长 但一局才二十几次 DPC 零次超阈值
            //   dxgkrnl 单次 3819us 但有超阈值记录 且 DPC 是它的三千倍
            //   摘要必须挑 dxgkrnl 挑 ACPI 就等于日常那行天天喊最不该动的那台
            IrqSessionRecord rec = Session(114,
                Rec("ACPI.sys", 26, 17354, 9335, 0, 0x1UL),
                Rec("dxgkrnl.sys", 87189, 21374785, 3819, 2, 0x7UL),
                Rec("ntoskrnl.exe", 72060, 5397311, 285, 0, 0xFFFUL));
            string s = IrqVerdict.SummarizeSession(rec);
            if (s == null) throw new Exception("summary was empty");
            if (s.IndexOf("dxgkrnl.sys", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("summary did not pick the driver with real impact: " + s);
            if (s.IndexOf("ACPI.sys", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("summary picked ACPI on max duration alone: " + s);

            // 全都没有超阈值时退回按单次最长挑 至少不能返回空
            IrqSessionRecord quiet = Session(114,
                Rec("a.sys", 100, 1000, 120, 0, 0x1UL),
                Rec("b.sys", 100, 1000, 300, 0, 0x1UL));
            string q = IrqVerdict.SummarizeSession(quiet);
            if (q == null) throw new Exception("summary was empty on a quiet session");
            if (q.IndexOf("b.sys", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("tie-break should fall back to the longest single run: " + q);
        }

        private static void TestPresentDpcZeroHitIsEvidence()
        {
            var intervals = new List<long[]> { new long[] { 100, 200 } };
            var outside = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry
                    { StartQpc = 500, EndQpc = 500, Module = "nvlddmkm.sys", Cpu = 2, DpcUs = 900 }
            };
            GameMode.PresentDpcAlignment zero = GameMode.AlignDpcToLongFrames(intervals, outside);
            if (zero == null || !zero.Ok) throw new Exception("区间外 DPC 被误判为采集不可用");
            Eq(1, zero.LongFrames);
            Eq(0, zero.TotalDpcInLongFrames);
            Eq(0, zero.LongFrameHits.Count);

            GameMode.PresentDpcAlignment noLongFrames = GameMode.AlignDpcToLongFrames(
                new List<long[]>(), outside);
            if (noLongFrames == null || !noLongFrames.Ok)
                throw new Exception("无长帧仍是有效对齐结果 不应判不可用");
            noLongFrames = GameMode.AlignDpcToLongFrames(new List<long[]>(), null);
            if (noLongFrames == null || !noLongFrames.Ok)
                throw new Exception("无长帧时不应被不可用的 DPC 探针降级");
            GameMode.PresentDpcAlignment noDpc = GameMode.AlignDpcToLongFrames(
                intervals, new List<InterruptAttribution.DpcTimelineEntry>());
            if (noDpc == null || !noDpc.Ok)
                throw new Exception("零 DPC 仍是有效对齐结果 不应判不可用");
            if (GameMode.AlignDpcToLongFrames(null, outside) != null
                || GameMode.AlignDpcToLongFrames(intervals, null) != null)
                throw new Exception("null 采集链应保持不可用");

            var twoFrames = new List<long[]>
            {
                new long[] { 100, 200 }, new long[] { 300, 400 }
            };
            var hits = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry { StartQpc = 150, EndQpc = 150, Module = "nvlddmkm.sys" },
                new InterruptAttribution.DpcTimelineEntry { StartQpc = 160, EndQpc = 160, Module = "NVLDDMKM.SYS" },
                new InterruptAttribution.DpcTimelineEntry { StartQpc = 350, EndQpc = 350, Module = "nvlddmkm.sys" }
            };
            GameMode.PresentDpcAlignment positive = GameMode.AlignDpcToLongFrames(twoFrames, hits);
            Eq(true, positive.Ok);
            Eq(2, positive.LongFrameHits["nvlddmkm.sys"]);
            Eq(3, positive.DpcCounts["nvlddmkm.sys"]);

            var crossing = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry
                    { StartQpc = 150, EndQpc = 250, Module = "cross.sys" }
            };
            GameMode.PresentDpcAlignment crossed = GameMode.AlignDpcToLongFrames(intervals, crossing);
            Eq(1, crossed.LongFrameHits["cross.sys"]);
            var adjacent = new List<long[]> { new long[] { 100, 200 }, new long[] { 200, 300 } };
            var onBoundary = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry
                    { StartQpc = 200, EndQpc = 250, Module = "boundary.sys" }
            };
            GameMode.PresentDpcAlignment boundary = GameMode.AlignDpcToLongFrames(adjacent, onBoundary);
            Eq(1, boundary.LongFrameHits["boundary.sys"]); // 只命中第二帧，边界不重复

            var spansBoth = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry
                    { StartQpc = 150, EndQpc = 250, Module = "spans.sys" }
            };
            GameMode.PresentDpcAlignment both = GameMode.AlignDpcToLongFrames(adjacent, spansBoth);
            Eq(2, both.LongFrameHits["spans.sys"]); // 同一事件确实撞到两帧
            Eq(1, both.DpcCounts["spans.sys"]);      // 但 DPC 事件总数不能重复
            Eq(1, both.TotalDpcInLongFrames);

            // 长帧区间里的未知模块可能正是 Worth 驱动，零命中不能据此排除；
            // 区间外的未知事件与本次撞帧无关，不应把证据无条件降级。
            var unknownInside = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry { StartQpc = 150, EndQpc = 150, Module = "?" }
            };
            GameMode.PresentDpcAlignment unknown = GameMode.AlignDpcToLongFrames(
                intervals, unknownInside);
            Eq(true, unknown.UnknownModuleInLongFrames);
            Eq(4, GameMode.ResolveIrqReportedCount(true,
                unknown.SwapchainIdentityReliable, unknown.UnknownModuleInLongFrames, 1, 0, 4));
            var unknownOutside = new List<InterruptAttribution.DpcTimelineEntry>
            {
                new InterruptAttribution.DpcTimelineEntry { StartQpc = 500, EndQpc = 500, Module = null }
            };
            unknown = GameMode.AlignDpcToLongFrames(intervals, unknownOutside);
            Eq(false, unknown.UnknownModuleInLongFrames);

            Eq(4, GameMode.ResolveIrqReportedCount(false, false, false, 0, 0, 4)); // 探针不可用
            Eq(4, GameMode.ResolveIrqReportedCount(true, false, false, 1, 0, 4));  // PID多流零命中不是反证
            Eq(0, GameMode.ResolveIrqReportedCount(true, true, false, 1, 0, 4));   // 可靠单流的完整零命中
            Eq(4, GameMode.ResolveIrqReportedCount(true, true, true, 1, 0, 4));    // 有长帧但 DPC 截断
            Eq(0, GameMode.ResolveIrqReportedCount(true, true, true, 0, 0, 4));    // 可靠单流无长帧
            Eq(4, GameMode.ResolveIrqReportedCount(true, false, true, 1, 2, 4));   // 不可靠流不能否定其余 Worth
            Eq(4, GameMode.ResolveIrqReportedCount(true, true, true, 1, 2, 4));    // DPC 不完整也不能否定其余
            Eq(2, GameMode.ResolveIrqReportedCount(true, true, false, 1, 2, 4));   // 可靠且完整时才缩到命中项
            Eq(true, GameMode.AlignDpcToLongFrames(intervals, outside, true)
                .SwapchainIdentityReliable);
        }

        private static void TestPresentCaptureReliability()
        {
            Eq(true, GameMode.PresentCaptureComplete(true, false, false, 0, 0));
            Eq(false, GameMode.PresentCaptureComplete(false, false, false, 0, 0));
            Eq(false, GameMode.PresentCaptureComplete(true, true, false, 0, 0));
            Eq(false, GameMode.PresentCaptureComplete(true, false, true, 0, 0));
            Eq(false, GameMode.PresentCaptureComplete(true, false, false, 1, 0));
            Eq(false, GameMode.PresentCaptureComplete(true, false, false, 0, 1));
            Eq(true, InterruptAttribution.CaptureComplete(true, true, true, false));
            Eq(false, InterruptAttribution.CaptureComplete(false, true, true, false));
            Eq(false, InterruptAttribution.CaptureComplete(true, false, true, false));
            Eq(false, InterruptAttribution.CaptureComplete(true, true, false, false));
            Eq(false, InterruptAttribution.CaptureComplete(true, true, true, true));
            Eq(100L, InterruptAttribution.SafeTimelineStart(100, 200, 100));
            Eq(1201L, InterruptAttribution.SafeTimelineStart(100, 1201, 1000));
            Eq(1000L, InterruptAttribution.SafeTimelineStart(2000, 1000, 1000));

            // 同一 profile 局内换渲染进程应更新；换局时新 profile 不得覆盖旧局 PID。
            Eq(202, GameMode.UpdateSessionRendererPid("profile-a", 101, "PROFILE-A", 202));
            Eq(101, GameMode.UpdateSessionRendererPid("profile-a", 101, "profile-b", 303));
            Eq(101, GameMode.UpdateSessionRendererPid("profile-a", 101, "profile-a", 0));
            Eq(true, GameMode.SameReportedProfile("profile-a", "PROFILE-A"));
            Eq(false, GameMode.SameReportedProfile("profile-a", "profile-b"));
            Eq(false, GameMode.SameReportedProfile(null, null));

            const int renderer = 77;
            var tooFew = new List<PresentFrame>
            {
                new PresentFrame { Pid = renderer, Qpc = 100 },
                new PresentFrame { Pid = renderer, Qpc = 200 }
            };
            // 塞再多其它进程帧也不能拿来替代目标渲染器，避免生成伪负证据。
            for (int i = 0; i < 100; i++)
                tooFew.Add(new PresentFrame { Pid = 88, Qpc = 1000 + i * 10 });
            Eq<List<long[]>>(null, GameMode.BuildLongFrameIntervals(tooFew, 1000, renderer, 2.0));
            Eq<List<long[]>>(null, GameMode.BuildLongFrameIntervals(tooFew, 1000, 0, 2.0));

            // 31 帧形成 30 个稳定间隔，样本够时“没有长帧”才是非 null 空集合。
            var enough = new List<PresentFrame>();
            for (int i = 0; i <= 30; i++)
                enough.Add(new PresentFrame { Pid = renderer, Qpc = 100L * i });
            List<long[]> none = GameMode.BuildLongFrameIntervals(enough, 1000, renderer, 2.0);
            if (none == null) throw new Exception("足量完整 present 被误判为不可用");
            Eq(0, none.Count);
            Eq<List<long[]>>(null, GameMode.BuildLongFrameIntervals(enough, 1000, renderer, 4.0));

            // 两小段呈现中间夹 Alt-Tab 空窗，空窗不得被当成一帧或 coverage。
            var splitByPause = new List<PresentFrame>();
            long splitQpc = 0;
            for (int i = 0; i < 16; i++)
            {
                splitByPause.Add(new PresentFrame { Pid = renderer, Qpc = splitQpc });
                splitQpc += 100;
            }
            splitQpc += 30000;
            for (int i = 0; i < 16; i++)
            {
                splitByPause.Add(new PresentFrame { Pid = renderer, Qpc = splitQpc });
                splitQpc += 100;
            }
            Eq<List<long[]>>(null,
                GameMode.BuildLongFrameIntervals(splitByPause, 1000, renderer, 2.0));

            // 在同一足量基线上放一个 5 倍间隔，应精确产出一段长帧区间。
            enough.Clear();
            long qpc = 0;
            for (int i = 0; i <= 30; i++)
            {
                enough.Add(new PresentFrame { Pid = renderer, Qpc = qpc });
                qpc += i == 14 ? 500 : 100;
            }
            List<long[]> one = GameMode.BuildLongFrameIntervals(enough, 1000, renderer, 2.0);
            if (one == null) throw new Exception("足量 present 未生成区间结果");
            Eq(1, one.Count);

            // 同 PID 下主 swapchain 确实缺两帧，但辅助呈现流恰好填进空洞。
            // Event 184 没有 swapchain id，按 PID 合并后会伪造“无长帧”；
            // 这种零结果只能不加分，不能把 Worth 建议打掉。
            var mainStream = new List<PresentFrame>();
            var mergedStreams = new List<PresentFrame>();
            for (int i = 0; i <= 40; i++)
            {
                if (i != 15 && i != 16)
                {
                    var main = new PresentFrame { Pid = renderer, Qpc = i * 200L };
                    mainStream.Add(main);
                    mergedStreams.Add(main);
                }
                mergedStreams.Add(new PresentFrame { Pid = renderer, Qpc = i * 200L + 100L });
            }
            List<long[]> mainLong = GameMode.BuildLongFrameIntervals(
                mainStream, 1000, renderer, 2.0);
            if (mainLong == null) throw new Exception("主呈现流样本被误判不足");
            Eq(1, mainLong.Count);
            List<long[]> mergedLong = GameMode.BuildLongFrameIntervals(
                mergedStreams, 1000, renderer, 2.0);
            if (mergedLong == null) throw new Exception("合并呈现流样本被误判不足");
            Eq(0, mergedLong.Count);
            Eq(3, GameMode.ResolveIrqReportedCount(true, false, false,
                mergedLong.Count, 0, 3));

            // 辅助流的高频 burst 还会把合并 median 压到极低，进而把
            // 主流正常 100-tick 间隔大量伪造成长帧。默认多流身份不可靠时，
            // 这种“正命中”同样只是日志线索，不得缩减 Worth 计数。
            var burstMerged = new List<PresentFrame>();
            for (int i = 0; i <= 40; i++)
                burstMerged.Add(new PresentFrame { Pid = renderer, Qpc = 100L * i + 1000L });
            for (int i = 0; i < 60; i++)
                burstMerged.Add(new PresentFrame { Pid = renderer, Qpc = i });
            List<long[]> burstLong = GameMode.BuildLongFrameIntervals(
                burstMerged, 1000, renderer, 2.0);
            if (burstLong == null || burstLong.Count == 0)
                throw new Exception("台架未能复现多流 burst 伪长帧");
            Eq(4, GameMode.ResolveIrqReportedCount(true, false, false,
                burstLong.Count, 1, 4));
        }

        private static void TestIrqVerdictGuards()
        {
            Eq(true, IrqSessionProbe.CanConfirmMask(0xFFUL, 0xFFFFUL, 123, 456));
            Eq(false, IrqSessionProbe.CanConfirmMask(0, 0xFFFFUL, 123, 456));
            Eq(false, IrqSessionProbe.CanConfirmMask(0xFFFFUL, 0xFFFFUL, 123, 456));
            Eq(false, IrqSessionProbe.CanConfirmMask(0x10000UL, 0xFFFFUL, 123, 456));
            Eq(false, IrqSessionProbe.CanConfirmMask(0xFFUL, 0xFFFFUL, 0, 456));
            Eq(false, IrqSessionProbe.CanConfirmMask(0xFFUL, 0xFFFFUL, 123, 0));
            Eq(false, GameMode.PlacementProofMatches(0x3UL, 0x1UL, false, true, false));
            Eq(true, GameMode.PlacementProofMatches(0x3UL, 0x7UL, false, true, false));
            Eq(true, GameMode.PlacementProofMatches(0x3UL, 0x3UL, false, false, true));
            Eq(false, GameMode.PlacementProofMatches(0x3UL, 0x7UL, false, false, true));
            Eq(false, GameMode.PlacementProofMatches(0x3UL, 0x3UL, false, false, false));
            Eq(true, GameMode.PlacementProofMatches(0x3UL, 0, true, true, false));
            Eq(false, GameMode.PlacementProofMatches(0x3UL, 0, true, false, true));
            // 默认 CPU Sets 可被线程覆盖，必须拿到每个线程的完整有效集合；
            // union 少一颗核也不能把那颗核上的 DPC 算成游戏重叠。
            Eq(true, GameMode.AttributionPlacementProofMatches(
                0x3UL, 0x3UL, false, true, 0x3UL));
            Eq(false, GameMode.AttributionPlacementProofMatches(
                0x3UL, 0x3UL, false, true, 0x1UL));
            Eq(false, GameMode.AttributionPlacementProofMatches(
                0x3UL, 0x3UL, false, false, 0x3UL));
            Eq(false, GameMode.AttributionPlacementProofMatches(
                0x3UL, 0x7UL, false, true, 0x3UL));
            Eq(false, GameMode.AttributionPlacementProofMatches(
                0x3UL, 0x3UL, true, true, 0x3UL));
            Eq(0x3UL, GameMode.EffectiveAttributionThreadMask(
                0x3UL, 0x3UL, 0, false));
            Eq(0x1UL, GameMode.EffectiveAttributionThreadMask(
                0x3UL, 0x3UL, 0x1UL, true));
            Eq(0x1UL, GameMode.EffectiveAttributionThreadMask(
                0x3UL, 0x1UL, 0x3UL, true));
            // CPU Set 与硬亲和性完全冲突时，Windows 以后者为准。
            Eq(0x3UL, GameMode.EffectiveAttributionThreadMask(
                0x3UL, 0x3UL, 0x4UL, true));
            Eq(true, GameMode.SameAttributionThreadSnapshot(
                new[] { 11, 22 }, new[] { 11, 22 }));
            Eq(false, GameMode.SameAttributionThreadSnapshot(
                new[] { 11, 22 }, new[] { 11, 23 }));
            Eq(false, GameMode.SameAttributionThreadSnapshot(
                new[] { 11 }, new[] { 11, 22 }));
            Eq(false, GameMode.SameAttributionThreadSnapshot(
                null, new[] { 11 }));
            Eq(true, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, true, 0x3UL,
                0, 0x3UL, true, 0x3UL));
            Eq(true, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, false, 0,
                0, 0x3UL, false, 0));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 42, false, 0, 0x3UL, true, 0x3UL,
                0, 0x3UL, true, 0x3UL));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 43, true, 0, 0x3UL, true, 0x3UL,
                0, 0x3UL, true, 0x3UL));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, true, 0x3UL,
                0, 0x1UL, true, 0x3UL));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, true, 0x3UL,
                1, 0x3UL, true, 0x3UL));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, true, 0x3UL,
                0, 0x3UL, true, 0x1UL));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, false, 0,
                0, 0x3UL, true, 0x3UL));
            Eq(false, GameMode.AttributionThreadStateMatches(
                42, 42, true, 0, 0x3UL, false, 0x1UL,
                0, 0x3UL, false, 0x1UL));
            bool zeroActive;
            Eq(false, Native.TryQueryThreadActive(
                IntPtr.Zero, out zeroActive));
            Eq(-1, Native.QueryThreadOwnerPid(IntPtr.Zero));
            ushort zeroGroup;
            ulong zeroGroupMask;
            Eq(false, Native.TryQueryThreadGroupAffinity(
                IntPtr.Zero, out zeroGroup, out zeroGroupMask));
            Eq(true, Native.QueryThreadSelectedCpuSets(
                IntPtr.Zero) == null);
            ulong nullCpuSetMask;
            Eq(false, CpuTopology.TryCpuSetIdsToMask(
                null, out nullCpuSetMask));
            ulong emptyCpuSetMask;
            Eq(true, CpuTopology.TryCpuSetIdsToMask(
                new uint[0], out emptyCpuSetMask));
            Eq(0UL, emptyCpuSetMask);

            if (!CpuTopology.MultiGroup)
            {
                using (var self = System.Diagnostics.Process.GetCurrentProcess())
                {
                    IntPtr selfHandle = Native.OpenProcess(
                        Native.PROCESS_QUERY_LIMITED_INFORMATION,
                        false, self.Id);
                    if (selfHandle == IntPtr.Zero)
                        throw new Exception("cannot open self for thread attribution proof test");
                    try
                    {
                        long selfCreation, selfCpu;
                        ulong selfIo;
                        if (!Native.QueryProcessSample(
                                selfHandle, out selfCreation,
                                out selfCpu, out selfIo))
                            throw new Exception("cannot identify thread attribution proof test process");
                        ulong hard = Native.QueryAffinity(selfHandle);
                        ulong one = hard & (~hard + 1UL);
                        uint[] ids = CpuTopology.CpuSetIdsFor(one);
                        ulong mapped;
                        if (ids == null || ids.Length == 0
                            || !CpuTopology.TryCpuSetIdsToMask(ids, out mapped))
                            throw new Exception("cannot roundtrip live CPU Set IDs");
                        Eq(one, mapped);
                        uint[] allIds = CpuTopology.CpuSetIdsFor(
                            CpuTopology.AllMask);
                        if (allIds == null || allIds.Length == 0)
                            throw new Exception("cannot enumerate live CPU Set IDs");
                        var knownIds = new HashSet<uint>(allIds);
                        uint unknownId = uint.MaxValue;
                        while (knownIds.Contains(unknownId)) unknownId--;
                        ulong unknownMask;
                        Eq(false, CpuTopology.TryCpuSetIdsToMask(
                            new[] { unknownId }, out unknownMask));

                        IntPtr currentThread = Native.OpenThread(
                            Native.THREAD_QUERY_LIMITED_INFORMATION
                                | Native.SYNCHRONIZE,
                            false, Native.GetCurrentThreadId());
                        if (currentThread == IntPtr.Zero)
                            throw new Exception("cannot open current thread for attribution proof test");
                        try
                        {
                            Eq(self.Id, Native.QueryThreadOwnerPid(currentThread));
                            bool active;
                            Eq(true, Native.TryQueryThreadActive(
                                currentThread, out active));
                            Eq(true, active);
                            ushort group;
                            ulong groupMask;
                            Eq(true, Native.TryQueryThreadGroupAffinity(
                                currentThread, out group, out groupMask));
                            Eq(0, (int)group);
                            Eq(true, groupMask != 0);
                            Eq(true, Native.QueryThreadSelectedCpuSets(
                                currentThread) != null);
                            bool maskAssigned;
                            ulong selectedMask;
                            Native.CpuSetMaskQueryResult maskResult =
                                Native.QueryThreadSelectedCpuSetMasks(
                                    currentThread, out maskAssigned,
                                    out selectedMask);
                            if (maskResult
                                == Native.CpuSetMaskQueryResult.Failed)
                                throw new Exception("live thread CPU Set mask query failed");
                            if (Environment.OSVersion.Version.Build >= 22000)
                                Eq(Native.CpuSetMaskQueryResult.Success,
                                    maskResult);
                            if (maskResult
                                == Native.CpuSetMaskQueryResult.Success)
                                Eq(maskAssigned, selectedMask != 0);
                        }
                        finally { Native.CloseHandle(currentThread); }

                        bool defaultMaskAssigned;
                        ulong defaultMask;
                        Native.CpuSetMaskQueryResult defaultMaskResult =
                            Native.QueryProcessDefaultCpuSetMasks(
                                selfHandle, out defaultMaskAssigned,
                                out defaultMask);
                        if (defaultMaskResult
                            == Native.CpuSetMaskQueryResult.Failed)
                            throw new Exception("live process CPU Set mask query failed");
                        if (Environment.OSVersion.Version.Build >= 22000)
                            Eq(Native.CpuSetMaskQueryResult.Success,
                                defaultMaskResult);
                        if (defaultMaskResult
                            == Native.CpuSetMaskQueryResult.Success)
                            Eq(defaultMaskAssigned, defaultMask != 0);

                        bool liveProof = false;
                        for (int attempt = 0; attempt < 20 && !liveProof; attempt++)
                        {
                            liveProof = GameMode.AttributionThreadPlacementMatches(
                                selfHandle, self.Id, selfCreation,
                                hard, false);
                            if (!liveProof)
                                System.Threading.Thread.Sleep(10);
                        }
                        Eq(true, liveProof);
                    }
                    finally { Native.CloseHandle(selfHandle); }
                }
            }

            // proof hard pin 必须持有首次可写句柄的副本，不能依赖稍后重新
            // OpenProcess。副本在源句柄关闭后仍绑定同一 pid+creation；任一
            // identity 不符都必须 fail-closed，匹配时才允许恢复并读回。
            using (var self = System.Diagnostics.Process.GetCurrentProcess())
            {
                IntPtr source = Native.OpenProcess(
                    Native.PROCESS_SET_INFORMATION
                        | Native.PROCESS_QUERY_LIMITED_INFORMATION,
                    false, self.Id);
                if (source == IntPtr.Zero)
                    throw new Exception("cannot open self for retained IRQ proof handle test");
                IntPtr retained = IntPtr.Zero;
                try
                {
                    long creation, cpu;
                    ulong disk;
                    if (!Native.QueryProcessSample(
                            source, out creation, out cpu, out disk))
                        throw new Exception("cannot identify retained IRQ proof handle test process");
                    ulong affinity = Native.QueryAffinity(source);
                    if (affinity == 0)
                        throw new Exception("cannot query affinity for retained IRQ proof handle test");
                    retained = GameMode.DuplicateIrqProofRestoreHandleForTest(source);
                    if (retained == IntPtr.Zero)
                        throw new Exception("failed to retain IRQ proof restore handle");
                    Native.CloseHandle(source);
                    source = IntPtr.Zero;

                    Eq(false, GameMode.IrqProofRestoreHandleForTest(
                        retained, self.Id + 1, creation, affinity));
                    Eq(false, GameMode.IrqProofRestoreHandleForTest(
                        retained, self.Id, creation + 1, affinity));
                    Eq(true, GameMode.IrqProofRestoreHandleForTest(
                        retained, self.Id, creation, affinity));
                }
                finally
                {
                    if (source != IntPtr.Zero) Native.CloseHandle(source);
                    if (retained != IntPtr.Zero) Native.CloseHandle(retained);
                }
            }
            long began = DateTime.UtcNow.Ticks;
            Eq(55, IrqSessionProbe.CaptureDurationSeconds(
                began, began + 55L * TimeSpan.TicksPerSecond));
            Eq(0, IrqSessionProbe.CaptureDurationSeconds(began, began));
            using (var disposedProbe = new IrqSessionProbe())
            {
                disposedProbe.Dispose();
                disposedProbe.Arm("must-not-rearm", 0xFFFFUL);
                Eq(false, disposedProbe.RequiresPlacementAudit);
                Eq(false, disposedProbe.IsCapturing);
            }

            // 重启前的 IRQ 配置样本不得进入当前 boot 裁决。
            long currentBoot;
            if (long.TryParse(IrqAffinityEngine.BootStamp(), out currentBoot))
            {
                var oldBoot = new List<IrqSessionRecord>();
                for (int i = 0; i < 3; i++)
                {
                    IrqSessionRecord old = Session(300,
                        Rec("oldboot.sys", 100000, 9000000, 4000, 900, 0x1UL));
                    old.BootStamp = (currentBoot - 60).ToString();
                    oldBoot.Add(old);
                }
                int bootUsed;
                Eq(0, IrqVerdict.Evaluate(oldBoot, 60, out bootUsed).Count);
                Eq(0, bootUsed);
            }

            // 落点与游戏核没有交集 再难看也不值得挪
            var away = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                away.Add(Session(300, Rec("x.sys", 100000, 9000000, 4000, 900, 0xF00UL)));
            int used;
            List<IrqDriverVerdict> v = IrqVerdict.Evaluate(away, 60, out used);
            Eq(1, v.Count);
            Eq(false, v[0].Worth);

            // 单次太短 一次都换不回一帧
            var tiny = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                tiny.Add(Session(300, Rec("y.sys", 500000, 9000000, 150, 900, 0x1UL)));
            v = IrqVerdict.Evaluate(tiny, 60, out used);
            Eq(false, v[0].Worth);

            // 太短的局不进判断窗口
            var shortOnes = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                shortOnes.Add(Session(10, Rec("z.sys", 1000, 900000, 4000, 900, 0x1UL)));
            v = IrqVerdict.Evaluate(shortOnes, 60, out used);
            Eq(0, used);
            Eq(0, v.Count);

            // 掩码被截断时落点不可信 不许判结构性冲突
            var trunc = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
            {
                IrqDriverRecord d = Rec("t.sys", 1000, 900000, 4000, 5, 0x1UL);
                d.MaskTruncated = true;
                trunc.Add(Session(300, d));
            }
            v = IrqVerdict.Evaluate(trunc, 60, out used);
            Eq(false, v[0].StructuralConflict);
            Eq(false, v[0].Worth);

            // 全局有三局但目标驱动只在一局出现，不能把一次偶发尖峰说成“多局建议”。
            var sporadic = new List<IrqSessionRecord>
            {
                Session(300, Rec("once.sys", 100000, 9000000, 4000, 900, 0x1UL)),
                Session(300, Rec("quiet.sys", 1000, 90000, 100, 0, 0x1UL)),
                Session(300, Rec("quiet.sys", 1000, 90000, 100, 0, 0x1UL))
            };
            v = IrqVerdict.Evaluate(sporadic, 60, out used);
            IrqDriverVerdict once = v.Find(delegate (IrqDriverVerdict x)
                { return x.Driver == "once.sys"; });
            Eq(3, used);
            Eq(1, once.SessionsSeen);
            Eq(false, once.Worth);

            // 驱动升级后只认窗口里最新出现的版本。旧版已经累计三局，也不能把建议
            // 贴到只跑过一局的新版本设备上。
            var versioned = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
            {
                IrqDriverRecord oldDriver = Rec("netdrv.sys", 100000, 9000000, 4000, 900, 0x1UL);
                oldDriver.DriverVersion = "1.0";
                versioned.Add(Session(300, oldDriver));
            }
            IrqDriverRecord newDriver = Rec("netdrv.sys", 100, 9000, 100, 0, 0x1UL);
            newDriver.DriverVersion = "2.0";
            versioned.Add(Session(300, newDriver));
            v = IrqVerdict.Evaluate(versioned, 60, out used);
            Eq(1, v.Count);
            Eq("2.0", v[0].DriverVersion);
            Eq(1, v[0].SessionsSeen);
            Eq(false, v[0].Worth);

            // 判定必须按每局真实游戏核心，而不是拿全局 StrictBoostMask 代替。
            var customAway = new List<IrqSessionRecord>();
            var customHit = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
            {
                customAway.Add(SessionWithMask(300, 0xF00UL,
                    Rec("custom.sys", 100000, 9000000, 4000, 900, 0x1UL)));
                customHit.Add(SessionWithMask(300, 0xF00UL,
                    Rec("custom.sys", 100000, 9000000, 4000, 900, 0x100UL)));
            }
            v = IrqVerdict.Evaluate(customAway, 60, out used);
            Eq(false, v[0].Worth);
            v = IrqVerdict.Evaluate(customHit, 60, out used);
            Eq(true, v[0].Worth);

            // 三局只有极低频的真实重叠，另两局的重负载完全落在游戏核外；
            // 核外数据可以展示，但绝不能把重叠评分抬成 Worth。
            var mixedOverlap = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                mixedOverlap.Add(SessionWithMask(300, 0x1UL,
                    Rec("mixed.sys", 10, 501, 501, 1, 0x1UL)));
            for (int i = 0; i < 2; i++)
                mixedOverlap.Add(SessionWithMask(300, 0x1UL,
                    Rec("mixed.sys", 100000, 900000000, 1000000, 100000, 0x2UL)));
            v = IrqVerdict.Evaluate(mixedOverlap, 60, out used);
            Eq(3, v[0].SessionsOverThreshold);
            Eq(3L, v[0].OverlapOver500);
            Eq(false, v[0].Worth);

            // 一局的聚合掩码同时包含游戏核和核外时，无法证明慢 DPC 发生在哪边；
            // 即使数值很重也只能展示，不能参与建议评分。
            var mixedCpus = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                mixedCpus.Add(SessionWithMask(300, 0x1UL,
                    Rec("mixedcpu.sys", 100000, 900000000, 4000, 1000, 0x3UL)));
            v = IrqVerdict.Evaluate(mixedCpus, 60, out used);
            Eq(0, v[0].SessionsOverThreshold);
            Eq(0L, v[0].OverlapOver500);
            Eq(false, v[0].Worth);

            // 没有确认该局实际游戏核时可以展示原始数据，但不能参与建议评分。
            var unknownGameMaskSessions = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                unknownGameMaskSessions.Add(SessionWithMask(300, 0,
                    Rec("unknown.sys", 100000, 9000000, 4000, 900, 0x1UL)));
            v = IrqVerdict.Evaluate(unknownGameMaskSessions, 60, out used);
            Eq(3, used);
            Eq(1, v.Count);
            Eq(false, v[0].Worth);

            // 游戏实际覆盖全部可用核心时没有已知的核外目标，只展示，不建议挪核。
            var allCore = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
            {
                IrqSessionRecord s = SessionWithMask(300, 0xFFFFUL,
                    Rec("allcore.sys", 100000, 9000000, 4000, 900, 0x1UL));
                allCore.Add(s);
            }
            v = IrqVerdict.Evaluate(allCore, 60, out used);
            Eq(false, v[0].Worth);

            // 评分时长只统计该驱动已证明落在游戏核内的局，不能被两局无关长会话稀释。
            var scoredDuration = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                scoredDuration.Add(SessionWithMask(60, 0x1UL,
                    Rec("duration.sys", 100, 9000, 600, 6, 0x1UL)));
            for (int i = 0; i < 2; i++)
                scoredDuration.Add(SessionWithMask(36000, 0x1UL,
                    Rec("other.sys", 100, 9000, 100, 0, 0x2UL)));
            v = IrqVerdict.Evaluate(scoredDuration, 60, out used);
            IrqDriverVerdict durationVerdict = v.Find(delegate (IrqDriverVerdict x)
                { return x.Driver == "duration.sys"; });
            Eq(180.0, durationVerdict.ScoredSeconds);
            Eq(true, durationVerdict.Worth);

            // 损坏台账里同一局重复三条同驱动记录，也只能算一局。
            IrqDriverRecord duplicate = Rec("duplicate.sys", 1000, 900000, 4000, 30, 0x1UL);
            var duplicateRecords = new List<IrqSessionRecord>
            {
                Session(300, duplicate, duplicate, duplicate),
                Session(300, Rec("quiet2.sys", 1, 1, 1, 0, 0x2UL)),
                Session(300, Rec("quiet2.sys", 1, 1, 1, 0, 0x2UL))
            };
            v = IrqVerdict.Evaluate(duplicateRecords, 60, out used);
            IrqDriverVerdict duplicateVerdict = v.Find(delegate (IrqDriverVerdict x)
                { return x.Driver == "duplicate.sys"; });
            Eq(1, duplicateVerdict.SessionsSeen);
            Eq(false, duplicateVerdict.Worth);

            // 高刷屏帧预算更小 同样的中断更容易撞坏一帧
            var edge = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
                edge.Add(Session(300, Rec("e.sys", 9000, 900000, 900, 40, 0x3UL)));
            List<IrqDriverVerdict> at60 = IrqVerdict.Evaluate(edge, 60, out used);
            List<IrqDriverVerdict> at240 = IrqVerdict.Evaluate(edge, 240, out used);
            if (at240[0].Collisions <= at60[0].Collisions)
                throw new Exception("higher refresh rate should raise the collision estimate");
        }

        private static void TestIrqSessionLedgerRoundtrip(string dir)
        {
            string work = System.IO.Path.Combine(dir, "irq-ledger");
            System.IO.Directory.CreateDirectory(work);
            IrqSessionLedger.Bind(work);
            IrqSessionLedger.Clear();
            Eq(0, IrqSessionLedger.Load().Count);

            IrqSessionRecord one = Session(300,
                Rec("ndis.sys", 71379, 7382165, 1271, 576, 0x1UL));
            one.GameName = "带空格 和中文";
            one.BootStamp = "63922739545";
            Eq(true, IrqSessionLedger.Append(one));

            List<IrqSessionRecord> back = IrqSessionLedger.Load();
            Eq(1, back.Count);
            Eq("带空格 和中文", back[0].GameName);
            Eq("63922739545", back[0].BootStamp);
            Eq(300, back[0].DurationSeconds);
            Eq(0xFFUL, back[0].GameMask);
            Eq(0xFFFFUL, back[0].SystemMask);
            Eq(1, back[0].Drivers.Count);
            Eq("ndis.sys", back[0].Drivers[0].Driver);
            Eq(576L, back[0].Drivers[0].Over500Us);
            Eq(0x1UL, back[0].Drivers[0].CpuMask);
            // 纳秒整数往返 不能因为浮点丢精度
            Eq(1271L * 1000L, back[0].Drivers[0].DpcMaxNs);

            // 只保留最近 KeepSessions 局
            for (int i = 0; i < IrqSessionLedger.KeepSessions + 4; i++)
                IrqSessionLedger.Append(Session(300 + i, Rec("a.sys", 1, 1, 1, 0, 1UL)));
            Eq(IrqSessionLedger.KeepSessions, IrqSessionLedger.Load().Count);

            // V2 没有每局游戏核，直接作废，不做猜测或兼容迁移。
            string path = System.IO.Path.Combine(work, IrqSessionLedger.FileName);
            System.IO.File.WriteAllText(path, "PAVISE_IRQ_SESSIONS_V2\r\nS|1|300|xx|0|0|0|0\r\n");
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, System.IO.File.Exists(path));
            Eq(true, IrqSessionLedger.Append(Session(300, Rec("new.sys", 1, 1, 1, 0, 1UL))));
            if (System.IO.File.ReadAllText(path).IndexOf("PAVISE_IRQ_SESSIONS_V4", StringComparison.Ordinal) < 0)
                throw new Exception("obsolete IRQ ledger was not replaced with V4");

            // 刷新页读取与对局结束写入会并发；V2 作废不能删掉另一线程刚写出的 V4。
            for (int i = 0; i < 16; i++)
            {
                System.IO.File.WriteAllText(path, "PAVISE_IRQ_SESSIONS_V2\r\nS|1|300|xx|0|0|0|0\r\n");
                bool appended = false;
                Exception threadFailure = null;
                var start = new System.Threading.ManualResetEvent(false);
                var reader = new System.Threading.Thread(delegate()
                {
                    start.WaitOne();
                    try { IrqSessionLedger.Load(); }
                    catch (Exception ex) { threadFailure = ex; }
                });
                var writer = new System.Threading.Thread(delegate()
                {
                    start.WaitOne();
                    try { appended = IrqSessionLedger.Append(
                        Session(300, Rec("race.sys", 1, 1, 1, 0, 1UL))); }
                    catch (Exception ex) { threadFailure = ex; }
                });
                reader.Start();
                writer.Start();
                start.Set();
                reader.Join();
                writer.Join();
                start.Dispose();
                if (threadFailure != null) throw threadFailure;
                Eq(true, appended);
                Eq(1, IrqSessionLedger.Load().Count);
            }

            // 短暂读冲突不能被当成“历史为空”再覆盖成只剩当前一局。
            string beforeLockedAppend = System.IO.File.ReadAllText(path);
            using (var held = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                Eq(false, IrqSessionLedger.Append(
                    Session(300, Rec("locked.sys", 1, 1, 1, 0, 1UL))));
            }
            Eq(beforeLockedAppend, System.IO.File.ReadAllText(path));
            Eq(0, System.IO.Directory.GetFiles(work,
                IrqSessionLedger.FileName + ".*.tmp").Length);

            // 任意一行损坏都不允许把可解析的前半截拿去评分，也不允许 Append
            // 把损坏文件覆盖掉。损坏的新 S 还必须切断上一局，不能让后续 D 串局。
            string malformed = "PAVISE_IRQ_SESSIONS_V3\r\n"
                + "S|1|300|VGVzdA==|boot|topo|0|0|FF|FFFF\r\n"
                + "D|eC5zeXM=||1|1000|1000|1|0|1|0|\r\n"
                + "S|broken\r\n"
                + "D|eS5zeXM=||1|1000|1000|1|0|1|0|\r\n";
            System.IO.File.WriteAllText(path, malformed);
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(
                Session(300, Rec("after-corrupt.sys", 1, 1, 1, 0, 1UL))));
            Eq(malformed, System.IO.File.ReadAllText(path));

            string badFlag = "PAVISE_IRQ_SESSIONS_V3\r\n"
                + "S|1|300|VGVzdA==|boot|topo|0|0|FF|FFFF\r\n"
                + "D|eC5zeXM=||1|1000|1000|1|0|1|x|\r\n";
            System.IO.File.WriteAllText(path, badFlag);
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(
                Session(300, Rec("bad-flag.sys", 1, 1, 1, 0, 1UL))));
            Eq(badFlag, System.IO.File.ReadAllText(path));

            string badBase64 = "PAVISE_IRQ_SESSIONS_V3\r\n"
                + "S|1|300|***|boot|topo|0|0|FF|FFFF\r\n";
            System.IO.File.WriteAllText(path, badBase64);
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(
                Session(300, Rec("bad-base64.sys", 1, 1, 1, 0, 1UL))));
            Eq(badBase64, System.IO.File.ReadAllText(path));

            string badUtf8 = "PAVISE_IRQ_SESSIONS_V3\r\n"
                + "S|1|300|/w==|boot|topo|0|0|FF|FFFF\r\n";
            System.IO.File.WriteAllText(path, badUtf8);
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(
                Session(300, Rec("bad-utf8.sys", 1, 1, 1, 0, 1UL))));
            Eq(badUtf8, System.IO.File.ReadAllText(path));

            string extraField = "PAVISE_IRQ_SESSIONS_V3\r\n"
                + "S|1|300|VGVzdA==|boot|topo|0|0|FF|FFFF|future\r\n";
            System.IO.File.WriteAllText(path, extraField);
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(
                Session(300, Rec("extra-field.sys", 1, 1, 1, 0, 1UL))));
            Eq(extraField, System.IO.File.ReadAllText(path));

            var invalidFileUtf8 = new List<byte>(System.Text.Encoding.ASCII.GetBytes(
                "PAVISE_IRQ_SESSIONS_V3\r\nS|1|300|VGVzdA==|"));
            invalidFileUtf8.Add(0xFF);
            invalidFileUtf8.AddRange(System.Text.Encoding.ASCII.GetBytes(
                "|topo|0|0|FF|FFFF\r\n"));
            byte[] invalidFileBytes = invalidFileUtf8.ToArray();
            System.IO.File.WriteAllBytes(path, invalidFileBytes);
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(
                Session(300, Rec("bad-file-utf8.sys", 1, 1, 1, 0, 1UL))));
            byte[] keptInvalidFileBytes = System.IO.File.ReadAllBytes(path);
            Eq(invalidFileBytes.Length, keptInvalidFileBytes.Length);
            for (int i = 0; i < invalidFileBytes.Length; i++)
                if (invalidFileBytes[i] != keptInvalidFileBytes[i])
                    throw new Exception("invalid UTF-8 ledger was rewritten");

            // 认不出的头一律只读 不许把新版数据碾成旧格式
            System.IO.File.WriteAllText(path, "PAVISE_IRQ_SESSIONS_V9\r\nS|1|300|xx|0|0\r\n");
            Eq(0, IrqSessionLedger.Load().Count);
            Eq(false, IrqSessionLedger.Append(Session(300, Rec("b.sys", 1, 1, 1, 0, 1UL))));
            string still = System.IO.File.ReadAllText(path);
            if (still.IndexOf("PAVISE_IRQ_SESSIONS_V9", StringComparison.Ordinal) < 0)
                throw new Exception("an unknown newer ledger format was overwritten");

            // A 目录的未知新版只读状态不能污染随后绑定的 B 目录。
            string work2 = System.IO.Path.Combine(dir, "irq-ledger-second");
            System.IO.Directory.CreateDirectory(work2);
            IrqSessionLedger.Bind(work2);
            IrqSessionLedger.Clear();
            Eq(true, IrqSessionLedger.Append(
                Session(300, Rec("fresh.sys", 1, 1, 1, 0, 1UL))));
            Eq(1, IrqSessionLedger.Load().Count);
            IrqSessionLedger.Clear();

            IrqSessionLedger.Bind(work);
            IrqSessionLedger.Clear();
        }
    }
}
