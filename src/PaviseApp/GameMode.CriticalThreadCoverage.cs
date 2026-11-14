using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void ConfigureCriticalThreadReassertion(CriticalRuntimeSource source)
        {
            criticalSession.BeforeReassertion = delegate
            {
                // 自动和手动规则共用同局预算；重新读回成功或规则切回都不能补充纠偏次数。
                return !criticalStability.RecordReassertion(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
            };
        }

        private void PublishCriticalWorkerPending(CriticalRuntimeSource source, int workers, int pendingWorkers, bool initial, long observed)
        {
            criticalObservationMisses = 0;
            // 已核验成员继续受容量护栏约束，新线程出现不能延后辅助池的过载判断。
            if (!AuditElasticAuxiliaryCapacity(source, initial)) return;
            if (threadLayoutCapture != null) threadLayoutCapture.MarkApplied(AutoCores.NowTicks);
            PublishCriticalThreadState(source, "coverage-pending", ThreadLayoutDescriptions.Text(
                "已核验绑定保留；新增 " + pendingWorkers + " 个普通线程等待下轮接管，当前覆盖未完整",
                "Verified bindings retained; " + pendingWorkers + " new worker(s) await the next audit. Coverage is incomplete.",
                "確認済みの固定を維持します。新しい作業スレッド " + pendingWorkers + " 個は次回確認時に適用し、現在の範囲は未完了です。"),
                true, false, workers, observed, CriticalThreadCoverageState.WorkersPartial);
        }
    }
}
