using System;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private CriticalThreadAuxiliaryObservation criticalAuxiliaryObservation = new CriticalThreadAuxiliaryObservation();
        private CriticalThreadAuxiliaryPlan criticalAuxiliaryPlan;
        private string criticalAuxiliaryIdentity;
        private long criticalAuxiliaryGuardTick;
        private int criticalAuxiliaryOverloadWindows;

        private void ResetElasticAuxiliary()
        {
            ResetElasticAuxiliaryObservation();
            criticalAuxiliaryPlan = null;
            criticalAuxiliaryIdentity = null;
        }

        private void ResetElasticAuxiliaryObservation()
        {
            if (criticalAuxiliaryObservation != null) criticalAuxiliaryObservation.Reset();
            criticalAuxiliaryGuardTick = 0;
            criticalAuxiliaryOverloadWindows = 0;
        }

        private void PrepareElasticAuxiliary(CriticalRuntimeSource source, HardThreadChoice primary, CriticalAutoTopology topology)
        {
            if (criticalAuxiliaryObservation == null) criticalAuxiliaryObservation = new CriticalThreadAuxiliaryObservation();
            CriticalThreadAuxiliaryEvidence[] evidence = threadLayoutCapture == null
                ? new CriticalThreadAuxiliaryEvidence[0] : threadLayoutCapture.ReadAuxiliaryEvidence();
            string[] directWorkers = evidence.Where(item => item != null && item.Relation != null && item.Relation.Valid
                && item.Relation.Pid == source.Pid && item.Relation.Birth == source.Birth
                && item.Relation.Presenter != null && item.Relation.Presenter.Tid == primary.Thread.Tid
                && item.Relation.Presenter.Creation == primary.Thread.Creation && item.Relation.Worker != null)
                .Select(item => new HardThreadIdentity(item.Relation.Worker.Tid, item.Relation.Worker.Creation).Key).Distinct().ToArray();
            CriticalThreadAuxiliaryCandidate[] candidates = criticalAuxiliaryObservation.Candidates()
                .Where(item => directWorkers.Contains(item.Thread.Key)).ToArray();
            foreach (CriticalThreadAuxiliaryCandidate candidate in candidates)
            {
                ulong mask;
                if (ThreadLayoutReadback.TryReadThreadMask(source.Pid, source.Birth, candidate.Thread, out mask))
                    candidate.AllowedMask = mask & topology.Allowed;
            }
            criticalAuxiliaryPlan = CriticalThreadAuxiliaryPolicy.Select(new CriticalThreadAuxiliaryInput {
                Pid = source.Pid, Birth = source.Birth, Now = AutoCores.NowTicks, Primary = primary.Thread,
                PrimaryMask = primary.Mask, Physical = topology.Physical, Domains = topology.Domains,
                Allowed = topology.Allowed, Reserved = topology.Reserved, Favored = CpuTopology.FavoredMask,
                Performance = topology.Performance, Crowded = CpuTopology.ThrottleMask,
                Candidates = candidates, Evidence = evidence, ConcurrencyKnown = false, MaximumConcurrentRunnable = 0 });
            criticalAuxiliaryIdentity = ThreadLayoutIdentity(source);
            criticalAuxiliaryGuardTick = criticalAuxiliaryObservation.LatestTicks;
            criticalAuxiliaryOverloadWindows = 0;
            Logger.Info("线程调度 辅助池准入 PID=" + source.Pid + " 创建=" + source.Birth + "；" + criticalAuxiliaryPlan.Detail
                + "；本阶段冻结成员和范围");
        }

        private HardThreadChoice[] ElasticWorkerPlacements(CriticalRuntimeSource source)
        {
            return source.Rule.Layout == CriticalThreadLayout.ElasticAuxiliary && criticalAuxiliaryPlan != null
                && criticalAuxiliaryIdentity == ThreadLayoutIdentity(source)
                ? (HardThreadChoice[])criticalAuxiliaryPlan.WorkerPlacements.Clone() : new HardThreadChoice[0];
        }

        private string ElasticAuxiliaryDescription(CriticalRuntimeSource source)
        {
            HardThreadChoice[] members = ElasticWorkerPlacements(source);
            if (members.Length == 0)
                return ThreadLayoutDescriptions.Text("主热点已核验；辅助池未准入，原因见布局日志",
                    "Primary verified; auxiliary pool not admitted. See placement logs for the reason.",
                    "主候補を確認済み。補助プールは未適用です。理由は配置ログに記録します。");
            int activeMembers = 0;
            HardThreadOwnership[] receipts = criticalSession == null ? new HardThreadOwnership[0] : criticalSession.ActiveReceipts;
            foreach (HardThreadChoice member in members)
                if (receipts.Any(item => item.Thread.Key == member.Thread.Key && item.Mask == member.Mask
                    && criticalPlan != null && item.Group == criticalPlan.Group)) activeMembers++;
            if (activeMembers == 0)
                return ThreadLayoutDescriptions.Text("主热点已核验；原辅助成员已退出或未确认，本局不补新成员",
                    "Primary verified; original auxiliary members exited or are unconfirmed. No replacements this session.",
                    "主候補を確認済み。元の補助メンバーは終了または未確認です。このセッションでは補充しません。");
            return ThreadLayoutDescriptions.Text("主热点＋辅助池已核验；成员=" + activeMembers + "/" + members.Length
                    + "；池内范围=0x" + criticalAuxiliaryPlan.PoolMask.ToString("X") + "；辅助核非独享",
                "Primary and auxiliary placement verified; members=" + activeMembers + "/" + members.Length
                    + "; pool=0x" + criticalAuxiliaryPlan.PoolMask.ToString("X") + "; auxiliary cores are shared",
                "主候補と補助配置を確認済み。メンバー=" + activeMembers + "/" + members.Length
                    + "、範囲=0x" + criticalAuxiliaryPlan.PoolMask.ToString("X") + "。補助コアは共有です。");
        }

        private bool AuditElasticAuxiliaryCapacity(CriticalRuntimeSource source, bool initial)
        {
            HardThreadChoice[] members = ElasticWorkerPlacements(source);
            if (members.Length == 0 || criticalAuxiliaryObservation == null) return true;
            long tick = criticalAuxiliaryObservation.LatestTicks;
            if (initial || tick <= criticalAuxiliaryGuardTick) return true;
            criticalAuxiliaryGuardTick = tick;
            if (criticalSession != null)
            {
                HardThreadOwnership[] receipts = criticalSession.ActiveReceipts;
                members = members.Where(member => receipts.Any(item => item.Thread.Key == member.Thread.Key
                    && item.Mask == member.Mask && criticalPlan != null && item.Group == criticalPlan.Group)).ToArray();
            }
            if (members.Length == 0)
            { criticalAuxiliaryOverloadWindows = 0; return true; }
            double demand; int present;
            if (!criticalAuxiliaryObservation.LatestDemand(members, out demand, out present))
            {
                // 退出成员不补位；实际退出和恢复归属继续由原会话核验。
                criticalAuxiliaryOverloadWindows = 0;
                return true;
            }
            // 这是连续容量确认，不是失败预算；一次撤回后由既有同规则阻止重绑机制封停。
            criticalAuxiliaryOverloadWindows = demand > CriticalThreadAuxiliaryPolicy.MaximumPoolMean
                ? criticalAuxiliaryOverloadWindows + 1 : 0;
            if (criticalAuxiliaryOverloadWindows < 2) return true;
            Logger.Warn("线程调度 辅助池容量护栏 PID=" + source.Pid + " 创建=" + source.Birth + "；连续窗口=2；辅助成员当前单核需求合计="
                + (demand * 100).ToString("F2", CultureInfo.InvariantCulture)
                + "%；超过双核池预留余量；撤回本局布局并停止重新绑定");
            criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText, "auxiliary-capacity");
            if (WithdrawCriticalThreadsLocked("辅助池连续两个窗口超出CPU容量余量"))
                PublishCriticalThreadState(source, "conflict", ThreadLayoutDescriptions.Text(
                    "辅助池持续超出容量余量，已恢复原设置，本局不再绑定",
                    "Auxiliary demand exceeded the reserved headroom. Original settings restored; no rebinding this session.",
                    "補助負荷が余裕を継続超過したため復元しました。このセッションでは再配置しません。"), false, false, 0, 0);
            return false;
        }

    }
}
