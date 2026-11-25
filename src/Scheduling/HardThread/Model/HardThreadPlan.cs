using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class HardThreadPlan
    {
        private readonly HardThreadChoice[] choices;

        private readonly HardThreadChoice[] workerPlacements;

        internal const int MaximumWorkerPlacements = 3;

        internal readonly HardThreadTarget Target;

        internal readonly ulong Workers;

        internal readonly ulong ProcessMask;

        internal readonly ushort Group;

        internal readonly HardThreadWorkerMode WorkerMode;

        internal readonly bool RequireWorkerCoverage;

        internal readonly bool PreserveOriginalMasks;
        internal readonly bool SharedCandidate;
        internal HardThreadChoice[] Choices
        {
            get
            {
                return (HardThreadChoice[])choices.Clone();
            }
        }

        internal HardThreadChoice[] WorkerPlacements
        {
            get { return (HardThreadChoice[])workerPlacements.Clone(); }
        }

        private HardThreadPlan(HardThreadTarget target, HardThreadChoice[] choices, HardThreadChoice[] workerPlacements, ulong workers, ulong processMask, ushort group, HardThreadWorkerMode workerMode, bool requireWorkerCoverage, bool preserveOriginalMasks, bool sharedCandidate)
        {
            Target = target;
            this.choices = (HardThreadChoice[])choices.Clone();
            this.workerPlacements = (HardThreadChoice[])workerPlacements.Clone();
            Workers = workers;
            ProcessMask = processMask;
            Group = group;
            WorkerMode = workerMode;
            RequireWorkerCoverage = requireWorkerCoverage;
            PreserveOriginalMasks = preserveOriginalMasks;
            SharedCandidate = sharedCandidate;
        }

        internal static HardThreadPlan Create(HardThreadTarget target, HardThreadMachine machine, HardThreadChoice[] selected, HardThreadWorkerMode workerMode = HardThreadWorkerMode.CandidatesOnly, bool requireWorkerCoverage = false, bool preserveOriginalMasks = false, HardThreadChoice[] workerPlacements = null, bool sharedCandidate = false)
        {
            if (target != null && target.AllowedMask == 0)
                throw new InvalidOperationException(Logger.Text("此目标仅授予优先级控制，不允许创建亲和性计划", "this target is granted priority control only; affinity plans are not allowed"));
            if (target == null || machine == null || machine.Read != 0 || !machine.CpuSetsEmpty)
            {
                throw new InvalidOperationException("known original process and empty CPU Sets required");
            }
            if (machine.GroupCount != 1 || machine.Group != 0)
            {
                throw new NotSupportedException("multiple processor groups are unsupported");
            }
            if (machine.ProcessMask == 0 || (target.AllowedMask & ~machine.ProcessMask) != 0)
            {
                throw new InvalidOperationException("original process mask does not contain the allowed range");
            }
            if (selected == null || selected.Length < 1 || selected.Length > CriticalThreadRule.MaximumIsolated)
            {
                throw new ArgumentException("one to " + CriticalThreadRule.MaximumIsolated + " candidates required");
            }
            ulong numericValue = 0uL;
            ulong numericValue2 = 0uL;
            HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
            ulong[] physicalCores = machine.PhysicalCores;
            foreach (ulong numericValueEntry in physicalCores)
            {
                if (numericValueEntry == 0 || (numericValue2 & numericValueEntry) != 0)
                {
                    throw new InvalidOperationException("invalid physical topology");
                }
                numericValue2 |= numericValueEntry;
            }
            if ((target.AllowedMask & ~numericValue2) != 0)
            {
                throw new InvalidOperationException("allowed range has unknown physical processors");
            }
            foreach (HardThreadChoice hardThreadChoice in selected)
            {
                if (hardThreadChoice == null || hardThreadChoice.Thread == null || !hashSet.Add(hardThreadChoice.Thread.Key) || hardThreadChoice.Mask == 0 || (hardThreadChoice.Mask & ~target.AllowedMask) != 0)
                {
                    throw new InvalidOperationException("invalid candidate identity or logical processor");
                }
                // 手动选了多个逻辑 CPU 时一个候选会有多位 它碰到的每颗物理核都归它 清核时这些核整颗让出 和别的候选不能共用
                ulong numericValue3 = 0uL;
                ulong[] physicalCores2 = machine.PhysicalCores;
                foreach (ulong numericValueEntry2 in physicalCores2)
                {
                    if ((numericValueEntry2 & hardThreadChoice.Mask) != 0)
                    {
                        numericValue3 |= numericValueEntry2;
                    }
                }
                if (numericValue3 == 0 || (numericValue & numericValue3) != 0)
                {
                    throw new InvalidOperationException("candidates must use distinct physical cores");
                }
                numericValue |= numericValue3;
            }
            ulong remainingAllowedMask = target.AllowedMask & ~numericValue;
            int processedCount = 0;
            ulong[] physicalCores3 = machine.PhysicalCores;
            foreach (ulong numericValueEntry3 in physicalCores3)
            {
                if ((remainingAllowedMask & numericValueEntry3) != 0)
                {
                    processedCount++;
                }
            }
        // 容量不足而退化为共享主核时 仅接管提交的候选 允许沿用原清核授权
            if (sharedCandidate && (requireWorkerCoverage
                || selected.Length != 1 || workerPlacements != null && workerPlacements.Length != 0))
                throw new InvalidOperationException(Logger.Text("共享主核计划仅允许一个目标，不包含整核避让", "a shared primary-core plan allows only one target and excludes whole-core avoidance"));
            // 共享主核计划只固定这一个线程 不预留整颗核 其他游戏线程仍可使用原范围
            if (!sharedCandidate && processedCount < 2)
            {
                throw new InvalidOperationException("logical processors on at least two other physical cores required");
            }
            if (!Enum.IsDefined(typeof(HardThreadWorkerMode), workerMode))
            {
                throw new ArgumentException("worker mode");
            }
            if (requireWorkerCoverage && workerMode != HardThreadWorkerMode.AvoidWorkers)
            {
                throw new ArgumentException("complete worker coverage requires worker avoidance");
            }
            HardThreadChoice[] auxiliary = workerPlacements ?? new HardThreadChoice[0];
            if (auxiliary.Length > MaximumWorkerPlacements || (auxiliary.Length != 0
                && (workerMode != HardThreadWorkerMode.AvoidWorkers || !requireWorkerCoverage || !preserveOriginalMasks)))
                throw new ArgumentException(Logger.Text("辅助工作线程范围需要完整覆盖、原范围保护，且最多三个成员", "auxiliary worker ranges require full coverage, original-range protection, and at most three members"));
            foreach (HardThreadChoice placement in auxiliary)
            {
                if (placement == null || placement.Thread == null || !hashSet.Add(placement.Thread.Key)
                    || placement.Mask == 0 || (placement.Mask & ~remainingAllowedMask) != 0)
                    throw new ArgumentException(Logger.Text("辅助成员身份重复、与主候选重合，或范围越过工作线程允许核心", "auxiliary member identity is duplicated, overlaps the primary candidate, or its range exceeds the allowed worker cores"));
            }
            // 辅助范围不独占物理核 普通工作线程仍可使用原先的剩余核心范围
            return new HardThreadPlan(target, selected, auxiliary, remainingAllowedMask, machine.ProcessMask, machine.Group, workerMode, requireWorkerCoverage, preserveOriginalMasks, sharedCandidate);
        }

        internal bool IsCandidate(HardThreadIdentity thread)
        {
            HardThreadChoice[] choicesSnapshot = choices;
            foreach (HardThreadChoice hardThreadChoice in choicesSnapshot)
            {
                if (hardThreadChoice.Thread.Tid == thread.Tid && hardThreadChoice.Thread.Creation == thread.Creation)
                {
                    return true;
                }
            }
            return false;
        }

        internal bool IsWorkerPlacement(HardThreadIdentity thread)
        {
            foreach (HardThreadChoice placement in workerPlacements)
                if (placement.Thread.Tid == thread.Tid && placement.Thread.Creation == thread.Creation) return true;
            return false;
        }

        internal ulong MaskFor(HardThreadIdentity thread)
        {
            HardThreadChoice[] choicesSnapshot = choices;
            foreach (HardThreadChoice hardThreadChoice in choicesSnapshot)
            {
                if (hardThreadChoice.Thread.Tid == thread.Tid && hardThreadChoice.Thread.Creation == thread.Creation)
                {
                    return hardThreadChoice.Mask;
                }
            }
            if (WorkerMode != HardThreadWorkerMode.AvoidWorkers)
            {
                throw new InvalidOperationException("candidate-only plan cannot bind workers");
            }
            foreach (HardThreadChoice placement in workerPlacements)
                if (placement.Thread.Tid == thread.Tid && placement.Thread.Creation == thread.Creation) return placement.Mask;
            return Workers;
        }

        internal ulong MaskFor(HardThreadIdentity thread, ulong original)
        {
            ulong desired = MaskFor(thread);
            if (!PreserveOriginalMasks) return desired;
            if (original == 0 || (original & ~ProcessMask) != 0) return 0;
            // 放置范围不得超出线程原范围
            // 辅助池需要完整保留两个可选处理器 原范围缺少成员时撤回 不能退化成单核排队
            return IsCandidate(thread) || IsWorkerPlacement(thread)
                ? ((desired & original) == desired ? desired : 0) : original & desired;
        }
    }
}
