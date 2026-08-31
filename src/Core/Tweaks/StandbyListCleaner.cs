// @author bdth 2074055628@qq.com
// 文件用途 双阈值待机清理策略 调度归 GameMode 管 不在这个引擎里
using System;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class StandbyCleanerOptions
    {
        public const int MaximumMegabytes = 1048576;
        public const int MinimumPollingMilliseconds = 250;
        public const int MaximumPollingMilliseconds = 300000;

        // 这是 Pavise 自己的默认值 不要说成某个第三方清理工具
        // 验证过的出厂设置
        private static readonly StandbyCleanerOptions defaults =
            new StandbyCleanerOptions(1024, 1024, 4000);
        // 参数弹窗的一键档位 仅填入输入框 保存仍走同一提交边界
        //   保守 = 缓存很大且空闲确实吃紧才清 激进 = 提早且更勤地清
        private static readonly StandbyCleanerOptions conservative =
            new StandbyCleanerOptions(2048, 512, 8000);
        private static readonly StandbyCleanerOptions aggressive =
            new StandbyCleanerOptions(512, 1536, 2000);

        private readonly int listMegabytes, freeMegabytes, pollingMilliseconds;

        public StandbyCleanerOptions(int listMegabytes, int freeMegabytes, int pollingMilliseconds)
        {
            this.listMegabytes = listMegabytes;
            this.freeMegabytes = freeMegabytes;
            this.pollingMilliseconds = pollingMilliseconds;
        }

        public static StandbyCleanerOptions Default { get { return defaults; } }
        public static StandbyCleanerOptions Conservative { get { return conservative; } }
        public static StandbyCleanerOptions Aggressive { get { return aggressive; } }
        public int ListMegabytes { get { return listMegabytes; } }
        public int FreeMegabytes { get { return freeMegabytes; } }
        public int PollingMilliseconds { get { return pollingMilliseconds; } }

        public bool IsValid
        {
            get
            {
                return listMegabytes >= 0 && listMegabytes <= MaximumMegabytes
                    && freeMegabytes >= 0 && freeMegabytes <= MaximumMegabytes
                    && pollingMilliseconds >= MinimumPollingMilliseconds
                    && pollingMilliseconds <= MaximumPollingMilliseconds;
            }
        }

        public string Serialize()
        {
            if (!IsValid) throw new InvalidOperationException("Invalid standby cleaner options.");
            return "1|" + listMegabytes.ToString(CultureInfo.InvariantCulture)
                + "|" + freeMegabytes.ToString(CultureInfo.InvariantCulture)
                + "|" + pollingMilliseconds.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string value, out StandbyCleanerOptions options)
        {
            options = null;
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            string[] parts = value.Split('|');
            int list, free, polling;
            if (parts.Length != 4 || parts[0] != "1"
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out list)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out free)
                || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out polling))
                return false;
            var parsed = new StandbyCleanerOptions(list, free, polling);
            if (!parsed.IsValid) return false;
            options = parsed;
            return true;
        }
    }

    internal enum StandbyCleanerResult
    {
        Cancelled,
        BelowThreshold,
        Purged,
        QueryFailed,
        PurgeFailed,
        InvalidOptions
    }

    internal sealed class StandbyCleanerEngine
    {
        internal const int StatusCancelled = unchecked((int)0xC0000120);
        internal const int StatusUnsuccessful = unchecked((int)0xC0000001);
        internal const int StatusInvalidParameter = unchecked((int)0xC000000D);

        private readonly IStandbyMemoryControl control;
        private readonly Func<Func<bool>, bool> runMutation;
        private readonly object gate = new object();
        private bool polling;

        public StandbyCleanerEngine(IStandbyMemoryControl control,
            Func<Func<bool>, bool> runMutation = null)
        {
            if (control == null) throw new ArgumentNullException("control");
            this.control = control;
            this.runMutation = runMutation;
        }

        public StandbyCleanerResult Poll(StandbyCleanerOptions options, Func<bool> mayContinue,
            out StandbyMemorySnapshot snapshot, out int nativeStatus)
        {
            lock (gate)
            {
                snapshot = null;
                nativeStatus = 0;
                // Monitor 是可重入的 协调器或者测试回调不能在同一轮轮询里
                // 递归采样并再发一次清理
                if (polling) return Cancelled(out nativeStatus);
                polling = true;
                try
                {
                    return PollCore(options, mayContinue, out snapshot, out nativeStatus);
                }
                finally { polling = false; }
            }
        }

        private StandbyCleanerResult PollCore(StandbyCleanerOptions options, Func<bool> mayContinue,
            out StandbyMemorySnapshot snapshot, out int nativeStatus)
        {
            snapshot = null;
            nativeStatus = 0;
            if (options == null || !options.IsValid)
            {
                nativeStatus = StatusInvalidParameter;
                return StandbyCleanerResult.InvalidOptions;
            }
            if (!MayContinue(mayContinue)) return Cancelled(out nativeStatus);

            bool queried;
            try { queried = control.TryQuery(out snapshot); }
            catch { queried = false; }
            if (!MayContinue(mayContinue)) return Cancelled(out nativeStatus);
            if (!queried || !ValidSnapshot(snapshot))
            {
                snapshot = null;
                nativeStatus = StatusUnsuccessful;
                return StandbyCleanerResult.QueryFailed;
            }

            ulong listLimit = (ulong)options.ListMegabytes * 1048576UL;
            ulong freeLimit = (ulong)options.FreeMegabytes * 1048576UL;
            if (snapshot.ListBytes < listLimit || snapshot.FreeBytes >= freeLimit)
                return StandbyCleanerResult.BelowThreshold;

            // 改动协调器必须同步调用它 等待中断期间 它可能
            // 拒掉一个已取消或过期的游戏代号
            // 就算取消紧随其后 已观察到的原生结果也要保留
            bool called = false, purged = false;
            int status = StatusCancelled;
            Func<bool> purge = delegate
            {
                if (called) return purged;
                called = true;
                if (!MayContinue(mayContinue)) return false;
                try { purged = control.TryPurge(mayContinue, out status); }
                catch { status = StatusUnsuccessful; }
                return purged;
            };
            try
            {
                if (runMutation == null) purge();
                else runMutation(purge);
            }
            catch
            {
                if (!purged && status == 0) status = StatusUnsuccessful;
                else if (!purged && status == StatusCancelled && called)
                    status = StatusUnsuccessful;
            }

            nativeStatus = status;
            if (purged) return StandbyCleanerResult.Purged;
            if (!called || status == StatusCancelled) return Cancelled(out nativeStatus);
            if (nativeStatus == 0) nativeStatus = StatusUnsuccessful;
            return StandbyCleanerResult.PurgeFailed;
        }

        internal static bool ValidSnapshot(StandbyMemorySnapshot snapshot)
        {
            if (snapshot == null || snapshot.TotalBytes == 0) return false;
            ulong total = snapshot.TotalBytes;
            return snapshot.AvailableBytes <= total && snapshot.FreeBytes <= total
                && snapshot.StandbyBytes <= total && snapshot.ListBytes <= total
                && snapshot.FreeBytes <= total - snapshot.StandbyBytes;
            // Available/List 和 Free/Standby 来自两次相邻的查询
            // 不要要求跨查询的顺序关系 比如 Free 小于等于 Available
        }

        internal static bool MayContinue(Func<bool> mayContinue)
        {
            if (mayContinue == null) return true;
            try { return mayContinue(); }
            catch { return false; }
        }

        private static StandbyCleanerResult Cancelled(out int nativeStatus)
        {
            nativeStatus = StatusCancelled;
            return StandbyCleanerResult.Cancelled;
        }
    }
}
