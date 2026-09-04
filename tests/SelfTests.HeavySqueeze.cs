#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const long SqMs = TimeSpan.TicksPerMillisecond;

        internal static void RunHeavySqueezeRegressionTests()
        {
            HeavySqueezeTargetRules();
            HeavySqueezeDesiredAffinityFollowsBackgroundReasonOnly();
            HeavySqueezeHeatNeedsSustainedLoad();
            HeavySqueezeDipResetsHotTimer();
            HeavySqueezeColdReleaseNeedsSustainedIdle();
            HeavySqueezeIdleProcessNeverHot();
            HeavySqueezeSnapshotReuseDoesNotAdvance();
            HeavySqueezePidReuseAndPruneReset();
            HeavySqueezeRefusalBackoff();
            HeavySqueezeCatalogAndDefaults();
        }

        private static void HeavySqueezeRefusalBackoff()
        {
            var t = new HeavySqueezeTracker();
            long now = 0, cpu = 0;
            t.Observe(12, 1, cpu, now);
            for (int i = 0; i <= HeavySqueezePolicy.HotHoldSeconds * 2; i++) Feed(t, 12, 1, ref now, ref cpu, 100);
            Eq(true, t.IsHot(12));
            Eq(false, t.IsRefused(12, now));
            t.Refuse(12, now + 30 * TimeSpan.TicksPerSecond);
            Eq(true, t.IsRefused(12, now));
            Eq(true, t.IsRefused(12, now + 29 * TimeSpan.TicksPerSecond));
            Eq(false, t.IsRefused(12, now + 30 * TimeSpan.TicksPerSecond));
            // 退避不影响热度本身 也随 PID 复用一起清掉
            Eq(true, t.IsHot(12));
            Eq(false, t.Observe(12, 2, 0, now + 500 * SqMs));
            Eq(false, t.IsRefused(12, now + 1000 * SqMs));
            // 没见过的 pid 不算退避
            t.Refuse(99, now + SqMs);
            Eq(false, t.IsRefused(99, now));
        }

        private static void HeavySqueezeTargetRules()
        {
            ulong all = 0xFFF, squeeze = 0xC00;
            Eq(squeeze, HeavySqueezePolicy.SqueezeTarget(squeeze, 0, new uint[0], all, false));
            Eq(squeeze, HeavySqueezePolicy.SqueezeTarget(squeeze, all, null, all, false));
            Eq(squeeze, HeavySqueezePolicy.SqueezeTarget(squeeze, 0xFF0, new uint[0], all, false));
            // 多处理器组 空掩码 空全集 一律不绑
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(squeeze, 0, new uint[0], all, true));
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(0, 0, new uint[0], all, false));
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(squeeze, 0, new uint[0], 0, false));
            // 进程自己收过亲和且不含落点 或落点就是它的全部范围 或它自己设过 CPU Sets
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(squeeze, 0x0F0, new uint[0], all, false));
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(squeeze, 0x800, new uint[0], all, false));
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(squeeze, squeeze, new uint[0], all, false));
            Eq(0UL, HeavySqueezePolicy.SqueezeTarget(squeeze, 0, new uint[] { 3, 4 }, all, false));
        }

        private static void HeavySqueezeDesiredAffinityFollowsBackgroundReasonOnly()
        {
            ulong all = 0xFFF, squeeze = 0xC00;
            Eq(squeeze, HeavySqueezePolicy.DesiredAffinity(SuppressReason.Background, squeeze, 0, all));
            // 反作弊条目的落点由 Tamer 在压制落地后下 带反作弊原因时同样按落点走
            Eq(squeeze, HeavySqueezePolicy.DesiredAffinity(
                SuppressReason.Background | SuppressReason.AntiCheat, squeeze, 0, all));
            Eq(squeeze, HeavySqueezePolicy.DesiredAffinity(SuppressReason.AntiCheat, squeeze, 0, all));
            // 没有落点时回原值 原值为空回全集
            Eq(all, HeavySqueezePolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0, all));
            Eq(0x0F0UL, HeavySqueezePolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0x0F0, all));
            Eq(0x0F0UL, HeavySqueezePolicy.DesiredAffinity(SuppressReason.Background, 0, 0x0F0, all));
            // 没有任何压制原因的条目不许带落点
            Eq(all, HeavySqueezePolicy.DesiredAffinity(SuppressReason.None, squeeze, 0, all));
            Eq(all, HeavySqueezePolicy.DesiredAffinity(SuppressReason.None, 0, 0, all));
        }

        // 500ms 一轮 占用按每轮 CPU 时间喂 hot 要连续超过阈值满 10 秒
        private static bool Feed(HeavySqueezeTracker t, int pid, long creation, ref long now, ref long cpu, int utilPercent)
        {
            now += 500 * SqMs;
            cpu += 5 * utilPercent * SqMs;
            return t.Observe(pid, creation, cpu, now);
        }

        private static void HeavySqueezeHeatNeedsSustainedLoad()
        {
            var t = new HeavySqueezeTracker();
            long now = 1000L * SqMs, cpu = 0;
            Eq(false, t.Observe(7, 42, cpu, now));
            int hold = HeavySqueezePolicy.HotHoldSeconds * 2;
            for (int i = 0; i < hold; i++)
                Eq(false, Feed(t, 7, 42, ref now, ref cpu, 60));
            Eq(true, Feed(t, 7, 42, ref now, ref cpu, 60));
            Eq(true, t.IsHot(7));
            // 恰好压线的 50% 也算热
            var edge = new HeavySqueezeTracker();
            long enow = 0, ecpu = 0;
            edge.Observe(9, 1, ecpu, enow);
            for (int i = 0; i < hold; i++) Feed(edge, 9, 1, ref enow, ref ecpu, HeavySqueezePolicy.HotUtilPercent);
            Eq(true, Feed(edge, 9, 1, ref enow, ref ecpu, HeavySqueezePolicy.HotUtilPercent));
        }

        private static void HeavySqueezeDipResetsHotTimer()
        {
            var t = new HeavySqueezeTracker();
            long now = 0, cpu = 0;
            t.Observe(8, 1, cpu, now);
            int hold = HeavySqueezePolicy.HotHoldSeconds * 2;
            for (int i = 0; i < hold - 2; i++) Feed(t, 8, 1, ref now, ref cpu, 90);
            Eq(false, Feed(t, 8, 1, ref now, ref cpu, 20));
            for (int i = 0; i < hold; i++)
                Eq(false, Feed(t, 8, 1, ref now, ref cpu, 90));
            Eq(true, Feed(t, 8, 1, ref now, ref cpu, 90));
        }

        private static void HeavySqueezeColdReleaseNeedsSustainedIdle()
        {
            var t = new HeavySqueezeTracker();
            long now = 0, cpu = 0;
            t.Observe(5, 1, cpu, now);
            int hotHold = HeavySqueezePolicy.HotHoldSeconds * 2;
            for (int i = 0; i <= hotHold; i++) Feed(t, 5, 1, ref now, ref cpu, 100);
            Eq(true, t.IsHot(5));
            int coldHold = HeavySqueezePolicy.ColdHoldSeconds * 2;
            // 一半冷却后又忙起来 冷却计时重置
            for (int i = 0; i < coldHold / 2; i++) Eq(true, Feed(t, 5, 1, ref now, ref cpu, 2));
            Eq(true, Feed(t, 5, 1, ref now, ref cpu, 30));
            for (int i = 0; i < coldHold; i++) Eq(true, Feed(t, 5, 1, ref now, ref cpu, 2));
            Eq(false, Feed(t, 5, 1, ref now, ref cpu, 2));
            Eq(false, t.IsHot(5));
            // 解除后重新变热要重新满 10 秒
            for (int i = 0; i < hotHold; i++) Eq(false, Feed(t, 5, 1, ref now, ref cpu, 100));
            Eq(true, Feed(t, 5, 1, ref now, ref cpu, 100));
        }

        private static void HeavySqueezeIdleProcessNeverHot()
        {
            var t = new HeavySqueezeTracker();
            long now = 0, cpu = 0;
            t.Observe(3, 1, cpu, now);
            for (int i = 0; i < 200; i++)
                Eq(false, Feed(t, 3, 1, ref now, ref cpu, HeavySqueezePolicy.HotUtilPercent - 1));
            Eq(false, t.IsHot(3));
        }

        private static void HeavySqueezeSnapshotReuseDoesNotAdvance()
        {
            var t = new HeavySqueezeTracker();
            long now = 0, cpu = 0;
            t.Observe(4, 1, cpu, now);
            int hold = HeavySqueezePolicy.HotHoldSeconds * 2;
            for (int i = 0; i < hold; i++) Feed(t, 4, 1, ref now, ref cpu, 100);
            // 同一份快照重复喂 时间没走 CPU 也没走 状态不推进
            Eq(false, t.Observe(4, 1, cpu, now));
            Eq(false, t.Observe(4, 1, cpu, now));
            // 间隔不足 250ms 的样本不结算 累积到下一次一起算
            now += 100 * SqMs; cpu += 100 * SqMs;
            Eq(false, t.Observe(4, 1, cpu, now));
            now += 400 * SqMs; cpu += 400 * SqMs;
            Eq(true, t.Observe(4, 1, cpu, now));
        }

        private static void HeavySqueezePidReuseAndPruneReset()
        {
            var t = new HeavySqueezeTracker();
            long now = 0, cpu = 0;
            t.Observe(6, 1, cpu, now);
            int hold = HeavySqueezePolicy.HotHoldSeconds * 2;
            for (int i = 0; i <= hold; i++) Feed(t, 6, 1, ref now, ref cpu, 100);
            Eq(true, t.IsHot(6));
            // 同 PID 新创建时间 一律当新进程 从基线重来
            Eq(false, t.Observe(6, 2, 0, now + 500 * SqMs));
            Eq(false, t.IsHot(6));
            Eq(1, t.Count);
            t.Observe(11, 1, 0, now);
            Eq(2, t.Count);
            t.Prune(new HashSet<int> { 11 });
            Eq(1, t.Count);
            Eq(false, t.IsHot(6));
            t.Clear();
            Eq(0, t.Count);
        }

        private static void HeavySqueezeCatalogAndDefaults()
        {
            PolicyItem item = PolicyCatalog.ItemOf(PolicyCatalog.KeyHeavySqueeze);
            Eq(true, item != null);
            Eq("0", item.Fallback);
            Eq(PolicyCatalog.GroupBackground, item.GroupKey);
            Eq("0", PolicyCatalog.Canonical(PolicyCatalog.KeyHeavySqueeze, "false"));
            Eq("1", PolicyCatalog.Canonical(PolicyCatalog.KeyHeavySqueeze, "true"));
            // 旧键 GmSqueezeBg 的值不能渗进来 键名本身就不同
            Eq(true, PolicyCatalog.KeyHeavySqueeze != "GmSqueezeBg");
            Eq(true, Lang.HasKey("gm.squeeze") && Lang.HasKey("gm.squeeze.sub")
                && Lang.HasKey("squeeze.warn") && Lang.HasKey("gm.squeeze.unsupported"));
            for (int i = 1; i <= 6; i++) Eq(true, Lang.HasKey("log.squeeze." + i));
            // 实验项不进极限档的强制清单
            foreach (string key in ExtremeMode.SessionPolicyKeys)
                Eq(true, key != PolicyCatalog.KeyHeavySqueeze);
        }
    }
}
#endif
