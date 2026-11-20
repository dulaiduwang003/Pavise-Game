using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int hardPinRestoreRequested;
        // 等线程恢复的观测上限 和游戏亲和性 Job 的顺序等待一致 约两分钟
        private const int HardPinOrderingObservationLimit = 120;

        // Job 释放还在等线程恢复或别的恢复操作 属于顺序等待 不是这次恢复失败
        private static bool HardPinOrderingReason(string reason)
        {
            return reason == "thread-recovery-pending" || reason == "recovery-operation-pending";
        }

        private void RequestHardPinRestoreLocked(HardPin pin, bool wake = true)
        {
            bool firstRequest = !pin.RestoreRequested;
            pin.RestoreRequested = true;
            corePinPending = true;
            Interlocked.Exchange(ref hardPinRestoreRequested, 1);
            if (wake && firstRequest && kick != null) kick.Set();
        }

        // 复用现有工作线程 criticalProcessGate 仍是唯一恢复锁 恢复任务不能超出退出流程 也不能与替代写入方并发

        private void RetryPendingHardPinRestores()
        {
            if (Monitor.IsEntered(sync) || Interlocked.Exchange(ref hardPinRestoreRequested, 0) == 0) return;
            var pending = new List<KeyValuePair<int, HardPin>>();
            lock (sync)
                foreach (KeyValuePair<int, HardPin> pair in hardPins)
                    if (pair.Value.RestoreRequested) pending.Add(pair);
            foreach (KeyValuePair<int, HardPin> pair in pending)
            {
                lock (sync)
                {
                    HardPin current;
                    if (!hardPins.TryGetValue(pair.Key, out current) || !object.ReferenceEquals(current, pair.Value)) continue;
                }
                RestoreHardPin(pair.Key, pair.Value);
            }
            lock (sync)
            {
                bool requested = false;
                foreach (HardPin pin in hardPins.Values) requested |= pin.RestoreRequested;
                if (requested) Interlocked.Exchange(ref hardPinRestoreRequested, 1);
                corePinPending = requested || coreAffinityJobs.HasPending;
            }
        }
    }
}
