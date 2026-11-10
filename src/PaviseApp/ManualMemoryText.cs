using System.Globalization;

namespace PaviseApp
{
    internal static class ManualMemoryText
    {
        internal static string Title
        {
            get
            {
                return T("极限内存清理", "Extreme memory cleanup");
            }
        }

        internal static string Button
        {
            get
            {
                return T("极限清理", "Deep clean");
            }
        }

        internal static string Hint
        {
            get
            {
                return T("全量回收", "Full cleanup");
            }
        }

        internal static string Permission
        {
            get
            {
                return T("需要管理员权限", "Administrator rights required");
            }
        }
        internal static string GameButton { get { return T("轻量清理", "Light clean"); } }
        internal static string GameHint { get { return T("游戏中 · 仅回收 Pavise 自身内存", "In game · Pavise memory only"); } }

        internal static string T(string chinese, string english)
        { return Lang.Cur == 1 ? english : chinese; }

        internal static string Stage(ManualMemoryStep step)
        {
            switch (step)
            {
            case ManualMemoryStep.PaviseMemory:
                return GameHint;
            case ManualMemoryStep.CombinePages:
                return T("合并重复页", "Combining duplicate pages");
            case ManualMemoryStep.WorkingSets:
                return T("回收全局工作集", "Trimming all working sets");
            case ManualMemoryStep.FileCache:
                return T("整理系统文件缓存", "Trimming system file cache");
            case ManualMemoryStep.RegistryCache:
                return T("整理注册表缓存", "Reconciling registry cache");
            case ManualMemoryStep.ModifiedPages:
                return T("请求修改页写回", "Requesting modified-page writeback");
            default:
                return T("清空全部待机缓存", "Purging all standby lists");
            }
        }

        internal static string Capacity(ulong bytes)
        {
            return ((double)bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        internal static string Delta(long value)
        {
            return ((value >= 0) ? "+" : "-") + Capacity((ulong)((value >= 0) ? value : (-value)));
        }

        internal static string Outcome(ManualMemoryReport report)
        {
            if (report.Busy)
            {
                return T("已有清理正在运行", "A cleanup is already running");
            }
            if (report.Cancelled)
            {
                return T("清理已中止", "Cleanup stopped");
            }
            if (report.GameProtected && report.Completed)
                return T("轻量回收完成", "Light cleanup completed");
            if (!report.Completed)
            {
                if (report.SucceededCount <= 0)
                {
                    return T("清理未完成", "Cleanup incomplete");
                }
                return T("部分完成", "Partially completed");
            }
            return T("清理完成", "Completed");
        }

        internal static string Summary(ManualMemoryReport report)
        {
            if (report.GameProtected) return Outcome(report) + " · " + GameHint;
            long? availableChangeBytes = report.AvailableChangeBytes;
            if (!availableChangeBytes.HasValue)
            {
                return Outcome(report) + " · " + T("内存读数不可用", "Memory readings unavailable");
            }
            return T("可用 ", "Available ") + Delta(availableChangeBytes.Value) + (report.Completed ? "" : (" · " + Outcome(report)));
        }
    }
}
