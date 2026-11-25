using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        internal const string FocusJobPrefix = "Local\\Pavise.BackgroundFocusJob.V1.";

        private sealed class FocusJobReceipt
        {
            internal string Name;
            internal uint Priority;
            internal bool Disk, OrdinaryPriority;
        }

        private sealed class FocusResourceJob
        {
            internal string Name, Key;
            internal int IndividualPid;
            internal IntPtr Handle;
            internal bool Active, DiskOwned, SkipDisk, ReleasePending;
            // 限额解除后成员仍留在 Job
            internal readonly Dictionary<int, long> Members = new Dictionary<int, long>();
            internal readonly Dictionary<int, long> Failed = new Dictionary<int, long>();
        }

        private readonly Dictionary<int, string> focusTargets = new Dictionary<int, string>();
        private readonly Dictionary<string, FocusResourceJob> focusResourceJobs =
            new Dictionary<string, FocusResourceJob>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<int, FocusResourceJob> focusIndividualJobs = new Dictionary<int, FocusResourceJob>();

        internal bool FocusUsesPerProcessLimits(string key)
        {
            lock (sync)
            {
                foreach (FocusResourceJob job in focusIndividualJobs.Values)
                    if (job.Active && !job.ReleasePending && string.Equals(job.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        internal void SetResourceJobMode(int mode, IReadOnlyDictionary<int, string> focusedPrograms)
        {
            lock (sync)
            {
                resourceJobMode = mode;
                focusTargets.Clear();
                if (mode == BackgroundGovernancePolicy.Aggressive && focusedPrograms != null)
                {
                    foreach (KeyValuePair<int, string> target in focusedPrograms)
                        if (target.Key > 4 && !string.IsNullOrEmpty(target.Value)) focusTargets[target.Key] = target.Value;
                }

                IResourceJobPlatform platform = ResourceJobPlatform;
                if (platform == null) return;
                List<string> empty = null;
                foreach (KeyValuePair<string, FocusResourceJob> item in focusResourceJobs)
                {
                    FocusResourceJob job = item.Value;
                    if (job.ReleasePending || !FocusMembersStayEligible(platform, item.Key, job))
                        ReleaseFocusGroupLocked(platform, job);
                    if (job.Members.Count != 0 || job.ReleasePending || job.Failed.Count != 0) continue;
                    if (empty == null) empty = new List<string>();
                    empty.Add(item.Key);
                }
                if (empty != null)
                {
                    foreach (string key in empty)
                    {
                        platform.Close(focusResourceJobs[key].Handle);
                        focusResourceJobs.Remove(key);
                    }
                }

                List<int> exitedIndividual = null;
                foreach (KeyValuePair<int, FocusResourceJob> item in focusIndividualJobs)
                {
                    FocusResourceJob job = item.Value;
                    if (job.ReleasePending || !FocusMembersStayEligible(platform, job.Key, job))
                        ReleaseFocusGroupLocked(platform, job);
                    if (job.Members.Count != 0 || job.ReleasePending) continue;
                    if (exitedIndividual == null) exitedIndividual = new List<int>();
                    exitedIndividual.Add(item.Key);
                }
                if (exitedIndividual != null)
                    foreach (int pid in exitedIndividual) focusIndividualJobs.Remove(pid);

                bool changed = false;
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    Entry entry = item.Value;
                    if (entry.ResourceJobName == null) continue;
                    if (mode == BackgroundGovernancePolicy.Aggressive && !IsFocusTarget(entry, item.Key)
                        && LiveOrdinaryResourceJob(entry, item.Key)) continue;
                    if (ReleaseOrdinaryResourceJobLocked(entry, item.Key, false)) changed = true;
                }
                if (changed) SaveBackgroundResourceCleanup();
            }
        }

        internal void RemoveFocusedProgram(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (sync)
            {
                var remove = new List<int>();
                foreach (KeyValuePair<int, string> target in focusTargets)
                    if (string.Equals(target.Value, key, StringComparison.OrdinalIgnoreCase)) remove.Add(target.Key);
                foreach (int pid in remove) focusTargets.Remove(pid);

                FocusResourceJob job;
                IResourceJobPlatform platform = ResourceJobPlatform;
                if (platform != null && focusResourceJobs.TryGetValue(key, out job))
                    ReleaseFocusGroupLocked(platform, job);
                if (platform != null)
                    foreach (FocusResourceJob individual in focusIndividualJobs.Values)
                        if (string.Equals(individual.Key, key, StringComparison.OrdinalIgnoreCase))
                            ReleaseFocusGroupLocked(platform, individual);
            }
        }

        private bool IsFocusTarget(Entry entry, int pid)
        {
            return resourceJobMode == BackgroundGovernancePolicy.Aggressive && focusTargets.ContainsKey(pid)
                && entry != null && (entry.Reasons & SuppressReason.Background) != 0
                && (entry.Reasons & SuppressReason.AntiCheat) == 0 && !AntiCheatCatalog.IsAntiCheatLikeName(entry.Name);
        }

        private bool FocusMembersStayEligible(IResourceJobPlatform platform, string key, FocusResourceJob job)
        {
            bool eligible = resourceJobMode == BackgroundGovernancePolicy.Aggressive;
            var exited = new List<int>();
            foreach (KeyValuePair<int, long> member in job.Members)
            {
                string target;
                Entry entry;
                if (focusTargets.TryGetValue(member.Key, out target) && string.Equals(target, key, StringComparison.OrdinalIgnoreCase)
                    && map.TryGetValue(member.Key, out entry) && entry.Creation == member.Value && IsFocusTarget(entry, member.Key))
                    continue;
                if (platform.Probe(member.Key, member.Value) == 1) exited.Add(member.Key);
                else eligible = false;
            }
            foreach (int pid in exited) job.Members.Remove(pid);
            exited.Clear();
            foreach (KeyValuePair<int, long> failure in job.Failed)
                if (!focusTargets.ContainsKey(failure.Key) || platform.Probe(failure.Key, failure.Value) == 1) exited.Add(failure.Key);
            foreach (int pid in exited) job.Failed.Remove(pid);
            return eligible && job.Members.Count != 0;
        }

        private static bool FocusMemberApplied(Entry entry, int pid, FocusResourceJob job)
        {
            long creation;
            return entry.FocusJob != null && job.Active && !job.ReleasePending && entry.FocusJob.Name == job.Name
                && job.Members.TryGetValue(pid, out creation) && creation == entry.Creation;
        }

        private bool FocusResourceJobMatches(Entry entry, int pid)
        {
            if (entry.ResourceJobName != null) return false;
            string key = focusTargets[pid];
            FocusResourceJob individual;
            if (focusIndividualJobs.TryGetValue(pid, out individual) && string.Equals(individual.Key, key, StringComparison.OrdinalIgnoreCase)
                && FocusMemberApplied(entry, pid, individual)) return true;
            FocusResourceJob job;
            long creation;
            return focusResourceJobs.TryGetValue(key, out job)
                && ((job.Failed.TryGetValue(pid, out creation) && creation == entry.Creation) || FocusMemberApplied(entry, pid, job));
        }

        private void EnsureFocusResourceJob(IResourceJobPlatform platform, Entry entry, int pid)
        {
            if (FocusResourceJobMatches(entry, pid) || entry.ResourceJobName != null || entry.FocusJob != null) return;
            string key = focusTargets[pid];
            FocusResourceJob job;
            if (!focusResourceJobs.TryGetValue(key, out job))
            {
                job = new FocusResourceJob { Key = key };
                focusResourceJobs[key] = job;
            }
            if (job.ReleasePending || (job.Members.Count != 0 && !FocusMembersStayEligible(platform, key, job))) return;
            IntPtr process = platform.OpenTarget(pid, entry.Creation);
            if (process == IntPtr.Zero) { SkipFocusJobMember(job, entry, pid, "process-access"); return; }
            try
            {
                if (!platform.SameSession(process)) { SkipFocusJobMember(job, entry, pid, "other-session"); return; }
                if (!platform.MatchesFocusProgram(process, key)) { SkipFocusJobMember(job, entry, pid, "image-changed"); return; }
                bool ordinaryPriority;
                uint priority = FocusRestorePriority(platform, entry, process, out ordinaryPriority);
                if (priority == 0) { SkipFocusJobMember(job, entry, pid, "priority-read"); return; }

                string reason;
                long creation;
                bool knownMember = job.Members.TryGetValue(pid, out creation) && creation == entry.Creation;
                if (job.Handle == IntPtr.Zero)
                {
                    job.Name = FocusJobPrefix + Guid.NewGuid().ToString("N");
                    job.Handle = platform.CreateAnchored(job.Name, process, out reason);
                    if (job.Handle == IntPtr.Zero)
                    {
                        SkipFocusJobMember(job, entry, pid, reason ?? "create");
                        return;
                    }
                }
                else if (!knownMember && !platform.Anchor(job.Handle, process, out reason))
                {
                    SkipFocusJobMember(job, entry, pid, reason ?? "anchor");
                    return;
                }

                List<Entry> prepared = null;
                if (!job.Active && !PrepareFocusMembers(platform, job, pid, out prepared))
                {
                    SkipFocusJobMember(job, entry, pid, "peer-priority-read");
                    return;
                }
                QueueFocusJob(platform, job, job, entry, pid, priority, ordinaryPriority, prepared, null);
            }
            catch (Exception error)
            {
                SkipFocusJobMember(job, entry, pid, "exception:" + error.GetType().Name);
            }
            finally { platform.Close(process); }
        }

        private void PrepareIndividualFocusJob(IResourceJobPlatform platform, FocusResourceJob aggregate, Entry entry, int pid,
            IntPtr process, uint priority, bool ordinaryPriority, string groupFailure)
        {
            ResourceJob own;
            if (resourceJobs.TryGetValue(pid, out own) && own.Creation != entry.Creation)
            {
                platform.Close(own.Job);
                resourceJobs.Remove(pid);
                own = null;
            }
            if (own == null || own.Job == IntPtr.Zero)
            {
                string name = ResourceJobPrefix + Guid.NewGuid().ToString("N") + "." + pid + "." + entry.Creation;
                string reason;
                IntPtr handle = platform.CreateAnchored(name, process, out reason);
                if (handle == IntPtr.Zero)
                {
                    SkipFocusJobMember(aggregate, entry, pid, reason ?? "own-create");
                    return;
                }
                own = new ResourceJob { Name = name, Job = handle, Pid = pid, Creation = entry.Creation };
                resourceJobs[pid] = own;
            }
            FocusResourceJob individual;
            if (!focusIndividualJobs.TryGetValue(pid, out individual) || individual.Name != own.Name)
            {
                individual = new FocusResourceJob { Name = own.Name, Handle = own.Job, IndividualPid = pid };
                focusIndividualJobs[pid] = individual;
            }
            if (individual.ReleasePending) return;
            individual.Key = aggregate.Key;
            QueueFocusJob(platform, aggregate, individual, entry, pid, priority, ordinaryPriority, null, groupFailure);
        }

        private void QueueFocusJob(IResourceJobPlatform platform, FocusResourceJob aggregate, FocusResourceJob job, Entry entry,
            int pid, uint priority, bool ordinaryPriority, List<Entry> prepared, string groupFailure)
        {
            var receipt = new FocusJobReceipt { Name = job.Name, Priority = priority, OrdinaryPriority = ordinaryPriority,
                Disk = job.DiskOwned || !job.SkipDisk };
            entry.FocusJob = receipt;
            CommitBackgroundResource(
                () =>
                {
                    if (entry.FocusJob != receipt) return;
                    IntPtr process = IntPtr.Zero;
                    try
                    {
                        Entry current;
                        string key;
                        if (!map.TryGetValue(pid, out current) || current != entry || !IsFocusTarget(entry, pid)
                            || !focusTargets.TryGetValue(pid, out key) || !string.Equals(key, aggregate.Key, StringComparison.OrdinalIgnoreCase)
                            || (!job.Active && !FocusPeersAreJournaled(job)))
                        {
                            DropUnusedFocusReceipt(entry, prepared);
                            return;
                        }
                        process = platform.OpenTarget(pid, entry.Creation);
                        if (process == IntPtr.Zero || !platform.MatchesFocusProgram(process, aggregate.Key))
                        {
                            DropUnusedFocusReceipt(entry, prepared);
                            SkipFocusJobMember(aggregate, entry, pid, "process-changed");
                            return;
                        }
                        bool ordinaryNow;
                        if (FocusRestorePriority(platform, entry, process, out ordinaryNow) != priority || ordinaryNow != ordinaryPriority)
                        {
                            DropUnusedFocusReceipt(entry, prepared);
                            return;
                        }
                        ApplyQueuedFocusJob(platform, aggregate, job, entry, pid, process, receipt, groupFailure);
                    }
                    catch (Exception error)
                    {
                        SkipFocusJobMember(aggregate, entry, pid, "exception:" + error.GetType().Name);
                        if (entry.FocusJob != receipt) return;
                        job.Members[pid] = entry.Creation;
                        job.Active = true;
                        ReleaseFocusGroupLocked(platform, job);
                    }
                    finally { if (process != IntPtr.Zero) platform.Close(process); }
                },
                () =>
                {
                    if (entry.FocusJob != receipt) return;
                    DropUnusedFocusReceipt(entry, prepared, false);
                    SkipFocusJobMember(aggregate, entry, pid, "journal");
                });
        }

        private bool FocusPeersAreJournaled(FocusResourceJob job)
        {
            foreach (KeyValuePair<int, long> peer in job.Members)
            {
                Entry entry;
                if (!map.TryGetValue(peer.Key, out entry) || entry.Creation != peer.Value
                    || entry.FocusJob == null || entry.FocusJob.Name != job.Name) return false;
            }
            return true;
        }

        private void DropUnusedFocusReceipt(Entry entry, List<Entry> prepared, bool checkpoint = true)
        {
            entry.FocusJob = null;
            if (prepared != null)
                foreach (Entry member in prepared) member.FocusJob = null;
            if (checkpoint) SaveBackgroundResourceCleanup();
        }

        private void ApplyQueuedFocusJob(IResourceJobPlatform platform, FocusResourceJob aggregate, FocusResourceJob job,
            Entry entry, int pid, IntPtr process, FocusJobReceipt receipt, string groupFailure)
        {
            var limits = new ResourceJobLimits
            {
                CpuRate = BackgroundGovernancePolicy.FocusCpuRate(BackgroundResourceJobPlatform.LogicalProcessorCount),
                IoBandwidth = BackgroundGovernancePolicy.AggressiveIoBandwidth,
                LockIdle = true,
            };
            string reason;
            if (!platform.Apply(job.Handle, process, limits, out reason))
            {
                if (job == aggregate && (reason ?? "").StartsWith("assign:", StringComparison.Ordinal))
                {
                    entry.FocusJob = null;
                    if (job.Members.Count != 0)
                    {
                        job.Active = true;
                        if (!job.DiskOwned && !job.SkipDisk) ApplyFocusDisk(platform, job, entry, limits.IoBandwidth);
                    }
                    else
                    {
                        string ignored;
                        platform.Clear(job.Handle, out ignored);
                    }
                    SaveBackgroundResourceCleanup();
                    PrepareIndividualFocusJob(platform, aggregate, entry, pid, process, receipt.Priority, receipt.OrdinaryPriority, reason);
                }
                else
                {
                    SkipFocusJobMember(aggregate, entry, pid, reason ?? "apply");
                    job.Members[pid] = entry.Creation;
                    job.Active = true;
                    ReleaseFocusGroupLocked(platform, job);
                }
                return;
            }
            job.Members[pid] = entry.Creation;
            job.Active = true;
            resourceJobJoined++;
            if (!job.DiskOwned && !job.SkipDisk) ApplyFocusDisk(platform, job, entry, limits.IoBandwidth);
            if (groupFailure != null && resourceJobReasonsLogged.Add("focus-per-process:" + aggregate.Key))
                Logger.Info(Logger.Format("重点压制按进程限额 进程={0} 合并失败原因={1}",
                    "Focused suppression is capped per process: process={0} merge failure={1}", entry.Name, groupFailure));
        }

        private bool FocusPriorityStillOwned(Entry entry)
        {
            if (entry.FocusJob == null || entry.FocusJob.Priority == 0) return false;
            FocusResourceJob individual;
            if (focusIndividualJobs.TryGetValue(entry.AccountingPid, out individual) && individual.Name == entry.FocusJob.Name)
                return individual.Active;
            foreach (FocusResourceJob job in focusResourceJobs.Values)
                if (job.Name == entry.FocusJob.Name) return job.Active;
            return false;
        }

        private static uint FocusRestorePriority(IResourceJobPlatform platform, Entry entry, IntPtr process, out bool ordinaryPriority)
        {
            uint priority = platform.ReadPriority(process);
            ordinaryPriority = priority == IdlePriorityClass && entry.OrdinaryPriorityOwned;
            return ordinaryPriority ? entry.OrigPri : priority;
        }

        private bool PrepareFocusMembers(IResourceJobPlatform platform, FocusResourceJob job, int currentPid, out List<Entry> prepared)
        {
            prepared = new List<Entry>();
            foreach (KeyValuePair<int, long> member in job.Members)
            {
                if (member.Key == currentPid) continue;
                Entry entry = map[member.Key];
                if (entry.FocusJob != null && entry.FocusJob.Name == job.Name) continue;
                IntPtr process = platform.OpenTarget(member.Key, member.Value);
                uint priority = 0;
                bool ordinaryPriority = false;
                if (process != IntPtr.Zero)
                {
                    try { priority = FocusRestorePriority(platform, entry, process, out ordinaryPriority); }
                    finally { platform.Close(process); }
                }
                if (priority == 0)
                {
                    foreach (Entry prior in prepared) prior.FocusJob = null;
                    return false;
                }
                entry.FocusJob = new FocusJobReceipt { Name = job.Name, Priority = priority, OrdinaryPriority = ordinaryPriority,
                    Disk = job.DiskOwned || !job.SkipDisk };
                prepared.Add(entry);
            }
            return true;
        }

        private void ApplyFocusDisk(IResourceJobPlatform platform, FocusResourceJob job, Entry entry, long bandwidth)
        {
            SetFocusDiskReceipt(job, true);
            job.DiskOwned = true;
            string reason;
            try
            {
                if (platform.SetIoBandwidth(job.Handle, bandwidth, out reason)) return;
            }
            catch (Exception error)
            {
                job.SkipDisk = true;
                LogResourceIoSkipped(entry, "exception:" + error.GetType().Name);
                return;
            }
            job.SkipDisk = true;
            job.DiskOwned = false;
            SetFocusDiskReceipt(job, false);
            SaveBackgroundResourceCleanup();
            LogResourceIoSkipped(entry, reason);
        }

        private void SetFocusDiskReceipt(FocusResourceJob job, bool disk)
        {
            if (job.IndividualPid != 0)
            {
                Entry entry;
                if (map.TryGetValue(job.IndividualPid, out entry) && entry.FocusJob != null && entry.FocusJob.Name == job.Name)
                    entry.FocusJob.Disk = disk;
                return;
            }
            foreach (Entry entry in map.Values)
                if (entry.FocusJob != null && entry.FocusJob.Name == job.Name) entry.FocusJob.Disk = disk;
        }

        private void SkipFocusJobMember(FocusResourceJob job, Entry entry, int pid, string reason)
        {
            job.Failed[pid] = entry.Creation;
            resourceJobFallbacks++;
            LogResourceJobReason("focus-" + reason, entry);
        }

        private bool ReleaseFocusForEntry(Entry entry, int pid)
        {
            if (entry.FocusJob == null) return true;
            IResourceJobPlatform platform = ResourceJobPlatform;
            if (platform == null) return false;
            FocusResourceJob individual;
            if (focusIndividualJobs.TryGetValue(pid, out individual) && individual.Name == entry.FocusJob.Name)
                return ReleaseFocusGroupLocked(platform, individual);
            foreach (FocusResourceJob job in focusResourceJobs.Values)
                if (job.Name == entry.FocusJob.Name) return ReleaseFocusGroupLocked(platform, job);
            string reason;
            if (!RecoverFocusJob(platform, entry, pid, out reason, () => RearmRestoredOrdinaryFields(entry, OrdinaryFields.Priority)))
            {
                LogResourceJobReason("focus-release:" + reason, entry);
                return false;
            }
            return SaveBackgroundResourceCleanup();
        }

        private bool ReleaseFocusGroupLocked(IResourceJobPlatform platform, FocusResourceJob job)
        {
            if (!job.Active && !job.ReleasePending && !job.DiskOwned) return true;
            job.ReleasePending = true;
            string reason;
            if (job.Active)
            {
                if (!platform.Clear(job.Handle, out reason))
                {
                    LogResourceJobReason("focus-release:" + reason, null);
                    return false;
                }
                job.Active = false;
            }
            if (job.DiskOwned)
            {
                if (ClearResourceIo(platform, job.Handle, out reason)) job.DiskOwned = false;
                else LogResourceJobReason("focus-release-disk:" + reason, null);
            }

            bool priorityRestored = true;
            if (job.IndividualPid != 0)
            {
                Entry entry;
                if (map.TryGetValue(job.IndividualPid, out entry))
                    priorityRestored = ReleaseFocusMember(platform, job, entry, job.IndividualPid);
            }
            else
                foreach (KeyValuePair<int, Entry> item in map)
                    priorityRestored &= ReleaseFocusMember(platform, job, item.Value, item.Key);
            bool saved = SaveBackgroundResourceCleanup();
            job.ReleasePending = job.DiskOwned || !priorityRestored || !saved;
            return !job.ReleasePending;
        }

        private bool ReleaseFocusMember(IResourceJobPlatform platform, FocusResourceJob job, Entry entry, int pid)
        {
            FocusJobReceipt receipt = entry.FocusJob;
            if (receipt == null || receipt.Name != job.Name) return true;
            receipt.Disk = job.DiskOwned;
            var inherited = new InheritedPriorities(entry, receipt.Priority, receipt.Priority == 0 ? 0u : IdlePriorityClass, -1, -1);
            if (!RestoreFocusPriority(platform, entry, pid, () => RearmRestoredOrdinaryFields(entry, OrdinaryFields.Priority)))
            {
                LogResourceJobReason("focus-release-priority", entry);
                return false;
            }
            RestoreInheritedDescendants(pid, entry, inherited);
            if (receipt.Priority == 0 && !receipt.Disk) entry.FocusJob = null;
            return true;
        }

        private static bool RestoreFocusPriority(IResourceJobPlatform platform, Entry entry, int pid, Action rearmOrdinary = null)
        {
            FocusJobReceipt receipt = entry.FocusJob;
            if (receipt.Priority == 0) return true;
            bool restored;
            if (!platform.RestoreIdlePriority(pid, entry.Creation, receipt.Priority, out restored)) return false;
            if (restored && receipt.OrdinaryPriority && rearmOrdinary != null) rearmOrdinary();
            if (receipt.Priority != IdlePriorityClass) entry.OrdinaryPriorityOwned = false;
            receipt.Priority = 0;
            return true;
        }

        private static bool RecoverFocusJob(IResourceJobPlatform platform, Entry entry, int pid, out string reason, Action rearmOrdinary = null)
        {
            reason = "platform";
            if (platform == null) return false;
            FocusJobReceipt receipt = entry.FocusJob;
            int error;
            IntPtr job = platform.Open(receipt.Name, out error);
            if (job == IntPtr.Zero)
            {
                reason = "open:" + error;
                return false;
            }
            try
            {
                if (!platform.Clear(job, out reason)) return false;
                if (receipt.Disk && ClearResourceIo(platform, job, out reason)) receipt.Disk = false;
                if (!RestoreFocusPriority(platform, entry, pid, rearmOrdinary))
                { reason = "priority"; return false; }
                if (receipt.Disk) return false;
                entry.FocusJob = null;
                return true;
            }
            finally { platform.Close(job); }
        }

        private static bool ValidFocusJobName(string name)
        {
            Guid nonce;
            return name != null && name.StartsWith(FocusJobPrefix, StringComparison.Ordinal)
                && name.Length == FocusJobPrefix.Length + 32
                && Guid.TryParseExact(name.Substring(FocusJobPrefix.Length), "N", out nonce) && nonce != Guid.Empty;
        }
    }
}
