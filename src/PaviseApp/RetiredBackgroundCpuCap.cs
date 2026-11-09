namespace PaviseApp
{
    // 启动时清理已退役的 CPU 上限和冻结记录，最后一个所有者句柄关闭后，其命名 Job 无法再次打开。

    internal static class RetiredBackgroundCpuCap
    {
        internal const string ReceiptKey = "BackgroundCpuCapGroupsV2";
        internal const string LegacyReceiptKey = "BackgroundCpuCapJobsV1";

        internal static bool HasReceipts()
        {
            string value;
            return (Settings.TryLoadStr(ReceiptKey, out value) && !string.IsNullOrEmpty(value))
                || (Settings.TryLoadStr(LegacyReceiptKey, out value) && !string.IsNullOrEmpty(value));
        }

        internal static bool Retire()
        {
            if (!HasReceipts()) return true;
            Settings.Remove(ReceiptKey);
            Settings.Remove(LegacyReceiptKey);
            bool clean = !HasReceipts();
            if (clean) Logger.Info("启动 事件=旧CPU限制回执 结果=已移除");
            else Logger.Warn("启动 事件=旧CPU限制回执 结果=已保留");
            return clean;
        }
    }
}
