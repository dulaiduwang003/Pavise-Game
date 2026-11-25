using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        internal static IAntiCheatJobPlatform AntiCheatJobs;
        private readonly System.Collections.Generic.Dictionary<int, long> antiCheatJobRejected = new System.Collections.Generic.Dictionary<int, long>();

        private static bool AntiCheatJobNeedsRelease(AntiCheatResourceState state)
        { return state != null && (state.JobPhase == 'P' || state.JobPhase == 'A'); }

        internal bool AntiCheatJobLocked(int pid, long birth)
        {
            lock (sync)
            {
                Entry entry;
                return map.TryGetValue(pid, out entry) && entry.Creation == birth && HasAntiCheatResources(entry)
                    && entry.AntiCheatResources.JobActive && entry.AntiCheatResources.JobPhase == 'A';
            }
        }

        private static bool OpenAntiCheatJob(AntiCheatResourceState state, out string reason)
        {
            reason = null;
            if (AntiCheatJobs == null) { reason = "platform"; return false; }
            if (state.JobHandle != IntPtr.Zero) return true;
            int error;
            state.JobHandle = AntiCheatJobs.Open(state.JobName, out error);
            if (state.JobHandle != IntPtr.Zero) return true;
            reason = "open:" + error;
            return false;
        }

        private static bool ClearAntiCheatJob(Entry entry, Func<bool> checkpoint)
        {
            AntiCheatResourceState state = entry.AntiCheatResources;
            if (!AntiCheatJobNeedsRelease(state))
            {
                if (state != null && state.JobHandle != IntPtr.Zero && AntiCheatJobs != null)
                { AntiCheatJobs.Close(state.JobHandle); state.JobHandle = IntPtr.Zero; }
                return true;
            }
            state.JobActive = false;
            string reason;
            if (!OpenAntiCheatJob(state, out reason))
            {
                if (reason != "open:2" || AntiCheatJobs.Probe(entry.AntiCheatPid, entry.Creation) != 1) return false;
            }
            else
            {
                if (!AntiCheatJobs.Clear(state.JobHandle, out reason)) return false;
                AntiCheatJobs.Close(state.JobHandle);
                state.JobHandle = IntPtr.Zero;
            }
            char before = state.JobPhase;
            state.JobPhase = 'S';
            if (checkpoint != null && !checkpoint()) { state.JobPhase = before; return false; }
            return true;
        }

        internal static bool ClearRecordedAntiCheatJob(int pid, long birth)
        {
            if (AntiCheatJobs == null) return false;
            int error;
            IntPtr job = AntiCheatJobs.Open(AntiCheatJobIdentity.Name(pid, birth), out error);
            if (job == IntPtr.Zero) return error == 2;
            try { string reason; return AntiCheatJobs.Clear(job, out reason); }
            finally { AntiCheatJobs.Close(job); }
        }

        internal bool ReleaseAntiCheatJob(int pid, long birth)
        {
            lock (sync)
            {
                Entry entry;
                return !map.TryGetValue(pid, out entry) || entry.Creation != birth || !HasAntiCheatResources(entry)
                    || ClearAntiCheatJob(entry, () => SaveAntiCheatResourceJournal(entry));
            }
        }

        internal bool ReleaseAntiCheatJobs(string group = null)
        {
            lock (sync)
            {
                bool restored = true;
                foreach (Entry entry in map.Values)
                    if (HasAntiCheatResources(entry) && (group == null || SameName(group, entry.Group)))
                        restored = ClearAntiCheatJob(entry, () => SaveAntiCheatResourceJournal(entry)) && restored;
                return restored;
            }
        }

        private void LogAntiCheatJobFallback(Entry entry, string reason)
        {
            AntiCheatResourceState state = entry.AntiCheatResources;
            if (state.JobLogged) return;
            state.JobLogged = true;
            Logger.Log(() => Logger.Format("ACE Job 未启用 目标={0}/{1} 原因={2} 使用逐进程设置", "ACE Job unavailable target={0}/{1} reason={2} using per-process settings", entry.Name, entry.Creation, reason));
        }

        internal bool EnsureAntiCheatJob(int pid, long birth, ulong mask)
        {
            lock (sync)
            {
                Entry entry;
                if (!map.TryGetValue(pid, out entry) || entry.Creation != birth || !HasAntiCheatResources(entry)
                    || !AntiCheatThrottled(entry) || !SameName(entry.Group, "ace") || (mask & (mask - 1)) != 0) return false;
                AntiCheatResourceState state = entry.AntiCheatResources;
                IAntiCheatJobPlatform platform = AntiCheatJobs;
                if (platform == null) return false;
                long rejectedBirth;
                if (antiCheatJobRejected.TryGetValue(pid, out rejectedBirth) && rejectedBirth == birth) state.JobRejected = true;
                if (state.JobRejected)
                {
                    ClearAntiCheatJob(entry, () => SaveAntiCheatResourceJournal(entry));
                    return false;
                }
                bool priorityOwned = state.Fields.Exists(field => field.Kind == 0 && field.Acquired && field.Phase == 'A' && field.Original >= 0);
                if (!priorityOwned) return false;
                long epoch = Interlocked.Read(ref antiCheatForwardEpoch);
                IntPtr process = IntPtr.Zero;
                string reason = "process-access";
                try
                {
                    process = platform.OpenTarget(pid, birth);
                    if (process == IntPtr.Zero || !AntiCheatAdmissionCurrent(process, entry, epoch)) return RejectAntiCheatJob(entry, reason);
                    if (state.JobActive && state.JobMask == mask)
                    {
                        if (platform.Verify(state.JobHandle, process, birth, mask, out reason)) return true;
                        return RejectAntiCheatJob(entry, reason);
                    }
                    if (AntiCheatJobNeedsRelease(state) && !ClearAntiCheatJob(entry, () => SaveAntiCheatResourceJournal(entry))) return false;
                    state.JobName = AntiCheatJobIdentity.Name(pid, birth);
                    state.JobMask = mask;
                    state.JobPhase = 'N';
                    if (!SaveAntiCheatResourceJournal(entry)) return RejectAntiCheatJob(entry, "intent-journal");
                    int error;
                    state.JobHandle = platform.Open(state.JobName, out error);
                    if (state.JobHandle == IntPtr.Zero)
                    {
                        if (error != 2) return RejectAntiCheatJob(entry, "open:" + error);
                        state.JobHandle = platform.Create(state.JobName, out reason);
                        if (state.JobHandle == IntPtr.Zero) return RejectAntiCheatJob(entry, reason ?? "create");
                        if (!platform.Anchor(state.JobHandle, process, out reason)) return RejectAntiCheatJob(entry, reason);
                    }
                    state.JobPhase = 'P';
                    if (!SaveAntiCheatResourceJournal(entry)) return RejectAntiCheatJob(entry, "armed-journal");
                    if (!AntiCheatAdmissionCurrent(process, entry, epoch)) return RejectAntiCheatJob(entry, "forward-revoked");
                    if (!platform.Apply(state.JobHandle, process, birth, mask, out reason)) return RejectAntiCheatJob(entry, reason);
                    if (!AntiCheatAdmissionCurrent(process, entry, epoch)) return RejectAntiCheatJob(entry, "forward-revoked");
                    state.JobPhase = 'A';
                    if (!SaveAntiCheatResourceJournal(entry)) return RejectAntiCheatJob(entry, "commit-journal");
                    state.JobActive = true;
                    entry.NextReconcileTicks = DateTime.UtcNow.AddSeconds(30).Ticks;
                    return true;
                }
                catch (Exception error) { return RejectAntiCheatJob(entry, "exception:" + error.GetType().Name); }
                finally { if (process != IntPtr.Zero) platform.Close(process); }
            }
        }

        private bool RejectAntiCheatJob(Entry entry, string reason)
        {
            AntiCheatResourceState state = entry.AntiCheatResources;
            antiCheatJobRejected[entry.AntiCheatPid] = entry.Creation;
            state.JobRejected = true;
            state.JobActive = false;
            SaveAntiCheatResourceJournal(entry);
            ClearAntiCheatJob(entry, () => SaveAntiCheatResourceJournal(entry));
            entry.NextReconcileTicks = DateTime.UtcNow.AddSeconds(5).Ticks;
            LogAntiCheatJobFallback(entry, reason);
            return false;
        }
    }
}
