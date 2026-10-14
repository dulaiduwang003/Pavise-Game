// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    internal sealed class AutoCorePlan
    {
        public ulong Exclusive;
        public int Cores;
        public int Logical;
        public ulong Region;
        public ulong Allocated;
        public string Kind = "";
        public string Blocked;
        public bool Cooperative;

        public bool Available { get { return Blocked == null && Exclusive != 0; } }
    }

    internal sealed class ThreadTimes
    {
        public long Ticks;
        public long Creation;
        public long ProcessCpu;
        public long[] Ids;
        public long[] Created;
        public long[] Cpu;
    }

    internal enum AutoCoreStep { None, Engage, Release }

    internal sealed class AutoCoreGovernor
    {
        internal const double ActiveShare = 0.20;
        internal const int WindowMs = 5000;
        internal const int MaxWindowMs = 60000;
        internal const int EngageWindows = 2;
        internal const double EngageSaturation = 0.85;
        internal const double ReleaseSaturation = 0.95;

        private int capacity, logical;
        private int fit;
        private long quietUntil;
        internal string Reason { get; private set; }
        internal long CooldownUntil { get { return quietUntil; } }

        internal AutoCoreGovernor(int exclusiveCores, int exclusiveLogical)
        {
            capacity = exclusiveCores;
            logical = exclusiveLogical;
        }

        internal int Capacity { get { return capacity; } }
        internal void Grow(int cores, int threads) { capacity = Math.Max(capacity, cores); logical = Math.Max(logical, threads); }
        internal int Logical { get { return logical; } }
        internal bool Exclusive { get; private set; }
        internal string Blocked { get; private set; }
        internal int Active { get; private set; }
        internal double Load { get; private set; }
        internal int Windows { get; private set; }
        internal int Releases { get { return 0; } }

        internal static bool Fits(int active, double load, int capacity, int logical)
        {
            // 线程数多不等于 CPU 压力大，准入以实际需求为准。
            return active >= 1 && load >= 0 && load <= Math.Min(capacity, logical * EngageSaturation);
        }

        internal static bool Exceeds(int active, double load, int capacity, int logical)
        {
            return active > capacity || load > capacity || load >= logical * ReleaseSaturation;
        }

        internal void Interrupt()
        {
            fit = 0;
        }

        internal void EngageManual()
        {
            if (Blocked != null || capacity <= 0 || logical < ManualCorePlan.MinLogical) return;
            Exclusive = true;
            Reason = null;
        }

        internal AutoCoreStep Pause(string reason, long nowTicks)
        {
            if (Blocked != null) return AutoCoreStep.None;
            bool wasExclusive = Exclusive;
            Exclusive = false;
            Interrupt();
            Reason = reason;
            quietUntil = 0;
            return wasExclusive ? AutoCoreStep.Release : AutoCoreStep.None;
        }

        internal AutoCoreStep CheckLease(long nowTicks)
        {
            // 核心保留属于对局，不随采样租约释放。
            return AutoCoreStep.None;
        }

        internal AutoCoreStep Observe(int active, double load, long nowTicks)
        {
            Active = active; Load = load; Windows++;
            if (Blocked != null || capacity <= 0) return AutoCoreStep.None;
            Reason = null;
            if (!Exclusive)
            {
                if (nowTicks < quietUntil) { fit = 0; return AutoCoreStep.None; }
                fit = Fits(active, load, capacity, logical) ? fit + 1 : 0;
                if (fit < EngageWindows || nowTicks < quietUntil) return AutoCoreStep.None;
                fit = 0;
                Exclusive = true;
                Reason = null;
                return AutoCoreStep.Engage;
            }
            return AutoCoreStep.None;
        }

        internal AutoCoreStep Block(string reason)
        {
            if (Blocked == null) Blocked = reason;
            Interrupt();
            quietUntil = 0;
            Reason = null;
            if (!Exclusive) return AutoCoreStep.None;
            Exclusive = false;
            return AutoCoreStep.Release;
        }
    }

    internal sealed class AutoCoreStatus
    {
        public AutoCorePlan Plan;
        public string Game;
        public string ProfileId;
        public bool Watching, Exclusive, Engaged, Pending;
        public string Blocked;
        public string Reason;
        public bool ObservationOnly;
        public bool Manual, ManualIsolation;
        public bool Cooperative, BackgroundOnly;
        public int BackgroundMoved, SampleCount, ReusedSamples, SampleIntervalMs;
        public double SampleAverageMs, SampleMaxMs;
        public int ThreadCandidates, BudgetDeferrals;
        public bool Learned;
        public string ThreadState, Feedback;
        public string PlacementEvidence;
        public double WorkMs, WorkMaxMs, CollectorCpuMs;
        public ulong CandidateMask, WorkerMask;
        public double BaselineP99, LatestP99;
        public ulong GameMask;
        public int Expansions;
        public int CooldownSeconds;
        public int Active, Capacity, Logical, Windows, Releases;
        public double Load;
    }

    internal static class AutoCores
    {
        internal static long NowTicks
        {
            get
            {
                long value = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency;
                return value / frequency * TimeSpan.TicksPerSecond
                    + value % frequency * TimeSpan.TicksPerSecond / frequency;
            }
        }
        internal const int MinExclusiveCores = 2;
        internal const int MinSpareCores = 2;
        internal const int MinGameLogical = 2;
        internal static readonly string[] RetiredSettings = { "GmManualCorePlanV1", "GmCoreMask", "GmStrictCores",
            "GmCoreDomainAlt", "GmHeavySqueezeV2", "GmHeavyCoreMaskV1", "GmHardAffinityV1", "GmAffinityGuardV1" };

        private static volatile AutoCoreStatus status = new AutoCoreStatus();

        internal static AutoCoreStatus Status { get { return status; } }

        internal static void Publish(AutoCoreStatus next) { status = next ?? new AutoCoreStatus(); }

#if PAVISE_SELFTEST
        internal static bool? SupportedForTest;
        internal static Func<ulong> AllocatedForTest;
#endif

        internal static bool Supported
        {
            get
            {
#if PAVISE_SELFTEST
                if (SupportedForTest.HasValue) return SupportedForTest.Value;
#endif
                return IntPtr.Size == 8 && Native.OsBuild() >= 19041 && !CpuTopology.MultiGroup;
            }
        }

        internal static ulong WholeCores(ulong mask, ulong[] cores)
        {
            ulong result = 0;
            if (cores == null) return 0;
            foreach (ulong core in cores) if ((core & mask) != 0) result |= core;
            return result;
        }

        internal static int CountBits(ulong mask)
        {
            int n = 0;
            while (mask != 0) { mask &= mask - 1; n++; }
            return n;
        }

        internal static ulong Expand(AutoCorePlan plan, ulong current, ulong all, ulong[] cores,
            ulong favored, int wanted)
        {
            if (plan == null || !plan.Available || cores == null) return current;
            ulong result = current | plan.Exclusive;
            int count = PhysicalCount(result, cores);
            for (int rank = 0; rank < 4 && count < wanted; rank++)
                foreach (ulong core in cores)
                {
                    if (count >= wanted) break;
                    if (core == 0 || (core & ~all) != 0 || (core & (result | plan.Allocated | 1UL)) != 0) continue;
                    int candidateRank = ((core & ~plan.Region) == 0 ? 0 : 2) + ((core & favored) != 0 ? 0 : 1);
                    if (candidateRank != rank) continue;
                    result |= core; count++;
                }
            return result;
        }

        internal static int PhysicalCount(ulong mask, ulong[] cores)
        {
            int count = 0;
            if (cores != null) foreach (ulong core in cores) if (core != 0 && (mask & core) == core) count++;
            return count;
        }

        internal static AutoCorePlan Plan(ulong all, ulong[] physical, bool hybrid, ulong perf,
            bool asymCache, ulong bigL3, ulong[] l3Domains, bool supported, bool handheld, ulong allocated, ulong favored = 0)
        {
            var plan = new AutoCorePlan { Allocated = allocated & all };
            if (handheld) return Block(plan, "schedule.auto.handheld");
            if (all == 0 || physical == null || physical.Length == 0) return Block(plan, "schedule.auto.topology");
            ulong covered = 0;
            foreach (ulong core in physical)
            {
                if (core == 0 || (core & ~all) != 0 || (core & covered) != 0)
                    return Block(plan, "schedule.auto.topology");
                covered |= core;
            }
            if (covered != all) return Block(plan, "schedule.auto.topology");
            plan.Cooperative = !supported;
            ulong region = 0;
            if (hybrid && (perf & all) != 0) { region = perf & all; plan.Kind = "hybrid"; }
            else if (asymCache && (bigL3 & all) != 0) { region = bigL3 & all; plan.Kind = "cache"; }
            else if (l3Domains != null && l3Domains.Length >= 2)
            {
                int bestScore = -1;
                foreach (ulong domain in l3Domains)
                {
                    int score = 0;
                    foreach (ulong c in physical)
                        if ((c & domain & all) == c && (c & (allocated | 1UL)) == 0)
                            score += 4 + ((c & favored) != 0 ? 3 : 0);
                    if (score > bestScore) { bestScore = score; region = domain & all; }
                }
                if (region == 0) region = l3Domains[0] & all;
                plan.Kind = "ccd";
            }
            if (region == 0) { region = all; plan.Kind = "uniform"; }
            plan.Region = region;
            var chosen = new List<ulong>();
            int usable = 0;
            foreach (ulong raw in physical)
            {
                ulong core = raw & all;
                if (core == 0) continue;
                if ((core & ~plan.Allocated) != 0) usable++;
                if ((core & 1UL) != 0 || (core & ~region) != 0 || (core & plan.Allocated) != 0) continue;
                chosen.Add(core);
            }
            chosen.Sort(delegate(ulong a, ulong b)
            {
                int aRank = (a & favored) != 0 ? 0 : 1, bRank = (b & favored) != 0 ? 0 : 1;
                return aRank != bRank ? aRank.CompareTo(bRank) : a.CompareTo(b);
            });
            while (chosen.Count > 0 && usable - chosen.Count < MinSpareCores) chosen.RemoveAt(chosen.Count - 1);
            if (chosen.Count < MinExclusiveCores) return Block(plan, "schedule.auto.fewcores");
            foreach (ulong core in chosen) plan.Exclusive |= core;
            plan.Cores = chosen.Count;
            plan.Logical = CountBits(plan.Exclusive);
            return plan;
        }

        private static AutoCorePlan Block(AutoCorePlan plan, string reason)
        {
            plan.Blocked = reason;
            plan.Exclusive = 0;
            plan.Cores = 0;
            plan.Logical = 0;
            return plan;
        }

        internal static ulong LiveAllocated()
        {
#if PAVISE_SELFTEST
            if (AllocatedForTest != null) return AllocatedForTest();
#endif
            try
            {
                IsolationCpuState state = new CoreIsolationNative().ReadSystem(IntPtr.Zero);
                return state == null ? 0 : state.Allocated;
            }
            catch { return 0; }
        }

        internal static AutoCorePlan Current(bool handheld)
        {
            return Current(handheld, true);
        }

        internal static AutoCorePlan Current(bool handheld, bool liveAllocation)
        {
            bool supported = Supported;
            if (IntPtr.Size != 8 || CpuTopology.MultiGroup)
                return Block(new AutoCorePlan(), "schedule.auto.unsupported");
            return Plan(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks(), CpuTopology.Hybrid,
                CpuTopology.PerfMask, CpuTopology.AsymCache, CpuTopology.BigL3Mask, CpuTopology.L3Masks(),
                supported, handheld, supported && liveAllocation ? LiveAllocated() : 0, CpuTopology.FavoredMask);
        }

        internal static string DiagnosticLine()
        {
            AutoCoreStatus s = Status;
            AutoCorePlan plan = s.Plan ?? Current(false);
            string text = plan.Available
                ? plan.Kind + " 0x" + plan.Exclusive.ToString("X") + " (" + plan.Cores + " cores)"
                : "unavailable " + plan.Blocked;
            if (plan.Allocated != 0) text += " · allocated 0x" + plan.Allocated.ToString("X");
            string live = s.Pending ? "recovery pending" : s.ObservationOnly ? "disabled" : !s.Watching ? "no match"
                : s.Engaged ? s.BackgroundOnly ? "background-only" : s.Cooperative ? "cooperative" : "exclusive" : s.Exclusive ? "exclusive requested"
                : s.Blocked != null ? "shared (" + s.Blocked + ")" : "shared";
            return text + " · " + live + " · active " + s.Active + " · load "
                + s.Load.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " · windows " + s.Windows + " · game 0x" + s.GameMask.ToString("X") + " · expansions " + s.Expansions
                + " · background " + s.BackgroundMoved + " · sampling " + s.SampleIntervalMs + "ms / " + s.SampleCount
                + " · reused " + s.ReusedSamples + " · average " + s.SampleAverageMs.ToString("0.000") + "ms"
                + " · thread routes " + s.ThreadCandidates + " · frame guard " + s.Feedback + " · learned " + s.Learned
                + " · work wall " + s.WorkMs.ToString("F1") + "ms · capture CPU " + s.CollectorCpuMs.ToString("F1")
                + "ms · budget deferrals " + s.BudgetDeferrals
                + (string.IsNullOrEmpty(s.PlacementEvidence) ? "" : " · placement {" + s.PlacementEvidence + "}");
        }

        internal static string ValidateRange(ulong isolation, ulong game, ulong all, ulong[] cores, ulong allocated, bool manual = false)
        {
            if (all == 0 || cores == null || cores.Length == 0) return "topology";
            ulong covered = 0;
            foreach (ulong core in cores)
            {
                if (core == 0 || (core & ~all) != 0 || (covered & core) != 0) return "topology";
                covered |= core;
            }
            if (covered != all) return "topology";
            if (game == 0 || (game & ~all) != 0
                || CountBits(game) < (manual ? ManualCorePlan.MinLogical : MinGameLogical)) return "game";
            if (isolation == 0 || (isolation & ~all) != 0 || (!manual && (isolation & 1UL) != 0)
                || WholeCores(isolation, cores) != isolation) return "whole";
            int spare = 0;
            foreach (ulong core in cores)
                if ((core & isolation) == 0 && (core & ~allocated) != 0) spare++;
            return spare < MinSpareCores ? "spare" : null;
        }

        internal static bool Measure(ThreadTimes before, ThreadTimes after, out int active, out double load)
        {
            active = 0;
            load = 0;
            if (before == null || after == null || after.Ids == null || after.Cpu == null
                || before.Creation != after.Creation) return false;
            long elapsed = after.Ticks - before.Ticks;
            if (elapsed <= 0) return false;
            var previous = new Dictionary<ThreadIdentity, long>(before.Ids != null ? before.Ids.Length : 0);
            if (before.Ids != null && before.Cpu != null)
                for (int i = 0; i < before.Ids.Length && i < before.Cpu.Length; i++)
                    previous[ThreadKey(before, i)] = before.Cpu[i];
            long counted = 0;
            for (int i = 0; i < after.Ids.Length && i < after.Cpu.Length; i++)
            {
                long old;
                long delta = previous.TryGetValue(ThreadKey(after, i), out old) ? after.Cpu[i] - old : after.Cpu[i];
                if (delta <= 0) continue;
                counted += delta;
                double share = Math.Min(1.0, (double)delta / elapsed);
                load += share;
                if (share >= AutoCoreGovernor.ActiveShare) active++;
            }
            long total = after.ProcessCpu - before.ProcessCpu;
            if (total < 0) return false;
            if (total == 0) return counted == 0 || before.ProcessCpu == 0;
            load = Math.Max(load, (double)total / elapsed);
            return total - counted <= elapsed / 2;
        }

        private struct ThreadIdentity : IEquatable<ThreadIdentity>
        {
            internal long Id, Created;
            public bool Equals(ThreadIdentity other) { return Id == other.Id && Created == other.Created; }
            public override bool Equals(object other) { return other is ThreadIdentity && Equals((ThreadIdentity)other); }
            public override int GetHashCode() { return Id.GetHashCode() ^ Created.GetHashCode(); }
        }

        private static ThreadIdentity ThreadKey(ThreadTimes times, int index)
        {
            long created = times.Created != null && index < times.Created.Length ? times.Created[index] : 0;
            return new ThreadIdentity { Id = times.Ids[index], Created = created };
        }

        internal static bool WindowUsable(ThreadTimes before, ThreadTimes after)
        {
            if (before == null || after == null || before.Creation != after.Creation) return false;
            long elapsed = after.Ticks - before.Ticks;
            return elapsed >= AutoCoreGovernor.WindowMs * TimeSpan.TicksPerMillisecond * 4 / 5
                && elapsed <= AutoCoreGovernor.MaxWindowMs * TimeSpan.TicksPerMillisecond;
        }

        internal static string KindText(string kind)
        {
            if (kind == "manual") return Lang.T("schedule.manual.mode");
            return Lang.T(kind == "hybrid" ? "schedule.auto.kind.hybrid"
                : kind == "cache" ? "schedule.auto.kind.cache" : kind == "uniform" ? "schedule.auto.kind.uniform" : "schedule.auto.kind.ccd");
        }
    }
}
