using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum CpuCoreClassificationState { Unknown, Uniform, Hybrid, Conflict }

    internal sealed class CpuCoreClassificationResult
    {
        internal CpuCoreClassificationState State;
        internal ulong PerformanceMask, EfficiencyMask, UnknownMask;
        internal string Source, Reason;
    }

    // 映射使用组内逻辑处理器编号，共 64 项；-1 表示不可用，0 是有效类别。核对厂商及 CPUID 叶支持后，在指定处理器上采样。

    internal static class CpuCoreClassification
    {
        private sealed class Evidence
        {
            internal bool Present, Complete, Invalid, SiblingConflict;
            internal ulong Performance, Efficiency;
            internal int[] Values;
            internal string Name;
            internal bool Split { get { return Complete && Performance != 0 && Efficiency != 0; } }
        }

        internal static CpuCoreClassificationResult Resolve(ulong allMask, ulong[] physicalCores,
            int[] topologyClasses, int[] cpuSetClasses, int[] cpuidTypes, bool topologySourcesAgree)
        {
            if (!topologySourcesAgree) return Unknown(allMask, "physical-topology-disagrees", false);
            if (!ValidPhysicalCores(allMask, physicalCores)) return Unknown(allMask, "physical-topology-invalid", false);
            Evidence topology = Read(allMask, physicalCores, topologyClasses, false, "windows-topology");
            Evidence sets = Read(allMask, physicalCores, cpuSetClasses, false, "cpu-sets");
            Evidence cpuid = Read(allMask, physicalCores, cpuidTypes, true, "cpuid");
            foreach (Evidence evidence in new[] { topology, sets, cpuid })
            {
                if (evidence.SiblingConflict) return Unknown(allMask, "sibling-core-class-conflict", true);
                if (evidence.Invalid) return Unknown(allMask, "core-class-data-invalid", false);
            }
            // Windows 映射不完整时，不能据此确认整个物理分区。
            if ((topology.Present && !topology.Complete) || (sets.Present && !sets.Complete))
                return Unknown(allMask, "windows-core-class-incomplete", false);

            Evidence selected = topology.Split ? topology : (sets.Split ? sets : (cpuid.Split ? cpuid : null));
            if (topology.Split && sets.Split && !SameSplit(topology, sets))
                return Unknown(allMask, "windows-core-class-conflict", true);
            if (selected != null)
            {
                // 不完整的 CPUID 采样不能定论，但采到相反类型仍属于冲突证据。
                if (!CpuidAgrees(allMask, selected, cpuid))
                    return Unknown(allMask, "cpuid-core-type-conflict", true);
                List<string> sources = new List<string>();
                foreach (Evidence currentEvidence in new[] { topology, sets, cpuid })
                    if (currentEvidence.Split && SameSplit(selected, currentEvidence)) sources.Add(currentEvidence.Name);
                return new CpuCoreClassificationResult {
                    State = CpuCoreClassificationState.Hybrid,
                    PerformanceMask = selected.Performance, EfficiencyMask = selected.Efficiency,
                    Source = string.Join("+", sources.ToArray()), Reason = "confirmed"
                };
            }
            // Windows 报告单一能效类别，不能证明硬件没有 E 核。完整且受支持的 CPUID 映射可确认已报告的微架构类型。

            if (cpuid.Complete)
                return new CpuCoreClassificationResult { State = CpuCoreClassificationState.Uniform,
                    Source = "cpuid", Reason = "single-cpuid-core-type" };
            return Unknown(allMask, cpuid.Present ? "cpuid-incomplete" :
                ((topology.Complete || sets.Complete) ? "windows-classes-uniform" : "core-class-unavailable"), false);
        }

        internal static bool SamePhysicalCores(ulong[] first, ulong[] second)
        {
            if (first == null || second == null || first.Length != second.Length || first.Length == 0) return false;
            ulong firstUnion = 0, secondUnion = 0;
            foreach (ulong mask in first) firstUnion |= mask;
            foreach (ulong mask in second) secondUnion |= mask;
            if (firstUnion != secondUnion || !ValidPhysicalCores(firstUnion, first) || !ValidPhysicalCores(secondUnion, second)) return false;
            HashSet<ulong> remaining = new HashSet<ulong>(first);
            foreach (ulong mask in second) if (!remaining.Remove(mask)) return false;
            return remaining.Count == 0;
        }

        private static bool ValidPhysicalCores(ulong allMask, ulong[] cores)
        {
            if (allMask == 0 || cores == null || cores.Length == 0) return false;
            ulong seen = 0;
            foreach (ulong core in cores)
            {
                if (core == 0 || (core & ~allMask) != 0 || (seen & core) != 0) return false;
                seen |= core;
            }
            return seen == allMask;
        }

        private static Evidence Read(ulong allMask, ulong[] cores, int[] values, bool cpuid, string name)
        {
            Evidence result = new Evidence { Values = values, Name = name };
            if (values == null) return result;
            if (values.Length != 64) { result.Invalid = true; return result; }
            int missing = 0, minimum = int.MaxValue, maximum = int.MinValue;
            for (int cpu = 0; cpu < 64; cpu++)
            {
                if ((allMask & (1UL << cpu)) == 0) continue;
                int coreTypeId = values[cpu];
                if (coreTypeId == -1) { missing++; continue; }
                result.Present = true;
                if (coreTypeId < 0 || coreTypeId > 255 || (cpuid && coreTypeId != 0x20 && coreTypeId != 0x40))
                { result.Invalid = true; return result; }
                minimum = Math.Min(minimum, coreTypeId); maximum = Math.Max(maximum, coreTypeId);
            }
            foreach (ulong core in cores)
            {
                int first = -1;
                for (int cpu = 0; cpu < 64; cpu++)
                {
                    if ((core & (1UL << cpu)) == 0 || values[cpu] == -1) continue;
                    if (first != -1 && first != values[cpu]) { result.SiblingConflict = true; return result; }
                    first = values[cpu];
                }
            }
            result.Complete = result.Present && missing == 0;
            if (!result.Complete || minimum == maximum) return result;
            for (int cpu = 0; cpu < 64; cpu++)
            {
                ulong bit = 1UL << cpu;
                if ((allMask & bit) == 0) continue;
                if (cpuid ? values[cpu] == 0x40 : values[cpu] == maximum) result.Performance |= bit;
                else result.Efficiency |= bit;
            }
            return result;
        }

        private static bool SameSplit(Evidence a, Evidence b)
        { return a.Performance == b.Performance && a.Efficiency == b.Efficiency; }

        private static bool CpuidAgrees(ulong allMask, Evidence split, Evidence cpuid)
        {
            if (!cpuid.Present) return true;
            for (int cpu = 0; cpu < 64; cpu++)
            {
                ulong bit = 1UL << cpu;
                if ((allMask & bit) == 0 || cpuid.Values[cpu] == -1) continue;
                if (((split.Performance & bit) != 0) != (cpuid.Values[cpu] == 0x40)) return false;
            }
            return true;
        }

        private static CpuCoreClassificationResult Unknown(ulong allMask, string reason, bool conflict)
        {
            return new CpuCoreClassificationResult { State = conflict ? CpuCoreClassificationState.Conflict : CpuCoreClassificationState.Unknown,
                UnknownMask = allMask, Source = "", Reason = reason };
        }
    }
}
