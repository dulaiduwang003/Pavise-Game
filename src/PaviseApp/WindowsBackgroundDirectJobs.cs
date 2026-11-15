using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    // 微软后台治理的一次性 Job 后端：入局先持久保存原值与归属，再施加限制并读回一次，之后不再逐轮核验；
    // 退局由主进程按回执恢复并读回。不使用独立守护进程，这些系统服务不会自行解除 Job 限制。
    // 代价：Pavise 异常退出时 Job 名称随最后一个句柄消失，限制会保留到目标进程重启；
    // 回执单独保存，不进入其他 Job 功能共用的遗留恢复，避免一处残留卡住全部 Job 功能，启动时只检测并提示。
    internal sealed class WindowsBackgroundDirectJobs : IWindowsBackgroundJobs
    {
        private sealed class Entry
        {
            internal int Pid;
            internal long Birth;
            internal IntPtr Process, Job;
            internal GameAffinityJobRecord Record;
            internal bool Active, Pending;
        }

        private readonly object gate = new object();
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        public bool HasActive
        {
            get
            {
                lock (gate)
                {
                    foreach (Entry entry in entries.Values) if (entry.Active) return true;
                    return false;
                }
            }
        }

        public bool HasPending
        {
            get
            {
                lock (gate)
                {
                    foreach (Entry entry in entries.Values) if (entry.Pending) return true;
                    return false;
                }
            }
        }

        public bool ReadOriginalMask(WindowsBackgroundTarget target, out ulong mask, out string reason)
        {
            mask = 0; reason = null;
            string identityFailure;
            IntPtr process = GameAffinityJobNative.OpenQueryIdentity(target.Process.Pid, target.Process.Creation, out identityFailure);
            if (process == IntPtr.Zero) { reason = "微软后台原始范围读取未完成：" + identityFailure; return false; }
            try
            {
                if (!Native.TryQueryAffinity(process, out mask))
                {
                    reason = "微软后台 GetProcessAffinityMask 失败，win32=" + System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    return false;
                }
                if (mask == 0) { reason = "微软后台原始核心范围为空"; return false; }
                int identity = Native.QueryProcessIdentityState(process, target.Process.Creation);
                if (identity == 0) return true;
                mask = 0;
                reason = identity == 1 ? "微软后台读取期间" + GameAffinityJobNative.TargetGone
                    : "微软后台读取后身份未能核验";
                return false;
            }
            finally { Native.CloseHandle(process); }
        }

        public bool Ensure(WindowsBackgroundTarget target, ulong original, ulong desired, int mode, out string reason)
        {
            lock (gate)
            {
                reason = null;
                int pid = target.Process.Pid;
                long birth = target.Process.Creation;
                if (pid <= 4 || birth <= 0 || original == 0 || desired == 0 || IntPtr.Size != 8) { reason = "invalid-target"; return false; }
                Entry entry;
                if (entries.TryGetValue(pid, out entry) && entry.Birth != birth)
                {
                    // 编号被新进程复用：旧对象先按回执结清，再为新身份建立记录。
                    if (!ReleaseLocked(entry, out reason)) return false;
                    Drop(entry);
                    entry = null;
                }
                if (entry != null && entry.Pending) { reason = "recovery-pending"; return false; }
                if (entry != null && entry.Active)
                {
                    if (entry.Record.Desired == desired && entry.Record.Resources != null && entry.Record.Resources.MatchesMode(mode)) return true;
                    if (!ReleaseLocked(entry, out reason)) return false;
                }
                if (entry == null && !Open(target, original, desired, out entry, out reason)) return false;
                ulong current;
                if (!Native.TryQueryAffinity(entry.Process, out current) || current != original) { reason = "original-affinity-changed"; return false; }
                WindowsBackgroundLimits resources;
                if (!WindowsBackgroundJobNative.Capture(entry.Process, mode, out resources, out reason)) return false;
                entry.Record.Original = original;
                entry.Record.Desired = desired;
                entry.Record.Resources = resources;
                entry.Record.Phase = "armed";
                // 先持久保存原值和恢复归属，再写入限制。
                if (!WindowsBackgroundReceiptStore.Save(entry.Record)) { reason = "receipt-arm"; return false; }
                string applyFailure;
                if (!WindowsBackgroundJobNative.Apply(entry.Record, entry.Job, entry.Process, out applyFailure))
                {
                    // 写入或读回不一致时立即按回执撤回本方部分写入；撤回失败保留回执与句柄。
                    entry.Active = true;
                    string cleanup;
                    reason = applyFailure + (ReleaseLocked(entry, out cleanup) ? "" : "; recovery=" + cleanup);
                    return false;
                }
                entry.Active = true;
                entry.Pending = false;
                return true;
            }
        }

        private bool Open(WindowsBackgroundTarget target, ulong original, ulong desired, out Entry entry, out string reason)
        {
            entry = null; reason = null;
            int pid = target.Process.Pid;
            long birth = target.Process.Creation;
            Native.TryEnableDebugPrivilege();
            IntPtr process = Native.OpenProcess(0x101701, false, pid);
            if (process == IntPtr.Zero) { reason = "process-access"; return false; }
            if (!GameAffinityJobNative.Matches(process, birth)) { Native.CloseHandle(process); reason = "target-identity"; return false; }
            if (!string.IsNullOrEmpty(target.Process.Name)
                && !string.Equals(Native.ImageName(process), target.Process.Name, StringComparison.OrdinalIgnoreCase))
            { Native.CloseHandle(process); reason = "target-name"; return false; }
            string jobName = GameAffinityJobNative.Prefix + Guid.NewGuid().ToString("N") + "." + pid + "." + birth;
            IntPtr job = GameAffinityJobNative.Create(jobName, out reason);
            if (job == IntPtr.Zero) { Native.CloseHandle(process); return false; }
            int ownerPid;
            long ownerBirth, cpu;
            ulong io;
            using (Process owner = System.Diagnostics.Process.GetCurrentProcess()) ownerPid = owner.Id;
            if (!Native.QueryProcessSample(new IntPtr(-1), out ownerBirth, out cpu, out io))
            { Native.CloseHandle(process); Native.CloseHandle(job); reason = "owner-identity"; return false; }
            entry = new Entry
            {
                Pid = pid, Birth = birth, Process = process, Job = job,
                Record = new GameAffinityJobRecord
                {
                    Name = jobName, Pid = pid, Birth = birth, Original = original, Desired = desired,
                    OwnerPid = ownerPid, OwnerBirth = ownerBirth, Phase = "prepared", ThreadRecoveryRoot = ""
                }
            };
            entries.Add(pid, entry);
            return true;
        }

        // 只撤回本方写入：之后由外部改过的值保留，失败时保留回执、句柄和待恢复状态。
        private static bool ReleaseLocked(Entry entry, out string reason)
        {
            reason = null;
            if (!entry.Active && !entry.Pending) return true;
            bool gone = Native.WaitForSingleObject(entry.Process, 0) == 0;
            bool restored = gone ? WindowsBackgroundJobNative.ClearGone(entry.Record, entry.Job, out reason)
                : WindowsBackgroundJobNative.Restore(entry.Record, entry.Job, entry.Process, out reason);
            if (!restored) { entry.Pending = true; return false; }
            entry.Active = false;
            entry.Record.Phase = "neutral";
            // 限制已解除；中性回执未能保存时仍按待恢复处理，下一次释放会重新确认并保存。
            if (!WindowsBackgroundReceiptStore.Save(entry.Record)) { entry.Pending = true; reason = "receipt-neutral"; return false; }
            entry.Pending = false;
            return true;
        }

        // Job 句柄在 Pavise 运行期间保留，下一局复用同一个 Job，避免长期运行的服务进程每局多嵌套一层。
        private void Drop(Entry entry)
        {
            if (entry.Process != IntPtr.Zero) { Native.CloseHandle(entry.Process); entry.Process = IntPtr.Zero; }
            if (entry.Job != IntPtr.Zero) { Native.CloseHandle(entry.Job); entry.Job = IntPtr.Zero; }
            entries.Remove(entry.Pid);
            WindowsBackgroundReceiptStore.Remove(entry.Record.Name);
        }

        public bool Release(WindowsBackgroundTarget target, out string reason)
        {
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(target.Process.Pid, out entry) || entry.Birth != target.Process.Creation) { reason = null; return true; }
                if (!ReleaseLocked(entry, out reason)) return false;
                if (Native.WaitForSingleObject(entry.Process, 0) == 0) Drop(entry);
                return true;
            }
        }

        public bool ReleaseAll(out string reason)
        {
            lock (gate)
            {
                reason = null;
                bool ok = true;
                foreach (Entry entry in new List<Entry>(entries.Values))
                {
                    string failure;
                    if (!ReleaseLocked(entry, out failure)) { ok = false; reason = failure; continue; }
                    if (Native.WaitForSingleObject(entry.Process, 0) == 0) Drop(entry);
                }
                return ok;
            }
        }

        public bool Stop(out string reason)
        {
            lock (gate)
            {
                if (!ReleaseAll(out reason)) return false;
                foreach (Entry entry in new List<Entry>(entries.Values)) Drop(entry);
                return true;
            }
        }

        // 供系统台架在施加后单独读回；运行期不再调用。
        internal bool VerifyForTest(WindowsBackgroundTarget target, ulong desired, out string reason)
        {
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(target.Process.Pid, out entry) || entry.Birth != target.Process.Creation || !entry.Active)
                { reason = "job-not-owned"; return false; }
                if (entry.Record.Desired != desired) { reason = "job-requested-mask-changed"; return false; }
                return WindowsBackgroundJobNative.Verify(entry.Record, entry.Job, entry.Process, out reason);
            }
        }

        // 启动时检查上次异常退出留下的限制：能重新打开 Job 就按回执恢复；
        // 名称已消失时限制无法解除，只记录提示，保留回执直到目标进程退出。每次启动最多处理一轮。
        internal static void ReportLeftovers()
        {
            List<GameAffinityJobRecord> records;
            if (!WindowsBackgroundReceiptStore.Read(out records))
            {
                Logger.Warn("微软后台治理回执读取失败，无法检查上次异常退出的遗留限制");
                return;
            }
            foreach (GameAffinityJobRecord record in records)
            {
                if (GameAffinityJobNative.Probe(record.OwnerPid, record.OwnerBirth) == 0) continue;
                int target = GameAffinityJobNative.Probe(record.Pid, record.Birth);
                if (record.Phase != "armed" || target == 1)
                {
                    WindowsBackgroundReceiptStore.Remove(record.Name);
                    continue;
                }
                if (target != 0) continue;
                IntPtr job = GameAffinityJobNative.Open(record.Name);
                if (job != IntPtr.Zero)
                {
                    IntPtr process = WindowsBackgroundJobNative.OpenRestore(record);
                    string reason = null;
                    bool restored = process != IntPtr.Zero && WindowsBackgroundJobNative.Restore(record, job, process, out reason);
                    if (process != IntPtr.Zero) Native.CloseHandle(process);
                    Native.CloseHandle(job);
                    if (restored)
                    {
                        WindowsBackgroundReceiptStore.Remove(record.Name);
                        Logger.Log("微软后台遗留限制已恢复：进程编号=" + record.Pid);
                        continue;
                    }
                }
                Logger.Warn("微软后台治理在上次异常退出后未能恢复：进程编号=" + record.Pid
                    + "；限制所在的 Job 已无法重新打开，会保留到该服务进程重启或电脑重启");
            }
        }
    }

    // 微软后台一次性治理的独立回执；与游戏亲和性 Job 回执分开保存。
    internal static class WindowsBackgroundReceiptStore
    {
        internal const string Key = "WindowsBackgroundJobReceiptsV1";

        private static readonly object gate = new object();

        private static bool Access(Func<List<GameAffinityJobRecord>, bool> action, bool write)
        {
            lock (gate)
            {
                try
                {
                    string raw;
                    if (!Settings.TryLoadStr(Key, out raw)) return false;
                    var records = new List<GameAffinityJobRecord>();
                    foreach (string line in (raw ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        // 无法解析的行不能丢弃，否则会失去恢复线索；整体按不可读处理。
                        GameAffinityJobRecord record = GameAffinityJobRecord.Parse(line.TrimEnd('\r'));
                        if (record == null || record.Resources == null || records.Count >= 128) return false;
                        records.Add(record);
                    }
                    if (!action(records)) return false;
                    if (!write) return true;
                    string[] rows = new string[records.Count];
                    for (int i = 0; i < rows.Length; i++) rows[i] = records[i].Encode();
                    string joined = string.Join("\n", rows), verify;
                    return Settings.SaveStrDurable(Key, joined) && Settings.TryLoadStr(Key, out verify) && verify == joined;
                }
                catch { return false; }
            }
        }

        internal static bool Read(out List<GameAffinityJobRecord> records)
        {
            List<GameAffinityJobRecord> result = null;
            bool ok = Access(delegate(List<GameAffinityJobRecord> rows) { result = rows; return true; }, false);
            records = result;
            return ok;
        }

        internal static bool Save(GameAffinityJobRecord record)
        {
            return Access(delegate(List<GameAffinityJobRecord> rows)
            {
                rows.RemoveAll(delegate(GameAffinityJobRecord row) { return row.Name == record.Name; });
                rows.Add(record);
                return rows.Count <= 128;
            }, true);
        }

        internal static bool Remove(string name)
        {
            return Access(delegate(List<GameAffinityJobRecord> rows)
            {
                rows.RemoveAll(delegate(GameAffinityJobRecord row) { return row.Name == name; });
                return true;
            }, true);
        }
    }
}
