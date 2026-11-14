using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private sealed class AntiCheatResourceField
        {
            internal string Plan;
            internal int Kind, Failures, RecoveryFailures;
            internal long Original, Expected, Previous;
            internal char Phase = 'N';
            internal bool Acquired;
        }

        private sealed class AntiCheatResourceState
        {
            internal string Plan = "";
            internal bool Active = true;
            internal bool TimerSealUnavailable;
            internal int LegacyRecoveryFailures;
            internal readonly List<AntiCheatResourceField> Fields = new List<AntiCheatResourceField>();
            internal int MaintenanceFailures;
            internal long NextLog;
            internal long NextCorrectionLog;
        }

        private long antiCheatForwardEpoch;
        private long antiCheatJournalFailureLogAt;
        private readonly object antiCheatAdmissionGate = new object();
        private sealed class AntiCheatResourceRequest
        {
            internal long Birth;
            internal string Name, Group;
            internal SuppressionLevel Level;
        }
        private readonly Dictionary<int, AntiCheatResourceRequest> antiCheatRequests = new Dictionary<int, AntiCheatResourceRequest>();
        private readonly HashSet<string> antiCheatRevokedTargets = new HashSet<string>(StringComparer.Ordinal);
        internal Func<string, SuppressionLevel, bool> AntiCheatForwardAdmission;
        private readonly Dictionary<string, AntiCheatResourceState> antiCheatResourceHistory = new Dictionary<string, AntiCheatResourceState>(StringComparer.Ordinal);
        private long antiCheatHistoryCycle = -1;
        private sealed class AntiCheatSnapshotBudget
        {
            internal int Failures, InFlight;
            internal long NextLog;
        }
        private sealed class AntiCheatSnapshotAttempt
        {
            internal AntiCheatSnapshotBudget Budget;
            internal bool Completed;
        }
        private readonly Dictionary<string, AntiCheatSnapshotBudget> antiCheatSnapshotBudgets = new Dictionary<string, AntiCheatSnapshotBudget>(StringComparer.Ordinal);
        private long antiCheatSnapshotCycle = -1;
        [ThreadStatic] private static SuppressionCore antiCheatAdmissionOwner;
        [ThreadStatic] private static long antiCheatAdmissionEpoch;

        private void CaptureAntiCheatAdmission()
        { antiCheatAdmissionOwner = this; antiCheatAdmissionEpoch = Interlocked.Read(ref antiCheatForwardEpoch); }

        private bool BeginAntiCheatSnapshot(int pid, long expectedBirth, string name, SuppressionLevel level, out AntiCheatSnapshotAttempt attempt)
        {
            attempt = null;
            lock (sync)
            {
                Entry entry;
                if (map.TryGetValue(pid, out entry) && entry.Creation > 0 && entry.OrigPri != uint.MaxValue
                    && (expectedBirth <= 0 || entry.Creation == expectedBirth) && SameName(entry.Name, name)) return true;
                long cycle = PerformanceTuning.RecoveryCycleSerial;
                if (antiCheatSnapshotCycle != cycle) { antiCheatSnapshotCycle = cycle; antiCheatSnapshotBudgets.Clear(); }
                // 未知创建时间固定落在保守桶中，不靠每轮观察换身份补充首次失败次数。
                string key = cycle + "/" + pid + "/" + Math.Max(0, expectedBirth) + "/" + B64((name ?? "").ToUpperInvariant()) + "/" + level;
                AntiCheatSnapshotBudget budget;
                if (!antiCheatSnapshotBudgets.TryGetValue(key, out budget))
                {
                    if (antiCheatSnapshotBudgets.Count >= 1024) return false;
                    antiCheatSnapshotBudgets[key] = budget = new AntiCheatSnapshotBudget();
                }
                if (budget.Failures + budget.InFlight >= 3)
                {
                    if (DateTime.UtcNow.Ticks >= budget.NextLog)
                    {
                        budget.NextLog = DateTime.UtcNow.AddSeconds(30).Ticks;
                        Logger.Log("反作弊首次快照暂停：目标=" + name + "/" + expectedBirth + " 档位=" + level + "；本阶段失败或在途预算已满");
                    }
                    return false;
                }
                budget.InFlight++;
                attempt = new AntiCheatSnapshotAttempt { Budget = budget };
                return true;
            }
        }

        private void CompleteAntiCheatSnapshot(AntiCheatSnapshotAttempt attempt)
        {
            if (attempt == null) return;
            lock (sync)
            {
                attempt.Budget.InFlight--;
                if (!attempt.Completed) attempt.Budget.Failures = Math.Min(3, attempt.Budget.Failures + 1);
            }
        }

        internal void RevokeAntiCheatRuntimeWrites(string group = null)
        {
            lock (antiCheatAdmissionGate)
            {
                Interlocked.Increment(ref antiCheatForwardEpoch);
                var removed = new List<int>();
                foreach (KeyValuePair<int, AntiCheatResourceRequest> pair in antiCheatRequests)
                    if (group == null || SameName(pair.Value.Group, group))
                    {
                        antiCheatRevokedTargets.Add(pair.Key + "/" + pair.Value.Birth);
                        removed.Add(pair.Key);
                    }
                foreach (int pid in removed) antiCheatRequests.Remove(pid);
            }
        }

        private void RevokeAntiCheatAdmission()
        { RevokeAntiCheatRuntimeWrites(); }

        private void RevokeAntiCheatTarget(int pid)
        {
            lock (antiCheatAdmissionGate)
            {
                Interlocked.Increment(ref antiCheatForwardEpoch);
                AntiCheatResourceRequest request;
                if (!antiCheatRequests.TryGetValue(pid, out request)) return;
                antiCheatRevokedTargets.Add(pid + "/" + request.Birth);
                antiCheatRequests.Remove(pid);
            }
        }

        internal void RevokeAntiCheatForwardWrites()
        { RevokeAntiCheatAdmission(); }

        private bool SaveAntiCheatResourceJournal(Entry entry)
        {
            bool saved = SaveJournalLocked(true, true);
            entry.AntiCheatResourceJournalPending = !saved;
            return saved;
        }

        private bool RegisterAntiCheatRequest(int pid, long birth, string name, string group, SuppressionLevel level)
        {
            if (!AntiCheatAdmissionEnabled(group, level)) return false;
            long entered = object.ReferenceEquals(antiCheatAdmissionOwner, this) ? antiCheatAdmissionEpoch : Interlocked.Read(ref antiCheatForwardEpoch);
            lock (antiCheatAdmissionGate)
            {
                if (entered != Interlocked.Read(ref antiCheatForwardEpoch)) return false;
                AntiCheatResourceRequest prior;
                if (antiCheatRequests.TryGetValue(pid, out prior))
                {
                    if (birth <= 0) birth = prior.Birth;
                    if (prior.Birth != birth || !SameName(prior.Name, name) || !SameName(prior.Group, group) || prior.Level != level)
                    {
                        antiCheatRevokedTargets.Add(pid + "/" + prior.Birth);
                        Interlocked.Increment(ref antiCheatForwardEpoch);
                    }
                }
                antiCheatRequests[pid] = new AntiCheatResourceRequest { Birth = birth, Name = name, Group = group, Level = level };
                CaptureAntiCheatAdmission();
                return true;
            }
        }

        private bool AntiCheatAdmissionEnabled(string group, SuppressionLevel level)
        {
            var admission = AntiCheatForwardAdmission;
            try { return admission == null || admission(group, level); }
            catch { return false; }
        }

        private bool BindAntiCheatRequest(int pid, long birth, string name, string group, SuppressionLevel level)
        {
            lock (antiCheatAdmissionGate)
            {
                AntiCheatResourceRequest request;
                if (antiCheatAdmissionEpoch != Interlocked.Read(ref antiCheatForwardEpoch) || !antiCheatRequests.TryGetValue(pid, out request)
                    || (request.Birth > 0 && request.Birth != birth) || !SameName(request.Name, name) || !SameName(request.Group, group) || request.Level != level) return false;
                request.Birth = birth;
                return true;
            }
        }

        private bool AntiCheatAdmissionCurrent(IntPtr handle, Entry entry, long epoch)
        {
            if (epoch != Interlocked.Read(ref antiCheatForwardEpoch) || entry.Reasons == SuppressReason.None || !AntiCheatThrottled(entry)
                || entry.RestoreInFlight || !AntiCheatAdmissionEnabled(entry.Group, entry.AntiCheatLevel) || EntryIdentity(handle, entry) != 0) return false;
            lock (antiCheatAdmissionGate)
            {
                AntiCheatResourceRequest request;
                return epoch == Interlocked.Read(ref antiCheatForwardEpoch) && antiCheatRequests.TryGetValue(entry.AntiCheatPid, out request)
                    && request.Birth == entry.Creation && SameName(request.Name, entry.Name) && SameName(request.Group, entry.Group) && request.Level == entry.AntiCheatLevel;
            }
        }

        private static bool HasAntiCheatResources(Entry entry)
        { return entry != null && entry.AntiCheatResources != null; }

        private static bool OwnsAntiCheatResources(Entry entry)
        { return HasAntiCheatResources(entry) && entry.AntiCheatResources.Active; }

        internal bool AntiCheatResourceRecoveryPending
        {
            get
            {
                lock (sync)
                {
                    foreach (Entry entry in map.Values)
                    {
                        if (!HasAntiCheatResources(entry)) continue;
                        if (entry.AntiCheatResourceRemovalPending || entry.AntiCheatResourceJournalPending) return true;
                        if (!entry.AntiCheatResources.Active && entry.Journaled) return true;
                        foreach (AntiCheatResourceField field in entry.AntiCheatResources.Fields)
                            if (field.Phase == 'P' || field.Phase == 'A') return true;
                    }
                    return false;
                }
            }
        }

        private bool AttachAntiCheatResourceHistory(int pid, Entry entry)
        {
            entry.AntiCheatPid = pid;
            long cycle = PerformanceTuning.RecoveryCycleSerial;
            if (cycle != antiCheatHistoryCycle) { antiCheatHistoryCycle = cycle; antiCheatResourceHistory.Clear(); }
            string key = pid + "/" + entry.Creation;
            AntiCheatResourceState prior;
            if (!HasAntiCheatResources(entry) && antiCheatResourceHistory.TryGetValue(key, out prior)) entry.AntiCheatResources = prior;
            if (!HasAntiCheatResources(entry)) entry.AntiCheatResources = new AntiCheatResourceState();
            if (!antiCheatResourceHistory.ContainsKey(key) && antiCheatResourceHistory.Count >= 1024) return false;
            antiCheatResourceHistory[key] = entry.AntiCheatResources;
            return true;
        }

        private static bool HasAcquiredAntiCheatResources(Entry entry)
        {
            if (!HasAntiCheatResources(entry) || !AntiCheatThrottled(entry)) return false;
            foreach (AntiCheatResourceField field in entry.AntiCheatResources.Fields)
                if (field.Plan == entry.AntiCheatResources.Plan && field.Acquired && field.Phase != 'S' && field.Kind != 5) return true;
            return false;
        }

        private static long QosValue(int control, int state)
        { return ((long)(uint)control << 32) | (uint)state; }

        private static long MergeAntiCheatQos(long current, long desired)
        { return QosValue(((int)(current >> 32) & ~5) | ((int)(desired >> 32) & 5), ((int)current & ~5) | ((int)desired & 5)); }

        private static bool AntiCheatResourceMatches(int kind, long current, long desired)
        { return kind == 3 ? (((current ^ desired) & QosValue(5, 5)) == 0) : current == desired; }

        private static bool ReadAntiCheatResource(IntPtr handle, int kind, out long value)
        {
            value = -1;
            switch (kind)
            {
            case 0: uint priority = Native.GetPriorityClass(handle); value = priority; return priority != 0;
            case 1: value = Native.QueryIoPriority(handle); return value >= 0;
            case 2: value = Native.QueryPagePriority(handle); return value >= 0;
            case 3:
                int control, state;
                if (!Native.TryQueryPowerThrottling(handle, out control, out state) || control < 0 || state < 0) return false;
                value = QosValue(control, state); return true;
            case 4: value = Native.QueryBoostDisabled(handle); return value == 0 || value == 1;
            case 5:
                int gpu;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out gpu) != 0) return false;
                value = gpu; return gpu >= 0;
            default: return false;
            }
        }

        private static bool WriteAntiCheatResource(IntPtr handle, int kind, long value, bool forward)
        {
            switch (kind)
            {
            case 0: return Native.SetPriorityClass(handle, (uint)value);
            case 1: return Native.TrySetIoPriority(handle, (int)value);
            case 2: return Native.TrySetPagePriority(handle, (int)value);
            case 3: return Native.RestorePowerThrottling(handle, (int)(value >> 32), (int)value);
            case 4: return Native.TrySetBoostDisabled(handle, value == 1);
            case 5: return Native.D3DKMTSetProcessSchedulingPriorityClass(handle, (int)value) == 0;
            default: return false;
            }
        }

        private static long AntiCheatResourceTarget(Entry entry, int kind)
        {
            switch (kind)
            {
            case 0: return DesiredPriority(entry.Level, entry.OrigPri, true);
            case 1: return DesiredIoPriority(entry.Level, true);
            case 2: return DesiredPagePriority(entry.Level, true);
            case 3:
                uint mask = Native.EcoQoSWantMask(DesiredTimerSeal(entry.Level, true) && !entry.AntiCheatResources.TimerSealUnavailable);
                return QosValue((int)mask, (int)mask);
            case 4: return DesiredBoostDisabled(entry.Level, true, entry.OrigBoost);
            case 5: return DesiredGpu(entry);
            default: return -1;
            }
        }

        private static string AntiCheatResourcePlan(Entry entry)
        { return PerformanceTuning.RecoveryCycleSerial + "/" + entry.AntiCheatLevel + "/" + entry.BackgroundLevel + "/" + GpuDemoteEnabled; }

        private bool PauseAntiCheatResources(Entry entry, string detail)
        {
            AntiCheatResourceState state = entry.AntiCheatResources;
            state.MaintenanceFailures = Math.Min(6, state.MaintenanceFailures + 1);
            int delay = Math.Min(30, 1 << (state.MaintenanceFailures - 1));
            entry.NextReconcileTicks = DateTime.UtcNow.AddSeconds(delay).Ticks;
            entry.SetApplied(false);
            LastApplyError = detail;
            if (DateTime.UtcNow.Ticks >= state.NextLog)
            {
                state.NextLog = DateTime.UtcNow.AddSeconds(30).Ticks;
                Logger.Log("反作弊资源维护暂停：目标=" + entry.Name + "/" + entry.Creation + " 阶段=" + detail
                    + "；保留原目标及恢复归属，退避=" + delay + "秒");
            }
            return false;
        }

        private bool ApplyAntiCheatResources(IntPtr handle, Entry entry)
        {
            long epoch = object.ReferenceEquals(antiCheatAdmissionOwner, this) ? antiCheatAdmissionEpoch : Interlocked.Read(ref antiCheatForwardEpoch);
            if (!HasAntiCheatResources(entry)) entry.AntiCheatResources = new AntiCheatResourceState();
            AntiCheatResourceState state = entry.AntiCheatResources;
            string plan = AntiCheatResourcePlan(entry);
            if (!AntiCheatAdmissionCurrent(handle, entry, epoch)) return PauseAntiCheatResources(entry, "identity-or-forward-unknown");
            if (!state.Active)
            {
                // 普通后台仍持有的资源先按其原有有限恢复链结清，再转交给反作弊字段日志。
                if (!RestoreBackgroundPlacement(handle, entry) || !RestoreRecordedAntiCheatResources(handle, entry)) return PauseAntiCheatResources(entry, "background-resource-handoff-pending");
                state.Active = true;
                if (!SaveAntiCheatResourceJournal(entry)) { state.Active = false; return PauseAntiCheatResources(entry, "resource-handoff-journal"); }
            }
            string revokedTarget = entry.AntiCheatPid + "/" + entry.Creation;
            bool revoked;
            lock (antiCheatAdmissionGate) revoked = antiCheatRevokedTargets.Contains(revokedTarget);
            if (revoked)
            {
                // 同档重新启用也必须先结清旧资格，已耗首次及恢复预算继续保留。
                if (!RestoreOwnedAntiCheatResources(handle, entry)) return PauseAntiCheatResources(entry, "revoked-resource-recovery-pending");
                bool current;
                lock (antiCheatAdmissionGate)
                {
                    current = epoch == Interlocked.Read(ref antiCheatForwardEpoch);
                    if (current) antiCheatRevokedTargets.Remove(revokedTarget);
                }
                if (!current) return PauseAntiCheatResources(entry, "resource-readmission-revoked");
            }
            if (state.Plan != plan)
            {
                if (!RestoreOwnedAntiCheatResources(handle, entry)) return PauseAntiCheatResources(entry, "previous-plan-recovery-pending");
                state.Plan = plan;
                string cycle = PerformanceTuning.RecoveryCycleSerial + "/";
                state.Fields.RemoveAll(field => !field.Plan.StartsWith(cycle, StringComparison.Ordinal) && (field.Phase == 'S' || field.Phase == 'N'));
                foreach (AntiCheatResourceField field in state.Fields)
                    if (field.Plan == plan && field.Phase == 'S') { field.Phase = 'N'; field.Acquired = false; field.Original = -1; }
                if (!SaveAntiCheatResourceJournal(entry)) return PauseAntiCheatResources(entry, "plan-journal");
            }
            bool complete = true;
            var corrected = new List<string>();
            for (int kind = 0; kind <= 5; kind++)
            {
                if (kind == 3 && !Native.PowerThrottlingSupported) continue;
                // GPU 仍只属于普通后台策略，反作弊专项没有新增 GPU 降级目标或无限维护资格。
                if (kind == 5 && ((entry.Reasons & SuppressReason.Background) == 0 || DesiredGpu(entry) < 0 || DesiredGpu(entry) == entry.OrigGpu)) continue;
                long desired = AntiCheatResourceTarget(entry, kind), observed;
                AntiCheatResourceField field = null;
                foreach (AntiCheatResourceField candidate in state.Fields)
                    if (candidate.Kind == kind && candidate.Plan == plan) { field = candidate; break; }
                if (field == null)
                {
                    if (state.Fields.Count >= 256) { complete = false; LastApplyError = "resource-journal-capacity"; continue; }
                    field = new AntiCheatResourceField { Plan = plan, Kind = kind, Original = -1 };
                    state.Fields.Add(field);
                }
                if (field.Phase == 'S') { field.Phase = 'N'; field.Acquired = false; field.Original = -1; }
                bool maintaining = field.Acquired && kind != 5;
                if (!maintaining && field.Failures >= 3) { complete = false; LastApplyError = "initial-resource-budget:" + kind; continue; }
                if (!ReadAntiCheatResource(handle, kind, out observed) || desired < 0)
                {
                    if (!maintaining) field.Failures = Math.Min(3, field.Failures + 1);
                    if (!SaveAntiCheatResourceJournal(entry)) LastApplyError = "resource-read-budget-journal";
                    complete = false; LastApplyError = "resource-read-unknown:" + kind; continue;
                }
                if (field.Original < 0 || field.Phase == 'N') field.Original = observed;
                if (kind == 3) desired = MergeAntiCheatQos(observed, desired);
                if (field.Acquired && AntiCheatResourceMatches(kind, observed, desired)) continue;
                char previousPhase = field.Phase;
                long previousExpected = field.Expected, previousBefore = field.Previous;
                field.Previous = observed; field.Expected = desired; field.Phase = 'P';
                if (!maintaining) field.Failures++;
                if (!SaveAntiCheatResourceJournal(entry))
                {
                    field.Phase = previousPhase; field.Expected = previousExpected; field.Previous = previousBefore;
                    SaveAntiCheatResourceJournal(entry); complete = false; LastApplyError = "resource-intent-journal:" + kind; continue;
                }
                long fresh;
                if (!AntiCheatAdmissionCurrent(handle, entry, epoch) || !ReadAntiCheatResource(handle, kind, out fresh) || fresh != observed)
                {
                    field.Phase = previousPhase; field.Expected = previousExpected; field.Previous = previousBefore; SaveAntiCheatResourceJournal(entry);
                    complete = false; LastApplyError = "resource-prewrite-drift-or-revoked:" + kind; continue;
                }
                if (!AntiCheatAdmissionCurrent(handle, entry, epoch))
                {
                    field.Phase = previousPhase; field.Expected = previousExpected; field.Previous = previousBefore; SaveAntiCheatResourceJournal(entry);
                    complete = false; LastApplyError = "resource-forward-revoked:" + kind; continue;
                }
                bool wrote = WriteAntiCheatResource(handle, kind, desired, true);
                int error = wrote ? 0 : Marshal.GetLastWin32Error();
                if (kind == 3 && !wrote && error == 87 && DesiredTimerSeal(entry.Level, true))
                { state.TimerSealUnavailable = true; SaveAntiCheatResourceJournal(entry); }
                long after;
                if (!wrote)
                { complete = false; LastApplyError = "resource-api-or-readback:" + kind + ":" + error + " 阶段=写入调用未成功"; continue; }
                if (!ReadAntiCheatResource(handle, kind, out after))
                { complete = false; LastApplyError = "resource-api-or-readback:" + kind + ":0 阶段=写后读取失败；写入调用已返回成功，尚未确认生效"; continue; }
                if (!AntiCheatResourceMatches(kind, after, desired))
                { complete = false; LastApplyError = "resource-api-or-readback:" + kind + ":0 阶段=写后值不匹配 目标=0x" + desired.ToString("X") + " 读回=0x" + after.ToString("X"); continue; }
                if (EntryIdentity(handle, entry) != 0)
                { complete = false; LastApplyError = "resource-api-or-readback:" + kind + ":0 阶段=写后目标身份未确认"; continue; }
                field.Expected = after; field.Phase = 'A';
                bool acquiredBefore = field.Acquired;
                field.Acquired = true;
                if (!SaveAntiCheatResourceJournal(entry)) { field.Acquired = acquiredBefore; field.Phase = 'P'; complete = false; LastApplyError = "resource-commit-journal:" + kind; continue; }
                // 只归还本次成功的预扣；此前首次失败不能因成功观察或配置往返清零。
                if (!maintaining) { field.Failures--; SaveAntiCheatResourceJournal(entry); }
                else corrected.Add(kind == 0 ? "CPU优先级" : kind == 1 ? "I/O优先级" : kind == 2 ? "内存页优先级" : kind == 3 ? "EcoQoS与计时器策略" : "动态提优策略");
            }
            if (corrected.Count > 0 && DateTime.UtcNow.Ticks >= state.NextCorrectionLog)
            {
                state.NextCorrectionLog = DateTime.UtcNow.AddSeconds(30).Ticks;
                Logger.Log("反作弊资源回写已纠正并读回：目标=" + entry.Name + "/" + entry.Creation + " 字段=" + string.Join("、", corrected.ToArray())
                    + (complete ? "；整档已核验" : "；其余字段仍待核验"));
            }
            if (!complete) return PauseAntiCheatResources(entry, LastApplyError);
            state.MaintenanceFailures = 0;
            entry.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatAuditSeconds(entry.Level)).Ticks;
            return true;
        }

        private bool RestoreOwnedAntiCheatResources(IntPtr handle, Entry entry)
        {
            lock (sync)
            {
                if (HasAntiCheatResources(entry))
                    foreach (AntiCheatResourceField field in entry.AntiCheatResources.Fields)
                        if ((field.Phase == 'P' || field.Phase == 'A') && field.RecoveryFailures >= 8) return false;
                bool restored = RestoreAntiCheatResources(handle, entry, () => SaveAntiCheatResourceJournal(entry));
                return SaveAntiCheatResourceJournal(entry) && restored;
            }
        }

        private bool RestoreRecordedAntiCheatResources(IntPtr handle, Entry entry)
        {
            if (OwnsAntiCheatResources(entry)) return RestoreOwnedAntiCheatResources(handle, entry);
            lock (sync)
            {
                bool restored = RestoreLegacyAntiCheatResources(handle, entry, () => SaveAntiCheatResourceJournal(entry));
                return SaveAntiCheatResourceJournal(entry) && restored;
            }
        }

        private static bool RestoreLegacyAntiCheatResources(IntPtr handle, Entry entry, Func<bool> checkpoint)
        {
            AntiCheatResourceState state = entry.AntiCheatResources;
            if (state.LegacyRecoveryFailures >= 8) return false;
            state.LegacyRecoveryFailures++;
            if (checkpoint != null && !checkpoint()) return false;
            if (IdentifyJournalEntry(handle, entry) != JournalIdentity.Confirmed) return false;
            bool restored = RestoreOrdinaryEntryValues(handle, entry, CpuTopology.AllMask, checkpoint ?? (() => true));
            if (restored) state.LegacyRecoveryFailures--;
            return restored;
        }

        private static bool RestoreAntiCheatResources(IntPtr handle, Entry entry, Func<bool> checkpoint = null)
        {
            if (!OwnsAntiCheatResources(entry)) return true;
            foreach (AntiCheatResourceField field in entry.AntiCheatResources.Fields)
            {
                if (field.Phase == 'S' || field.Phase == 'N') continue;
                if (field.RecoveryFailures >= 8) return false;
                // 恢复也先持久预扣，进程在原生调用中终止不会把同一旧计划的预算补满。
                field.RecoveryFailures++;
                if (checkpoint != null && !checkpoint()) return false;
                if (Native.QueryProcessIdentityState(handle, entry.Creation) != 0) return false;
                long current;
                if (!ReadAntiCheatResource(handle, field.Kind, out current)) return false;
                if (field.Kind == 3)
                {
                    long target = current;
                    foreach (int bit in new[] { 1, 4 })
                    {
                        long pair = QosValue(bit, bit);
                        if (((current ^ field.Expected) & pair) == 0) target = (target & ~pair) | (field.Original & pair);
                    }
                    if (target != current && (!WriteAntiCheatResource(handle, 3, target, false)
                        || !ReadAntiCheatResource(handle, 3, out current) || !AntiCheatResourceMatches(3, current, target))) return false;
                    field.RecoveryFailures--; field.Phase = 'S'; field.Acquired = false; continue;
                }
                // 关闭后只恢复本方末次写入；外部在最后一次纠偏之后留下的新值照原样保留。
                if (current != field.Expected || current == field.Original)
                { field.RecoveryFailures--; field.Phase = 'S'; field.Acquired = false; continue; }
                if (field.Original < 0 || !WriteAntiCheatResource(handle, field.Kind, field.Original, false)
                    || !ReadAntiCheatResource(handle, field.Kind, out current) || current != field.Original) return false;
                field.RecoveryFailures--; field.Phase = 'S'; field.Acquired = false;
            }
            return true;
        }

        private static string EncodeAntiCheatResources(AntiCheatResourceState state)
        {
            if (state == null) return "";
            var text = new StringBuilder("ar1;");
            text.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(state.Plan))).Append(';').Append(state.Active ? '1' : '0').Append(';').Append(state.TimerSealUnavailable ? '1' : '0');
            text.Append(";r:").Append(state.LegacyRecoveryFailures);
            foreach (AntiCheatResourceField field in state.Fields)
                text.Append(';').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(field.Plan))).Append(',').Append(field.Kind)
                    .Append(',').Append(field.Original.ToString(CultureInfo.InvariantCulture)).Append(',').Append(field.Expected.ToString(CultureInfo.InvariantCulture))
                    .Append(',').Append(field.Previous.ToString(CultureInfo.InvariantCulture)).Append(',').Append(field.Phase)
                    .Append(',').Append(field.Acquired ? '1' : '0').Append(',').Append(field.Failures).Append(',').Append(field.RecoveryFailures);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text.ToString()));
        }

        private static string UpdatedResourceJournalLine(string original, Entry entry)
        {
            int gpuMarker = original.LastIndexOf("|gpu-v1|", StringComparison.Ordinal);
            if (gpuMarker >= 0) original = original.Substring(0, gpuMarker);
            int marker = original.LastIndexOf("|ac-resources|", StringComparison.Ordinal);
            if (HasAntiCheatResources(entry) && marker >= 0)
                original = original.Substring(0, marker) + "|ac-resources|" + EncodeAntiCheatResources(entry.AntiCheatResources);
            return original + "|gpu-v1|" + entry.GpuLease.Encode();
        }

        private static AntiCheatResourceState ParseAntiCheatResources(string value)
        {
            try
            {
                if (value == null || value.Length > 262144) return null;
                string[] rows = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split(';');
                if (rows.Length < 4 || rows.Length > 261 || rows[0] != "ar1" || (rows[2] != "0" && rows[2] != "1") || (rows[3] != "0" && rows[3] != "1")) return null;
                var state = new AntiCheatResourceState { Plan = Encoding.UTF8.GetString(Convert.FromBase64String(rows[1])), Active = rows[2] == "1", TimerSealUnavailable = rows[3] == "1" };
                int firstField = 4;
                if (rows.Length > 4 && rows[4].StartsWith("r:", StringComparison.Ordinal))
                {
                    if (!int.TryParse(rows[4].Substring(2), NumberStyles.None, CultureInfo.InvariantCulture, out state.LegacyRecoveryFailures) || state.LegacyRecoveryFailures < 0 || state.LegacyRecoveryFailures > 8) return null;
                    firstField++;
                }
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = firstField; i < rows.Length; i++)
                {
                    string[] fields = rows[i].Split(',');
                    if (fields.Length != 9) return null;
                    var field = new AntiCheatResourceField { Plan = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])), Kind = int.Parse(fields[1], CultureInfo.InvariantCulture),
                        Original = long.Parse(fields[2], CultureInfo.InvariantCulture), Expected = long.Parse(fields[3], CultureInfo.InvariantCulture), Previous = long.Parse(fields[4], CultureInfo.InvariantCulture),
                        Phase = fields[5].Length == 1 ? fields[5][0] : '?', Acquired = fields[6] == "1", Failures = int.Parse(fields[7], CultureInfo.InvariantCulture), RecoveryFailures = int.Parse(fields[8], CultureInfo.InvariantCulture) };
                    if (field.Kind < 0 || field.Kind > 5 || field.Failures < 0 || field.Failures > 3 || field.RecoveryFailures < 0 || field.RecoveryFailures > 8 || field.Plan.Length > 128
                        || !keys.Add(field.Plan + "/" + field.Kind) || (field.Phase != 'N' && field.Phase != 'P' && field.Phase != 'A' && field.Phase != 'S')
                        || (fields[6] != "0" && fields[6] != "1") || field.Original < -1 || field.Expected < 0 || field.Previous < 0
                        || (field.Phase != 'N' && field.Original < 0)) return null;
                    state.Fields.Add(field);
                }
                return state;
            }
            catch { return null; }
        }
    }
}
