using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class HardThreadPlan
    {
        private readonly HardThreadChoice[] choices;

        private readonly HardThreadChoice[] workerPlacements;

        internal const int MaximumWorkerPlacements = 3;
        internal const int MaximumSelectedWorkerPlacements = 32;

        internal readonly HardThreadTarget Target;

        internal readonly ulong Workers;

        internal readonly ulong ProcessMask;

        internal readonly ushort Group;

        internal readonly HardThreadWorkerMode WorkerMode;

        internal readonly bool RequireWorkerCoverage;

        internal readonly bool PreserveOriginalMasks;
        internal readonly bool SharedCandidate;
        internal readonly bool SelectedWorkersOnly;
        // 浮动候选：候选只用来确定要清出的物理核，自身保持原范围不写；用于“渲染线程提优”打开清核。
        internal readonly bool FloatingCandidates;

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

        private HardThreadPlan(HardThreadTarget target, HardThreadChoice[] choices, HardThreadChoice[] workerPlacements, ulong workers, ulong processMask, ushort group, HardThreadWorkerMode workerMode, bool requireWorkerCoverage, bool preserveOriginalMasks, bool sharedCandidate, bool selectedWorkersOnly, bool floatingCandidates)
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
            SelectedWorkersOnly = selectedWorkersOnly;
            FloatingCandidates = floatingCandidates;
        }

        internal static HardThreadPlan Create(HardThreadTarget target, HardThreadMachine machine, HardThreadChoice[] selected, HardThreadWorkerMode workerMode = HardThreadWorkerMode.CandidatesOnly, bool requireWorkerCoverage = false, bool preserveOriginalMasks = false, HardThreadChoice[] workerPlacements = null, bool sharedCandidate = false, bool selectedWorkersOnly = false, bool floatingCandidates = false)
        {
            if (target != null && target.AllowedMask == 0)
                throw new InvalidOperationException("此目标仅授予优先级控制，不允许创建亲和性计划");
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
            if (selected == null || selected.Length < 1 || selected.Length > CriticalThreadAutoPolicy.MaximumIsolated)
            {
                throw new ArgumentException("one to " + CriticalThreadAutoPolicy.MaximumIsolated + " candidates required");
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
                if (hardThreadChoice == null || hardThreadChoice.Thread == null || !hashSet.Add(hardThreadChoice.Thread.Key) || hardThreadChoice.Mask == 0 || (hardThreadChoice.Mask & (hardThreadChoice.Mask - 1)) != 0 || (hardThreadChoice.Mask & ~target.AllowedMask) != 0)
                {
                    throw new InvalidOperationException("invalid candidate identity or logical processor");
                }
                ulong numericValue3 = 0uL;
                ulong[] physicalCores2 = machine.PhysicalCores;
                foreach (ulong numericValueEntry2 in physicalCores2)
                {
                    if ((numericValueEntry2 & hardThreadChoice.Mask) != 0)
                    {
                        numericValue3 = numericValueEntry2;
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
        // 容量不足而退化为共享主核时，仅接管提交的候选，允许沿用原清核授权。
            if (sharedCandidate && (requireWorkerCoverage || selectedWorkersOnly
                || selected.Length != 1 || workerPlacements != null && workerPlacements.Length != 0))
                throw new InvalidOperationException("共享主核计划仅允许一个目标，不包含整核避让");
            // 共享主核计划只固定一个逻辑 CPU，不预留整颗核，其他游戏线程仍可使用原范围。
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
            if (floatingCandidates && (!requireWorkerCoverage || !preserveOriginalMasks || sharedCandidate || selectedWorkersOnly))
                throw new ArgumentException("浮动候选只用于清核：需要整核避让和原范围保护");
            HardThreadChoice[] auxiliary = workerPlacements ?? new HardThreadChoice[0];
            if (selectedWorkersOnly && (workerMode != HardThreadWorkerMode.AvoidWorkers || requireWorkerCoverage || !preserveOriginalMasks))
                throw new ArgumentException("显式工作组计划只接管列明成员，必须保护原范围，不能声明全线程避让");
            int auxiliaryLimit = selectedWorkersOnly ? MaximumSelectedWorkerPlacements : MaximumWorkerPlacements;
            if (auxiliary.Length > auxiliaryLimit || (!selectedWorkersOnly && auxiliary.Length != 0
                && (workerMode != HardThreadWorkerMode.AvoidWorkers || !requireWorkerCoverage || !preserveOriginalMasks)))
                throw new ArgumentException(selectedWorkersOnly ? "显式工作组范围必须保护原值，且最多32个成员"
                    : "辅助工作线程范围需要完整覆盖、原范围保护，且最多三个成员");
            foreach (HardThreadChoice placement in auxiliary)
            {
                if (placement == null || placement.Thread == null || !hashSet.Add(placement.Thread.Key)
                    || placement.Mask == 0 || (placement.Mask & ~remainingAllowedMask) != 0)
                    throw new ArgumentException("辅助成员身份重复、与主候选重合，或范围越过工作线程允许核心");
            }
            // 辅助范围不独占物理核，普通工作线程仍可使用原先的剩余核心范围。
            return new HardThreadPlan(target, selected, auxiliary, remainingAllowedMask, machine.ProcessMask, machine.Group, workerMode, requireWorkerCoverage, preserveOriginalMasks, sharedCandidate, selectedWorkersOnly, floatingCandidates);
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
                    return FloatingCandidates ? ProcessMask : hardThreadChoice.Mask;
                }
            }
            if (WorkerMode != HardThreadWorkerMode.AvoidWorkers)
            {
                throw new InvalidOperationException("candidate-only plan cannot bind workers");
            }
            foreach (HardThreadChoice placement in workerPlacements)
                if (placement.Thread.Tid == thread.Tid && placement.Thread.Creation == thread.Creation) return placement.Mask;
            if (SelectedWorkersOnly) throw new InvalidOperationException("显式工作组计划不能接管未列明的线程身份");
            return Workers;
        }

        internal ulong MaskFor(HardThreadIdentity thread, ulong original)
        {
            ulong desired = MaskFor(thread);
            if (!PreserveOriginalMasks) return desired;
            if (original == 0 || (original & ~ProcessMask) != 0) return 0;
            // 浮动候选按原值记录、不写入；游戏原范围窄于进程范围时也照原样保留。
            if (FloatingCandidates && IsCandidate(thread)) return original;
            // 并行工作组保留各线程原有窄范围；未知成员不能借用共同工作池取得写入资格。
            if (SelectedWorkersOnly && IsWorkerPlacement(thread)) return original & desired;
            // 放置范围不得超出线程原范围。
            // 辅助池需要完整保留两个可选处理器；原范围缺少成员时撤回，不能退化成单核排队。
            return IsCandidate(thread) || IsWorkerPlacement(thread)
                ? ((desired & original) == desired ? desired : 0) : original & desired;
        }
    }
}
