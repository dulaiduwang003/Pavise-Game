using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private BackgroundRecoveryLedger backgroundRecoveryAccounting;

        private void BeginBackgroundRecoveryAccounting()
        {
            BackgroundRecoveryLedger previous = CaptureBackgroundRecoveryAccounting();
            BackgroundRecoveryLedger ledger = core.BeginBackgroundRecoveryAccounting(Interlocked.Read(ref gameSessionStarts));
            lock (sync) backgroundRecoveryAccounting = ledger;
            // 游戏 A 直接切换到 B 不经过 Deactivate；旧段先发布，晚到恢复仍更新原段。
            string fields;
            if (previous != null && previous.TryPublishFields(out fields))
                Logger.Info("后台恢复统计段结束 原因=会话切换 " + fields);
        }

        private BackgroundRecoveryLedger CaptureBackgroundRecoveryAccounting()
        {
            lock (sync) return backgroundRecoveryAccounting;
        }

        private static void LogBackgroundRecoveryAccounting(string reason, BackgroundRecoveryLedger ledger)
        {
            if (ledger == null) return;
            Logger.Log(Lang.T("log.gamemodeboost.52", 0) + reason + " " + ledger.PublishFields());
        }
    }
}
