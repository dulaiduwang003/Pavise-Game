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

    // Maps use group-relative logical CPU indexes (64 entries); -1 is unavailable and 0 is a valid class.
    // Validate vendor/leaf support and sample CPUID on the intended processor.
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
            foreach (Evidence item in new[] { topology, sets, cpuid })
            {
                if (item.SiblingConflict) return Unknown(allMask, "sibling-core-class-conflict", true);
                if (item.Invalid) return Unknown(allMask, "core-class-data-invalid", false);
            }
            // Partial Windows mappings cannot certify the complete physical partition.
            if ((topology.Present && !topology.Complete) || (sets.Present && !sets.Complete))
                return Unknown(allMask, "windows-core-class-incomplete", false);

            Evidence selected = topology.Split ? topology : (sets.Split ? sets : (cpuid.Split ? cpuid : null));
            if (topology.Split && sets.Split && !SameSplit(topology, sets))
                return Unknown(allMask, "windows-core-class-conflict", true);
            if (selected != null)
            {
                // Partial CPUID is not authoritative, but a sampled opposite type is a conflict.
                if (!CpuidAgrees(allMask, selected, cpuid))
                    return Unknown(allMask, "cpuid-core-type-conflict", true);
                List<string> sources = new List<string>();
                foreach (Evidence item in new[] { topology, sets, cpuid })
                    if (item.Split && SameSplit(selected, item)) sources.Add(item.Name);
                return new CpuCoreClassificationResult {
                    State = CpuCoreClassificationState.Hybrid,
                    PerformanceMask = selected.Performance, EfficiencyMask = selected.Efficiency,
                    Source = string.Join("+", sources.ToArray()), Reason = "confirmed"
                };
            }
            // A flat Windows efficiency class is not evidence that hardware has no E-cores.
            // A complete supported CPUID map can identify a single reported microarchitecture type.
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
                int value = values[cpu];
                if (value == -1) { missing++; continue; }
                result.Present = true;
                if (value < 0 || value > 255 || (cpuid && value != 0x20 && value != 0x40))
                { result.Invalid = true; return result; }
                minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
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
