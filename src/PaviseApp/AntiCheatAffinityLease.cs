using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class AntiCheatAffinityIdentity
    {
        internal readonly int ProcessId;
        internal readonly long CreationTime;
        internal readonly string ImagePath;
        internal AntiCheatAffinityIdentity(int processId, long creationTime, string imagePath)
        { ProcessId = processId; CreationTime = creationTime; ImagePath = imagePath ?? ""; }
        internal string Key { get { return ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + CreationTime.ToString(CultureInfo.InvariantCulture) + ":" + ImagePath.ToUpperInvariant(); } }
    }

    internal enum AntiCheatAffinityRead { Known, Gone, DifferentProcess, Unavailable, Unsupported }
    internal sealed class AntiCheatAffinityObservation
    {
        internal AntiCheatAffinityRead Read;
        internal ushort Group;
        internal ulong Mask, AllowedMask;
        internal string Detail;
        internal bool CpuSetsKnown = true, CpuSetsEmpty = true;
        internal bool PlacementVerified = true;
        internal AntiCheatAffinityObservation(AntiCheatAffinityRead read, ushort group, ulong mask, ulong allowed, string detail)
        { Read = read; Group = group; Mask = mask; AllowedMask = allowed; Detail = detail ?? ""; }
    }

    // 写前复核身份、处理器组和预期亲和性；Windows 无原子比较交换接口，仍可能与外部写入竞争。

    internal interface IAntiCheatAffinityPlatform
    {
        string DeviceIdentity { get; }
        long NowMilliseconds { get; }
        AntiCheatAffinityObservation Read(AntiCheatAffinityIdentity identity);
        bool Write(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation observed, bool allowCpuSets = false);
        void Forget(AntiCheatAffinityIdentity identity);
    }
    internal interface IAntiCheatAffinityStore
    {
        bool Load(out string receipt);
        bool Save(string receipt);
    }
    // 仅恢复本方硬掩码，保留外部 CPU Sets；外部策略不阻断本方恢复。

    internal interface IAntiCheatAffinityRestorePlatform
    {
        bool Restore(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong original, out AntiCheatAffinityObservation observed);
    }
    // 约束的存续周期与进程掩码恢复分别管理。
    internal interface IAntiCheatAffinityConstraintPlatform
    {
        bool ConstraintPending { get; }
        bool RecoverConstraints(bool restoreOwned, out string reason);
        bool ReleaseConstraint(AntiCheatAffinityIdentity identity, out string reason);
        bool ReleaseAllConstraints(out string reason);
        bool StopConstraints(out string reason);
    }
    internal interface IAntiCheatAffinityTargetConstraintPlatform
    {
        bool ConstraintPendingFor(AntiCheatAffinityIdentity identity);
    }
    internal interface IAntiCheatAffinityForwardPlatform
    {
        void RevokeForward(AntiCheatAffinityIdentity identity);
    }
    internal interface IAntiCheatAffinityForwardAdmissionPlatform
    {
        long ForwardEpoch { get; }
        bool WriteOwned(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, long admittedEpoch,
            out AntiCheatAffinityObservation observed, bool allowCpuSets);
    }
    internal sealed class AntiCheatAffinityResult
    {
        internal readonly string Code, Detail;
        internal readonly bool Bound, PendingRecovery;
        internal AntiCheatAffinityResult(string code, string detail, bool bound, bool pending)
        { Code = code; Detail = detail; Bound = bound; PendingRecovery = pending; }
    }

    // 调用方提供已核验的组内掩码；反作弊的逐线程原值由独立 Job 守护持久保存。
    internal sealed class AntiCheatAffinityLease
    {
        internal const string ReceiptKey = "AntiCheatHardAffinityV1";
        private sealed class Receipt
        {
            internal AntiCheatAffinityIdentity Identity;
            internal ushort Group;
            internal ulong Original, Applied, Previous;
            internal char Phase;
            internal Receipt Copy() { return (Receipt)MemberwiseClone(); }
        }
        private sealed class Guard
        {
            internal AntiCheatAffinityIdentity Identity;
            internal long NextRead;
            internal bool AllowCpuSets;
            internal bool Owned, Revoked, EverOwned, TargetRecovery;
            internal int MaintenanceFailures;
            internal long NextLog;
            internal AntiCheatAffinityResult Last;
        }
        private sealed class SettingsStore : IAntiCheatAffinityStore
        {
            public bool Load(out string receipt) { return Settings.TryLoadStr(ReceiptKey, out receipt); }
            public bool Save(string receipt)
            {
                string read;
                return Settings.SaveStrDurable(ReceiptKey, receipt) && Settings.TryLoadStr(ReceiptKey, out read) && read == receipt;
            }
        }
        private readonly IAntiCheatAffinityPlatform platform;
        private readonly IAntiCheatAffinityStore store;
        private readonly object sync = new object();
        private readonly Dictionary<string, Receipt> rows = new Dictionary<string, Receipt>(StringComparer.Ordinal);
        private readonly Dictionary<string, Guard> guards = new Dictionary<string, Guard>(StringComparer.Ordinal);
        private bool loaded, corrupt, recoveryPending;
        private string device;
        private sealed class RecoveryAttempt { internal int Failures; internal long Next; }
        private readonly Dictionary<string, RecoveryAttempt> recoveryAttempts = new Dictionary<string, RecoveryAttempt>(StringComparer.Ordinal);
        private readonly Dictionary<string, RecoveryAttempt> acquisitionAttempts = new Dictionary<string, RecoveryAttempt>(StringComparer.Ordinal);
        private long recoverySession;
        private int loadFailures;
        private long loadNext;

        internal void BeginRecoverySession(long serial)
        {
            lock (sync)
            {
                if (serial <= recoverySession) return;
                recoverySession = serial;
                loadFailures = 0; loadNext = 0;
            }
        }

        internal AntiCheatAffinityLease() : this(new NativePlatform(), new SettingsStore()) { }
        internal AntiCheatAffinityLease(IAntiCheatAffinityPlatform native, IAntiCheatAffinityStore durable)
        { if (native == null || durable == null) throw new ArgumentNullException(); platform = native; store = durable; }
        internal static bool TryIdentify(int pid, long creation, string expectedName, out AntiCheatAffinityIdentity identity, out string reason)
        { return NativePlatform.TryIdentify(pid, creation, expectedName, out identity, out reason); }
        internal static bool TryInspect(AntiCheatAffinityIdentity identity, out ushort group, out ulong affinity, out string reason, bool allowCpuSets = false)
        {
            NativePlatform native = new NativePlatform();
            try
            {
                AntiCheatAffinityObservation read = native.Read(identity);
                group = read.Group; affinity = read.Mask; reason = read.Detail;
                if (read.Read == AntiCheatAffinityRead.Known && (!read.CpuSetsKnown || (!allowCpuSets && !read.CpuSetsEmpty)))
                { reason = read.CpuSetsKnown ? "进程已有 CPU Sets 限制，保留原分配" : "无法核验进程已有核心限制"; return false; }
                return read.Read == AntiCheatAffinityRead.Known;
            }
            finally { native.Forget(identity); }
        }
        internal bool HasOwnership(AntiCheatAffinityIdentity identity)
        { lock (sync) { return identity != null && rows.ContainsKey(identity.Key); } }
        internal bool HasAppliedOwnership(AntiCheatAffinityIdentity identity)
        { lock (sync) { Guard guard; return identity != null && rows.ContainsKey(identity.Key)
            && guards.TryGetValue(identity.Key, out guard) && guard.Owned && !guard.Revoked; } }
        internal void RevokeForward(AntiCheatAffinityIdentity identity = null)
        {
            IAntiCheatAffinityForwardPlatform forward = platform as IAntiCheatAffinityForwardPlatform;
            if (forward != null) forward.RevokeForward(identity);
        }
        internal long ForwardEpoch
        {
            get
            {
                IAntiCheatAffinityForwardAdmissionPlatform admission = platform as IAntiCheatAffinityForwardAdmissionPlatform;
                return admission == null ? 0 : admission.ForwardEpoch;
            }
        }
        internal ulong OriginalAffinity(AntiCheatAffinityIdentity identity)
        { lock (sync) { Receipt row; return identity != null && rows.TryGetValue(identity.Key, out row) ? row.Original : 0; } }
        private IAntiCheatAffinityConstraintPlatform Constraints { get { return platform as IAntiCheatAffinityConstraintPlatform; } }
        internal bool PendingRecovery { get { lock (sync) return recoveryPending || corrupt || (Constraints != null && Constraints.ConstraintPending); } }
        internal bool PendingRecoveryFor(AntiCheatAffinityIdentity identity)
        {
            lock (sync)
            {
                if (!loaded || corrupt || identity == null) return PendingRecovery;
                IAntiCheatAffinityTargetConstraintPlatform scoped = platform as IAntiCheatAffinityTargetConstraintPlatform;
                if (scoped != null ? scoped.ConstraintPendingFor(identity) : Constraints != null && Constraints.ConstraintPending) return true;
                Receipt row; Guard guard;
                return rows.TryGetValue(identity.Key, out row)
                    && (!guards.TryGetValue(identity.Key, out guard) || !guard.Owned || guard.Revoked || row.Phase == 'R');
            }
        }
        private static AntiCheatAffinityResult Result(string code, string detail, bool bound, bool pending)
        { return new AntiCheatAffinityResult(code, detail, bound, pending); }
        internal static bool IsSGuard(AntiCheatAffinityIdentity identity)
        { return identity != null && string.Equals(Path.GetFileNameWithoutExtension(identity.ImagePath), "SGuard64", StringComparison.OrdinalIgnoreCase); }
        private Guard GuardFor(AntiCheatAffinityIdentity identity)
        {
            Guard guard;
            if (!guards.TryGetValue(identity.Key, out guard)) guards.Add(identity.Key, guard = new Guard { Identity = identity });
            return guard;
        }
        private bool Load()
        {
            if (loaded) return !corrupt;
            if (loadFailures >= 3 || platform.NowMilliseconds < loadNext) return false;
            string receiptValue;
            device = platform.DeviceIdentity;
            if (string.IsNullOrEmpty(device) || !store.Load(out receiptValue))
            { loadFailures++; loadNext = platform.NowMilliseconds + 30000; return false; }
            if (string.IsNullOrEmpty(receiptValue)) { loaded = true; return true; }
            try
            {
                string[] lines = receiptValue.Split('\n');
                if (lines.Length < 2 || lines[0] != "ACHA1|" + device || lines.Length > 1026) throw new FormatException();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    string[] p = lines[i].Split('|');
                    if (p.Length != 8) throw new FormatException();
                    int pid = int.Parse(p[0], CultureInfo.InvariantCulture);
                    long creation = long.Parse(p[1], CultureInfo.InvariantCulture);
                    string path = Encoding.UTF8.GetString(Convert.FromBase64String(p[2]));
                    Receipt row = new Receipt();
                    row.Identity = new AntiCheatAffinityIdentity(pid, creation, path);
                    row.Group = ushort.Parse(p[3], CultureInfo.InvariantCulture);
                    row.Original = ulong.Parse(p[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    row.Applied = ulong.Parse(p[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    row.Previous = ulong.Parse(p[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (p[7].Length != 1) throw new FormatException();
                    row.Phase = p[7][0];
                    if (pid <= 0 || creation <= 0 || !Path.IsPathRooted(path) || row.Original == 0 || row.Applied == 0 || (row.Phase != 'P' && row.Phase != 'A' && row.Phase != 'R')) throw new FormatException();
                    rows.Add(row.Identity.Key, row);
                }
                loaded = true; recoveryPending = rows.Count != 0; return true;
            }
            catch { rows.Clear(); corrupt = true; loaded = true; recoveryPending = true; return false; }
        }
        private bool Save()
        {
            StringBuilder stringBuilder = new StringBuilder();
            if (rows.Count != 0)
            {
                stringBuilder.Append("ACHA1|").Append(device).Append('\n');
                List<string> keys = new List<string>(rows.Keys); keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    Receipt row = rows[key];
                    stringBuilder.Append(row.Identity.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Identity.CreationTime.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(row.Identity.ImagePath))).Append('|')
                        .Append(row.Group.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Original.ToString("X", CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Applied.ToString("X", CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Previous.ToString("X", CultureInfo.InvariantCulture)).Append('|').Append(row.Phase).Append('\n');
                }
            }
            return store.Save(stringBuilder.ToString());
        }
        private bool Remove(Receipt row)
        {
            string reason;
            if (Constraints != null && !Constraints.ReleaseConstraint(row.Identity, out reason)) return Debt(row);
            rows.Remove(row.Identity.Key);
            if (!Save()) { rows[row.Identity.Key] = row; return Debt(row); }
            RefreshRecoveryPending(); platform.Forget(row.Identity); return true;
        }
        private void RefreshRecoveryPending()
        {
            recoveryPending = false;
            foreach (Receipt row in rows.Values)
            {
                Guard guard;
                if (!guards.TryGetValue(row.Identity.Key, out guard) || !guard.Owned || guard.Revoked || row.Phase == 'R')
                { recoveryPending = true; break; }
            }
        }
        private bool HasOtherLiveOwnership(AntiCheatAffinityIdentity identity)
        {
            foreach (Receipt row in rows.Values)
            {
                Guard guard;
                if (row.Identity.Key != identity.Key && guards.TryGetValue(row.Identity.Key, out guard)
                    && guard.Owned && !guard.Revoked && !PendingRecoveryFor(row.Identity)) return true;
            }
            return false;
        }
        private bool Debt(Receipt row)
        { recoveryPending = true; return false; }
        internal bool Recover()
        {
            lock (sync)
            {
                if (!Load()) { recoveryPending = true; return false; }
                string reason;
                if (Constraints != null && Constraints.ConstraintPending) recoveryPending = true;
                if (Constraints != null && !Constraints.RecoverConstraints(recoveryPending, out reason))
                { recoveryPending = true; return false; }
                if (!recoveryPending) return true;
                bool ok = true;
                foreach (Receipt row in new List<Receipt>(rows.Values)) ok = Restore(row) && ok;
                recoveryPending = !ok || rows.Count != 0;
                return !recoveryPending;
            }
        }
        private bool Restore(Receipt row)
        {
            string key = recoverySession + "|" + row.Identity.Key + "|" + row.Original + "|" + row.Applied;
            RecoveryAttempt attempt;
            if (!recoveryAttempts.TryGetValue(key, out attempt)) recoveryAttempts[key] = attempt = new RecoveryAttempt();
            if (attempt.Failures >= 3 || platform.NowMilliseconds < attempt.Next) return Debt(row);
            bool restored = false;
            try { restored = RestoreCore(row); return restored; }
            finally
            {
                if (restored)
                {
                    attempt.Next = 0;
                }
                else
                {
                    attempt.Failures++;
                    attempt.Next = platform.NowMilliseconds + 30000;
                    Logger.Warn("反作弊亲和性恢复：目标=" + row.Identity.Key + " 失败详情=" + attempt.Failures + "/3；保留回执和句柄");
                }
            }
        }
        private bool RestoreCore(Receipt row)
        {
            // 守护丢失时，普通读取按失败处理。先撤销 Job，其独立持久记录也负责恢复原进程掩码。

            string reason;
            if (Constraints != null && !Constraints.ReleaseConstraint(row.Identity, out reason)) return Debt(row);
            AntiCheatAffinityObservation read = platform.Read(row.Identity);
            if (read.Read == AntiCheatAffinityRead.Gone || read.Read == AntiCheatAffinityRead.DifferentProcess)
            {
                bool removed = Remove(row);
                if (removed) Logger.Info("反作弊亲和性恢复记录已结清：原目标已退出或身份变化，未记为原值读回；目标=" + row.Identity.Key);
                return removed;
            }
            if (read.Read != AntiCheatAffinityRead.Known || read.Group != row.Group) return Debt(row);
            // 若进程在 Set 前崩溃，准备中的目标变更可能仍停留在上一次掩码。
            bool owned = read.Mask == row.Applied || ((row.Phase == 'P' || row.Phase == 'R') && row.Previous != 0 && read.Mask == row.Previous);
            if (!owned || read.Mask == row.Original)
            {
                bool removed = Remove(row);
                if (removed) Logger.Info("反作弊亲和性恢复记录已结清：目标=" + row.Identity.Key
                    + (read.Mask == row.Original ? " 原进程范围已读回，逐线程恢复已结清" : " 保留关闭后的外部范围，逐线程恢复已结清"));
                return removed;
            }
            if ((row.Original & ~read.AllowedMask) != 0) return Debt(row);
            Receipt backup = row.Copy(); row.Previous = read.Mask; row.Phase = 'R';
            if (!Save()) { rows[row.Identity.Key] = backup; return Debt(backup); }
            AntiCheatAffinityObservation after;
            IAntiCheatAffinityRestorePlatform restorer = platform as IAntiCheatAffinityRestorePlatform;
            if (restorer != null) restorer.Restore(row.Identity, row.Group, read.Mask, row.Original, out after);
            else platform.Write(row.Identity, row.Group, read.Mask, row.Original, out after);
            if (after != null && (after.Read == AntiCheatAffinityRead.Gone || after.Read == AntiCheatAffinityRead.DifferentProcess)) return Remove(row);
            if (after != null && after.Read == AntiCheatAffinityRead.Known && after.Group == row.Group)
            {
                if (after.Mask == row.Original || (after.Mask != row.Applied && after.Mask != row.Previous)) return Remove(row);
            }
            // 权限失败或写入未经核验时，保留持久恢复意图。
            return Debt(row);
        }
        internal AntiCheatAffinityResult Ensure(AntiCheatAffinityIdentity identity, ushort group, ulong targetMask, bool allowCpuSets = false, long? expectedEpoch = null)
        {
            // 准入代号在等锁、读值和保存日志前冻结，停用后的新代号不能被旧周期借用。
            long admittedEpoch = expectedEpoch ?? ForwardEpoch;
            lock (sync)
            {
                if (identity == null || identity.ProcessId <= 0 || identity.CreationTime <= 0 || targetMask == 0)
                    return Result("invalid-target", "进程身份或目标核心无效", false, false);
                return EnsureContinuous(identity, group, targetMask, allowCpuSets, admittedEpoch);
            }
        }
        private AntiCheatAffinityResult PauseMaintenance(Guard guard, string detail)
        {
            guard.MaintenanceFailures = Math.Min(6, guard.MaintenanceFailures + 1);
            long delay = Math.Min(30000, 1000L << (guard.MaintenanceFailures - 1));
            guard.NextRead = platform.NowMilliseconds + delay;
            if (platform.NowMilliseconds >= guard.NextLog)
            {
                Logger.Warn("反作弊亲和性维护暂停：目标=" + guard.Identity.Key + " 阶段=" + detail
                    + "；保留原目标和恢复归属，退避=" + delay + "ms");
                guard.NextLog = platform.NowMilliseconds + 30000;
            }
            return guard.Last = Result("maintenance-paused", detail, false, false);
        }

        // 只有已完成首次接管的反作弊字段允许持续维护；首次失败预算独立保存，不由观察成功或配置往返补充。
        private AntiCheatAffinityResult EnsureContinuous(AntiCheatAffinityIdentity identity, ushort group, ulong targetMask, bool allowCpuSets, long admittedEpoch)
        {
            if (!Load()) return Result("recovery-pending", "硬绑定恢复未完成", false, true);
            Guard guard = GuardFor(identity);
            Receipt row;
            bool acquired = rows.TryGetValue(identity.Key, out row);
            bool sameScope = acquired && row.Group == group && row.Applied == targetMask && guard.AllowCpuSets == allowCpuSets;
            if (sameScope && guard.EverOwned && (guard.TargetRecovery || PendingRecoveryFor(identity)))
            {
                // 守护失联仅恢复本目标；失败后保留阶段，避免全局恢复撤销其他组的归属。
                guard.TargetRecovery = true;
                bool restored = Release(identity);
                return Result(restored ? "maintenance-paused" : "recovery-pending", restored
                    ? "本目标恢复已结清；等待原计划再次核验" : "本目标恢复待核验；保留原恢复预算", false, !restored);
            }
            // 别组的恢复债务不能撤销仍有效的当前归属；首次接管、目标变化及来源未知的恢复记录仍先走全局恢复链。
            bool maintainingScope = sameScope && guard.Owned && !guard.Revoked;
            if (PendingRecovery && !maintainingScope)
            {
                if (HasOtherLiveOwnership(identity))
                    return Result("recovery-pending", "已有目标继续维护；新接管等待旧恢复债务结清", false, true);
                if (!Recover()) return Result("recovery-pending", "硬绑定恢复未完成", false, true);
                acquired = rows.TryGetValue(identity.Key, out row);
            }
            if (acquired && (row.Group != group || row.Applied != targetMask || guard.AllowCpuSets != allowCpuSets))
            {
                guard.Revoked = true; guard.Owned = false;
                if (!Restore(row)) return guard.Last = Result("recovery-pending", "旧目标写入资格已撤销，恢复待核验", false, true);
                acquired = false; row = null;
            }
            guard.Revoked = false;
            long now = platform.NowMilliseconds;
            if (acquired && guard.Owned && now < guard.NextRead)
                return guard.Last ?? Result("maintenance-paused", "等待下一次核验", false, false);
            string key = recoverySession + "|" + identity.Key + "|" + group + "|" + targetMask + "|" + allowCpuSets;
            RecoveryAttempt attempt;
            if (!acquisitionAttempts.TryGetValue(key, out attempt)) acquisitionAttempts[key] = attempt = new RecoveryAttempt();
            if (!acquired && (attempt.Failures >= 3 || now < attempt.Next))
                return guard.Last = Result(attempt.Failures >= 3 ? "acquisition-exhausted" : "acquisition-paused",
                    "首次接管预算=" + attempt.Failures + "/3；保留本阶段记录", false, false);
            guard.AllowCpuSets = allowCpuSets;
            guard.NextRead = now + 1000;
            AntiCheatAffinityObservation read = platform.Read(identity);
            if (read.Read == AntiCheatAffinityRead.Gone || read.Read == AntiCheatAffinityRead.DifferentProcess)
            {
                guard.Revoked = true; guard.Owned = false;
                bool cleared = !acquired || Remove(row);
                platform.Forget(identity);
                return guard.Last = Result("gone", "原目标已退出或身份已变化；未记为原值读回", false, !cleared);
            }
            string invalid = read.Read != AntiCheatAffinityRead.Known ? read.Detail
                : !read.CpuSetsKnown ? "进程 CPU Sets 读取未知"
                : (!allowCpuSets && !read.CpuSetsEmpty) ? "进程已有 CPU Sets 限制"
                : read.Group != group || (targetMask & ~read.AllowedMask) != 0 ? "目标处理器组或合法范围待核验" : null;
            if (invalid != null)
            {
                if (acquired && guard.Owned) return PauseMaintenance(guard, invalid);
                attempt.Failures++; attempt.Next = now + 30000;
                return guard.Last = Result("acquisition-paused", "首次准入未通过：" + invalid, false, false);
            }
            if (acquired && guard.Owned && read.Mask == targetMask && read.PlacementVerified)
            {
                guard.MaintenanceFailures = 0;
                return guard.Last = Result("bound", read.Detail.Length == 0 ? "反作弊亲和性已核验" : read.Detail, true, false);
            }
            if (!acquired && read.Mask == targetMask && read.PlacementVerified)
                return guard.Last = Result("already-matched", "进程已有相同硬亲和性，未接管", false, false);
            bool maintaining = acquired && guard.Owned;
            Receipt prior = acquired ? row.Copy() : null;
            if (!acquired)
            {
                row = new Receipt { Identity = identity, Group = group, Original = read.Mask };
                rows.Add(identity.Key, row);
            }
            // 运行期漂移不改变首次原值；准备阶段的外部前值也不能被当作本方末次写入。
            row.Previous = 0; row.Applied = targetMask; row.Phase = 'P';
            if (!Save())
            {
                if (prior == null) rows.Remove(identity.Key); else rows[identity.Key] = prior;
                if (maintaining) return PauseMaintenance(guard, "纠偏日志持久化失败，尚未写入");
                attempt.Failures++; attempt.Next = now + 30000;
                return guard.Last = Result("acquisition-paused", "首次恢复日志持久化失败，尚未写入", false, false);
            }
            AntiCheatAffinityObservation after;
            IAntiCheatAffinityForwardAdmissionPlatform admission = platform as IAntiCheatAffinityForwardAdmissionPlatform;
            bool wrote = admission == null ? platform.Write(identity, group, read.Mask, targetMask, out after, allowCpuSets)
                : admission.WriteOwned(identity, group, read.Mask, targetMask, admittedEpoch, out after, allowCpuSets);
            if (wrote && after != null && after.Read == AntiCheatAffinityRead.Known && after.Group == group
                && after.Mask == targetMask && after.PlacementVerified)
            {
                row.Phase = 'A';
                if (Save())
                {
                    guard.Owned = true; guard.EverOwned = true; guard.MaintenanceFailures = 0; guard.NextRead = now + 1000;
                    if (maintaining && now >= guard.NextLog)
                    {
                        Logger.Info("反作弊亲和性读回漂移已纠正：目标=" + identity.Key + " 掩码=0x" + targetMask.ToString("X", CultureInfo.InvariantCulture)
                            + " 纠正前掩码=0x" + read.Mask.ToString("X", CultureInfo.InvariantCulture) + " 核验详情=" + read.Detail);
                        guard.NextLog = now + 30000;
                    }
                    return guard.Last = Result(maintaining ? "reasserted" : "bound", after.Detail.Length == 0
                        ? maintaining ? "检测到回写，原目标已重新核验" : "反作弊亲和性接管已核验" : after.Detail, true, false);
                }
            }
            string failure = after == null || after.Detail.Length == 0 ? "写入或读回未核验" : after.Detail;
            if (maintaining) return PauseMaintenance(guard, failure);
            attempt.Failures++; attempt.Next = now + 30000;
            guard.Revoked = true; guard.Owned = false; Debt(row);
            return guard.Last = Result("write-unverified", "首次接管未核验：" + failure + "；保留恢复日志", false, true);
        }

        internal bool Release(AntiCheatAffinityIdentity identity)
        {
            RevokeForward(identity);
            lock (sync)
            {
                if (!Load()) { recoveryPending = true; return false; }
                Receipt row;
                if (identity == null) return true;
                Guard revoked;
                if (guards.TryGetValue(identity.Key, out revoked)) { revoked.Revoked = true; revoked.Owned = false; }
                if (!rows.TryGetValue(identity.Key, out row))
                {
                    string reason;
                    if (Constraints != null && !Constraints.ReleaseConstraint(identity, out reason)) { recoveryPending = true; return false; }
                    platform.Forget(identity); guards.Remove(identity.Key);
                    RefreshRecoveryPending();
                    return true;
                }
                bool ok = Restore(row);
                if (!ok) recoveryPending = true;
                else
                {
                    guards.Remove(identity.Key);
                    RefreshRecoveryPending();
                }
                return ok;
            }
        }
        internal bool ReleaseAll()
        {
            RevokeForward();
            lock (sync)
            {
                if (!Load()) { recoveryPending = true; return false; }
                foreach (Guard revoked in guards.Values) { revoked.Revoked = true; revoked.Owned = false; }
                bool ok = true;
                foreach (Receipt row in new List<Receipt>(rows.Values)) ok = Restore(row) && ok;
                string reason;
                if (Constraints != null) ok = Constraints.ReleaseAllConstraints(out reason) && ok;
                recoveryPending = !ok || rows.Count != 0;
                if (!recoveryPending)
                {
                    foreach (Guard guard in guards.Values) platform.Forget(guard.Identity);
                    guards.Clear();
                }
                return !recoveryPending;
            }
        }

        // 会话切换时保留中性的 Job 供复用，只在真正关闭程序时释放。
        internal bool Stop()
        {
            lock (sync)
            {
                bool restored = ReleaseAll();
                string reason;
                bool stopped = Constraints == null || Constraints.StopConstraints(out reason);
                if (!restored || !stopped) recoveryPending = true;
                return restored && stopped;
            }
        }

        private sealed class NativePlatform : IAntiCheatAffinityPlatform, IAntiCheatAffinityRestorePlatform, IAntiCheatAffinityConstraintPlatform, IAntiCheatAffinityTargetConstraintPlatform, IAntiCheatAffinityForwardPlatform, IAntiCheatAffinityForwardAdmissionPlatform
        {
            private sealed class Backend
            {
                internal ulong Original, Desired;
                internal bool Job;
                internal string Detail;
            }
            private readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            private readonly Dictionary<string, Backend> backends = new Dictionary<string, Backend>(StringComparer.Ordinal);
            private readonly GameAffinityJobLease jobs = new GameAffinityJobLease();
            private readonly Stopwatch timer = Stopwatch.StartNew();
            private bool recovered;
            private long forwardEpoch;
            private string device;
            [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder text, ref int capacity);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessGroupAffinity(IntPtr handle, ref ushort count, [Out] ushort[] groups);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessAffinityMask(IntPtr handle, out UIntPtr process, out UIntPtr system);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessAffinityMask(IntPtr handle, UIntPtr mask);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessDefaultCpuSets(IntPtr handle, [Out] uint[] ids, uint capacity, out uint required);
            [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
            public long NowMilliseconds { get { return timer.ElapsedMilliseconds; } }
            public string DeviceIdentity
            {
                get
                {
                    if (device != null) return device;
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (RegistryKey key = root.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography"))
                        { Guid id; return device = key != null && Guid.TryParse(key.GetValue("MachineGuid") as string, out id) && id != Guid.Empty ? id.ToString("N") : ""; }
                    }
                    catch { return ""; }
                }
            }
            private static AntiCheatAffinityObservation Failure(AntiCheatAffinityRead code, string detail)
            { return new AntiCheatAffinityObservation(code, 0, 0, 0, detail); }
            private static AntiCheatAffinityRead Identity(IntPtr handle, AntiCheatAffinityIdentity expected, out string path)
            {
                path = ""; uint code;
                if (!GetExitCodeProcess(handle, out code)) return AntiCheatAffinityRead.Unavailable;
                if (code != 259) return AntiCheatAffinityRead.Gone;
                long born, exit, kernel, user;
                if (!GetProcessTimes(handle, out born, out exit, out kernel, out user)) return AntiCheatAffinityRead.Unavailable;
                if (exit != 0) return AntiCheatAffinityRead.Gone;
                if (born != expected.CreationTime) return AntiCheatAffinityRead.DifferentProcess;
                StringBuilder image = new StringBuilder(32768); int length = image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref length)) return AntiCheatAffinityRead.Unavailable;
                try { path = Path.GetFullPath(image.ToString()); }
                catch { return AntiCheatAffinityRead.Unavailable; }
                return expected.ImagePath.Length == 0 || string.Equals(path, expected.ImagePath, StringComparison.OrdinalIgnoreCase)
                    ? AntiCheatAffinityRead.Known : AntiCheatAffinityRead.DifferentProcess;
            }
            internal static bool TryIdentify(int pid, long creation, string expectedName, out AntiCheatAffinityIdentity identity, out string reason)
            {
                identity = null; reason = "无法核验反作弊进程身份";
                if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(expectedName)) return false;
                IntPtr handle = OpenProcess(0x1000, false, pid);
                if (handle == IntPtr.Zero) return false;
                try
                {
                    string path;
                    AntiCheatAffinityIdentity sample = new AntiCheatAffinityIdentity(pid, creation, "");
                    if (Identity(handle, sample, out path) != AntiCheatAffinityRead.Known) return false;
                    if (!string.Equals(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(expectedName), StringComparison.OrdinalIgnoreCase)) return false;
                    identity = new AntiCheatAffinityIdentity(pid, creation, path); reason = ""; return true;
                }
                finally { CloseHandle(handle); }
            }
            private static AntiCheatAffinityObservation ReadHandle(IntPtr handle, AntiCheatAffinityIdentity identity)
            {
                string path;
                AntiCheatAffinityRead state = Identity(handle, identity, out path);
                if (state != AntiCheatAffinityRead.Known) return Failure(state, "进程身份失效或无法读取");
                try
                {
                    ushort count = 64; ushort[] groups = new ushort[count];
                    if (!GetProcessGroupAffinity(handle, ref count, groups)) return Failure(AntiCheatAffinityRead.Unavailable, "无法读取进程处理器组");
                    if (count != 1) return Failure(AntiCheatAffinityRead.Unsupported, "进程跨多个处理器组，保留原分配");
                    UIntPtr affinity, system;
                    if (!GetProcessAffinityMask(handle, out affinity, out system) || affinity == UIntPtr.Zero || system == UIntPtr.Zero)
                        return Failure(AntiCheatAffinityRead.Unavailable, "无法读取单组硬亲和性");
                    AntiCheatAffinityObservation result = new AntiCheatAffinityObservation(AntiCheatAffinityRead.Known, groups[0], affinity.ToUInt64(), system.ToUInt64(), "");
                    // 读取失败仅阻止新准入，本方硬掩码仍按归属恢复。
                    try
                    {
                        uint required;
                        bool setsRead = GetProcessDefaultCpuSets(handle, null, 0, out required);
                        result.CpuSetsKnown = setsRead || (Marshal.GetLastWin32Error() == 122 && required > 0);
                        result.CpuSetsEmpty = setsRead && required == 0;
                    }
                    catch (EntryPointNotFoundException) { result.CpuSetsKnown = result.CpuSetsEmpty = false; }
                    return result;
                }
                catch (EntryPointNotFoundException) { return Failure(AntiCheatAffinityRead.Unsupported, "系统缺少处理器组核验接口"); }
                catch { return Failure(AntiCheatAffinityRead.Unavailable, "无法核验硬亲和性"); }
            }
            public AntiCheatAffinityObservation Read(AntiCheatAffinityIdentity identity)
            {
                if (identity == null) return Failure(AntiCheatAffinityRead.Unavailable, "进程身份为空");
                IntPtr handle;
                if (!handles.TryGetValue(identity.Key, out handle))
                {
                    handle = OpenProcess(0x1400, false, identity.ProcessId);
                    if (handle == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        return Failure(error == 87 ? AntiCheatAffinityRead.Gone : AntiCheatAffinityRead.Unavailable, "OpenProcess 读取失败：" + error);
                    }
                    handles.Add(identity.Key, handle);
                }
                AntiCheatAffinityObservation read = ReadHandle(handle, identity);
                if (read.Read == AntiCheatAffinityRead.Gone || read.Read == AntiCheatAffinityRead.DifferentProcess) Forget(identity);
                Backend backend;
                if (read.Read == AntiCheatAffinityRead.Known && IsSGuard(identity) && !backends.ContainsKey(identity.Key))
                    read.PlacementVerified = false;
                if (read.Read == AntiCheatAffinityRead.Known && backends.TryGetValue(identity.Key, out backend))
                {
                    string reason;
                    if (backend.Job && !jobs.Verify(identity.ProcessId, identity.CreationTime, backend.Desired, out reason))
                    {
                        // 未取得核验锁时结果保持未知，不能借后续快照补判漂移。
                        if (reason == "job-operation-pending")
                            return Failure(AntiCheatAffinityRead.Unavailable, "反作弊线程放置正在核验：" + reason);
                        if (jobs.HasPendingFor(identity.ProcessId, identity.CreationTime))
                            return Failure(AntiCheatAffinityRead.Unavailable, "Job 硬绑定待恢复：" + reason);
                        read.PlacementVerified = false;
                        read.Detail = "反作弊线程放置待核验：" + reason;
                        return read;
                    }
                    read.Detail = backend.Detail;
                }
                return read;
            }
            public bool Write(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation observed, bool allowCpuSets = false)
            { return WriteMask(identity, group, expected, target, true, allowCpuSets, out observed, ForwardEpoch); }
            public long ForwardEpoch { get { return System.Threading.Interlocked.Read(ref forwardEpoch); } }
            public bool WriteOwned(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, long admittedEpoch,
                out AntiCheatAffinityObservation observed, bool allowCpuSets)
            { return WriteMask(identity, group, expected, target, true, allowCpuSets, out observed, admittedEpoch); }
            public bool Restore(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation observed)
            {
                string reason;
                if (!ReleaseConstraint(identity, out reason)) { observed = Failure(AntiCheatAffinityRead.Unavailable, "Job 恢复未完成：" + reason); return false; }
                observed = Read(identity);
                if (observed.Read == AntiCheatAffinityRead.Known && observed.Group == group && observed.Mask == target) return true;
                return WriteMask(identity, group, expected, target, false, true, out observed, ForwardEpoch);
            }
            private bool WriteMask(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, bool admission, bool allowCpuSets, out AntiCheatAffinityObservation observed, long admittedEpoch)
            {
                observed = null;
                if (admission && admittedEpoch != ForwardEpoch)
                { observed = Failure(AntiCheatAffinityRead.Unavailable, "反作弊旧代写入资格已撤销：阶段=pre-native-write"); return false; }
                IntPtr handle = OpenProcess(0x1600, false, identity.ProcessId);
                if (handle == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    observed = Failure(error == 87 ? AntiCheatAffinityRead.Gone : AntiCheatAffinityRead.Unavailable, "OpenProcess 写入权限打开失败：" + error + "；保留恢复记录"); return false;
                }
                try
                {
                    observed = ReadHandle(handle, identity);
                    if (observed.Read != AntiCheatAffinityRead.Known || observed.Group != group || observed.Mask != expected || target == 0 || (target & ~observed.AllowedMask) != 0) return false;
                    if (admission && (!observed.CpuSetsKnown || (!allowCpuSets && !observed.CpuSetsEmpty)))
                    { observed.Detail = "本方准入暂停：进程 CPU Sets 未通过核验"; return false; }
                    string detail = "";
                    ulong restoreOriginal = expected;
                    if (admission)
                    {
                        Backend prior;
                        bool existingBackend = backends.TryGetValue(identity.Key, out prior);
                        ulong original = existingBackend ? prior.Original : expected;
                        restoreOriginal = original;
                        string reason = "processor-group-unsupported";
                        bool supported = false;
                        try { supported = group == 0 && GetActiveProcessorGroupCount() == 1; }
                        catch (EntryPointNotFoundException) { }
                        jobs.OwnThreadAffinity = true;
                        jobs.StrictThreadAdmission = !IsSGuard(identity);
                        jobs.ForwardAdmission = delegate { return admittedEpoch == System.Threading.Interlocked.Read(ref forwardEpoch); };
                        // 已有进程租约沿用原后端，避免切换 Job 后将本方写入值当作原值。

                        if (supported && (!existingBackend || prior.Job) && jobs.Ensure(identity.ProcessId, identity.CreationTime, Path.GetFileNameWithoutExtension(identity.ImagePath), original, target, out reason))
                        {
                            // 守护仍持有后端；Job 接管被拒时，回执仅报告进程范围限制。
                            backends[identity.Key] = new Backend { Original = original, Desired = target, Job = true,
                                Detail = jobs.IsDirectRange(identity.ProcessId, identity.CreationTime)
                                    ? "进程范围已核验；目标拒绝 Job 接管，线程原值由守护日志恢复" : "Job 硬亲和性已核验" };
                            observed = Read(identity);
                            return observed.Read == AntiCheatAffinityRead.Known && observed.Group == group && observed.Mask == target;
                        }
                        if (GameThreadAffinityAdmission.IsRefusal(reason))
                        { observed = Failure(AntiCheatAffinityRead.Unavailable, "线程分配保护拒绝：" + reason); return false; }
                        // SGuard 的线程原值归属只由专用守护持有；守护准入或 API 失败后不能退回无逐线程日志的进程写入。
                        if (IsSGuard(identity))
                        { observed = Failure(AntiCheatAffinityRead.Unavailable, "SGuard64 专用接管未核验：" + reason); return false; }
                        if ((existingBackend ? jobs.HasPendingFor(identity.ProcessId, identity.CreationTime) : jobs.HasPending)
                            || jobs.IsActive(identity.ProcessId, identity.CreationTime))
                        { observed = Failure(AntiCheatAffinityRead.Unavailable, "Job 硬绑定待恢复：" + reason); return false; }
                        // 退回进程方案前复核原句柄和外部 CPU Sets，防止耗时操作期间基准变化。

                        observed = ReadHandle(handle, identity);
                        if (observed.Read != AntiCheatAffinityRead.Known || observed.Group != group || observed.Mask != expected
                            || (target & ~observed.AllowedMask) != 0 || !observed.CpuSetsKnown || (!allowCpuSets && !observed.CpuSetsEmpty)) return false;
                        string threadReason;
                        if (observed.Mask != target && !GameThreadAffinityAdmission.Check(handle, identity.ProcessId, identity.CreationTime, observed.Mask, out threadReason))
                        { observed = Failure(AntiCheatAffinityRead.Unavailable, "线程分配保护拒绝：" + threadReason); return false; }
                        detail = existingBackend && !prior.Job ? prior.Detail : "进程硬亲和性已核验；Job 未启用（" + reason + "）";
                    }
                    if (!admission && observed.Mask != target)
                    {
                        string threadReason;
                        if (!GameThreadAffinityAdmission.Check(handle, identity.ProcessId, identity.CreationTime, observed.Mask, out threadReason))
                        { observed.Detail = "恢复未确认：保留之后的线程设置，阶段=" + threadReason; return false; }
                    }
                    if (admission && admittedEpoch != ForwardEpoch)
                    { observed.Detail = "反作弊旧代写入资格已撤销：阶段=pre-process-write"; return false; }
                    bool ok = (admission && observed.Mask == target) || SetProcessAffinityMask(handle, new UIntPtr(target));
                    int writeError = ok ? 0 : Marshal.GetLastWin32Error();
                    observed = ReadHandle(handle, identity);
                    if (!ok) observed.Detail = "SetProcessAffinityMask 写入失败：" + writeError;
                    if (admission && ok && observed.Read == AntiCheatAffinityRead.Known && observed.Group == group && observed.Mask == target)
                    {
                        backends[identity.Key] = new Backend { Original = restoreOriginal, Desired = target, Job = false, Detail = detail };
                        observed.Detail = detail;
                    }
                    return ok && observed.Read == AntiCheatAffinityRead.Known && observed.Group == group && observed.Mask == target;
                }
                finally { CloseHandle(handle); }
            }
            public void Forget(AntiCheatAffinityIdentity identity)
            {
                if (identity == null) return;
                IntPtr handle;
                if (handles.TryGetValue(identity.Key, out handle)) { handles.Remove(identity.Key); CloseHandle(handle); }
            }
            public bool ConstraintPending { get { return jobs.HasPending; } }
            public bool ConstraintPendingFor(AntiCheatAffinityIdentity identity)
            { return identity == null ? jobs.HasPending : jobs.HasPendingFor(identity.ProcessId, identity.CreationTime); }
            public void RevokeForward(AntiCheatAffinityIdentity identity)
            {
                System.Threading.Interlocked.Increment(ref forwardEpoch);
                jobs.RevokeForward(identity == null ? 0 : identity.ProcessId, identity == null ? 0 : identity.CreationTime);
            }
            public bool RecoverConstraints(bool restoreOwned, out string reason)
            {
                reason = null;
                if ((restoreOwned || jobs.HasPending) && !ReleaseAllConstraints(out reason)) return false;
                if (recovered) return true;
                recovered = GameAffinityJobLease.Recover(out reason);
                return recovered;
            }
            public bool ReleaseConstraint(AntiCheatAffinityIdentity identity, out string reason)
            {
                reason = null;
                if (identity == null) return true;
                if (!jobs.Release(identity.ProcessId, identity.CreationTime, out reason)) return false;
                backends.Remove(identity.Key);
                return true;
            }
            public bool ReleaseAllConstraints(out string reason)
            {
                if (!jobs.ReleaseAll(out reason)) return false;
                backends.Clear();
                return true;
            }
            public bool StopConstraints(out string reason)
            {
                if (!jobs.Stop(out reason)) return false;
                backends.Clear();
                foreach (IntPtr handle in handles.Values) CloseHandle(handle);
                handles.Clear();
                return true;
            }
        }
    }
}
