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
        internal AntiCheatAffinityObservation(AntiCheatAffinityRead read, ushort group, ulong mask, ulong allowed, string detail)
        { Read = read; Group = group; Mask = mask; AllowedMask = allowed; Detail = detail ?? ""; }
    }

    // A setter must revalidate identity, group and expected affinity immediately before writing.
    // Windows has no compare-and-set process affinity API; a foreign writer can still race that check.
    internal interface IAntiCheatAffinityPlatform
    {
        string DeviceIdentity { get; }
        long NowMilliseconds { get; }
        AntiCheatAffinityObservation Read(AntiCheatAffinityIdentity identity);
        bool Write(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation observed);
        void Forget(AntiCheatAffinityIdentity identity);
    }
    internal interface IAntiCheatAffinityStore
    {
        bool Load(out string receipt);
        bool Save(string receipt);
    }
    // Restoration only changes the owned hard mask. A newly added CPU Sets policy is foreign
    // state and must neither prevent that restoration nor be removed by this owner.
    internal interface IAntiCheatAffinityRestorePlatform
    {
        bool Restore(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong original, out AntiCheatAffinityObservation observed);
    }
    internal sealed class AntiCheatAffinityResult
    {
        internal readonly string Code, Detail;
        internal readonly bool Bound, PendingRecovery;
        internal AntiCheatAffinityResult(string code, string detail, bool bound, bool pending)
        { Code = code; Detail = detail; Bound = bound; PendingRecovery = pending; }
    }

    // This owner has no topology policy: the caller supplies a verified, group-local hard mask.
    // No thread affinity or CPU Sets setter is used here.
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
            internal long NextRead, WindowStart;
            internal int Corrections;
            internal bool Stopped;
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

        internal AntiCheatAffinityLease() : this(new NativePlatform(), new SettingsStore()) { }
        internal AntiCheatAffinityLease(IAntiCheatAffinityPlatform native, IAntiCheatAffinityStore durable)
        { if (native == null || durable == null) throw new ArgumentNullException(); platform = native; store = durable; }
        internal static bool TryIdentify(int pid, long creation, string expectedName, out AntiCheatAffinityIdentity identity, out string reason)
        { return NativePlatform.TryIdentify(pid, creation, expectedName, out identity, out reason); }
        internal static bool TryInspect(AntiCheatAffinityIdentity identity, out ushort group, out ulong affinity, out string reason)
        {
            NativePlatform native = new NativePlatform();
            try
            {
                AntiCheatAffinityObservation read = native.Read(identity);
                group = read.Group; affinity = read.Mask; reason = read.Detail;
                if (read.Read == AntiCheatAffinityRead.Known && (!read.CpuSetsKnown || !read.CpuSetsEmpty))
                { reason = read.CpuSetsKnown ? "进程已有 CPU Sets 限制，保留原分配" : "无法核验进程已有核心限制"; return false; }
                return read.Read == AntiCheatAffinityRead.Known;
            }
            finally { native.Forget(identity); }
        }
        internal bool HasOwnership(AntiCheatAffinityIdentity identity)
        { lock (sync) { return identity != null && rows.ContainsKey(identity.Key); } }
        internal ulong OriginalAffinity(AntiCheatAffinityIdentity identity)
        { lock (sync) { Receipt row; return identity != null && rows.TryGetValue(identity.Key, out row) ? row.Original : 0; } }
        internal bool PendingRecovery { get { lock (sync) return recoveryPending || corrupt; } }
        private static AntiCheatAffinityResult Result(string code, string detail, bool bound, bool pending)
        { return new AntiCheatAffinityResult(code, detail, bound, pending); }
        private Guard GuardFor(AntiCheatAffinityIdentity identity)
        {
            Guard guard;
            if (!guards.TryGetValue(identity.Key, out guard)) guards.Add(identity.Key, guard = new Guard { Identity = identity });
            return guard;
        }
        private bool Load()
        {
            if (loaded) return !corrupt;
            string text;
            device = platform.DeviceIdentity;
            if (string.IsNullOrEmpty(device) || !store.Load(out text)) return false;
            if (string.IsNullOrEmpty(text)) { loaded = true; return true; }
            try
            {
                string[] lines = text.Split('\n');
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
            StringBuilder text = new StringBuilder();
            if (rows.Count != 0)
            {
                text.Append("ACHA1|").Append(device).Append('\n');
                List<string> keys = new List<string>(rows.Keys); keys.Sort(StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    Receipt row = rows[key];
                    text.Append(row.Identity.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Identity.CreationTime.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(row.Identity.ImagePath))).Append('|')
                        .Append(row.Group.ToString(CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Original.ToString("X", CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Applied.ToString("X", CultureInfo.InvariantCulture)).Append('|')
                        .Append(row.Previous.ToString("X", CultureInfo.InvariantCulture)).Append('|').Append(row.Phase).Append('\n');
                }
            }
            return store.Save(text.ToString());
        }
        private bool Remove(Receipt row)
        {
            rows.Remove(row.Identity.Key);
            if (!Save()) { rows[row.Identity.Key] = row; return Debt(row); }
            platform.Forget(row.Identity); return true;
        }
        private bool Debt(Receipt row)
        { recoveryPending = true; platform.Forget(row.Identity); return false; }
        internal bool Recover()
        {
            lock (sync)
            {
                if (!Load()) { recoveryPending = true; return false; }
                if (!recoveryPending) return true;
                bool ok = true;
                foreach (Receipt row in new List<Receipt>(rows.Values)) ok = Restore(row) && ok;
                recoveryPending = !ok || rows.Count != 0;
                return !recoveryPending;
            }
        }
        private bool Restore(Receipt row)
        {
            AntiCheatAffinityObservation read = platform.Read(row.Identity);
            if (read.Read == AntiCheatAffinityRead.Gone || read.Read == AntiCheatAffinityRead.DifferentProcess) return Remove(row);
            if (read.Read != AntiCheatAffinityRead.Known || read.Group != row.Group) return Debt(row);
            // A prepared target change can still be at our previous mask if the process crashed before Set.
            bool owned = read.Mask == row.Applied || ((row.Phase == 'P' || row.Phase == 'R') && row.Previous != 0 && read.Mask == row.Previous);
            if (!owned || read.Mask == row.Original) return Remove(row); // Preserve external modifications.
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
            // Permission failures and unverified writes retain the durable restore intent.
            return Debt(row);
        }
        internal AntiCheatAffinityResult Ensure(AntiCheatAffinityIdentity identity, ushort group, ulong targetMask)
        {
            lock (sync)
            {
                if (identity == null || identity.ProcessId <= 0 || identity.CreationTime <= 0 || targetMask == 0)
                    return Result("invalid-target", "进程身份或目标核心无效", false, false);
                if (!Load() || (recoveryPending && !Recover())) return Result("recovery-pending", "硬绑定恢复未完成", false, true);
                Guard guard = GuardFor(identity);
                if (guard.Stopped)
                {
                    AntiCheatAffinityResult last = guard.Last;
                    if (last == null || last.Bound || (last.PendingRecovery && !recoveryPending))
                        return guard.Last = Result("stopped", "硬绑定已停止，恢复记录已处理", false, recoveryPending);
                    return last;
                }
                Receipt row;
                bool acquired = rows.TryGetValue(identity.Key, out row);
                long now = platform.NowMilliseconds;
                if (acquired && row.Group == group && row.Applied == targetMask && now < guard.NextRead)
                    return guard.Last ?? Result("bound", "已硬绑定辅助核", true, false);
                guard.NextRead = now + 1000;
                AntiCheatAffinityObservation read = platform.Read(identity);
                if (read.Read != AntiCheatAffinityRead.Known)
                {
                    if (acquired)
                    {
                        if (read.Read == AntiCheatAffinityRead.Gone || read.Read == AntiCheatAffinityRead.DifferentProcess) Remove(row);
                        else recoveryPending = true;
                    }
                    platform.Forget(identity);
                    guard.Stopped = true;
                    return guard.Last = Result(read.Read == AntiCheatAffinityRead.Unsupported ? "unsupported" : "unavailable", read.Detail, false, recoveryPending);
                }
                if (!read.CpuSetsKnown || !read.CpuSetsEmpty)
                {
                    if (acquired && !Restore(row)) recoveryPending = true;
                    else if (!acquired) platform.Forget(identity);
                    guard.Stopped = true;
                    return guard.Last = Result("unsupported", read.CpuSetsKnown ? "检测到 CPU Sets 限制，已让出硬绑定" : "无法核验进程已有核心限制，已停止硬绑定", false, recoveryPending);
                }
                if (read.Group != group || (targetMask & ~read.AllowedMask) != 0)
                {
                    if (acquired && !Restore(row)) recoveryPending = true;
                    else if (!acquired) platform.Forget(identity);
                    guard.Stopped = true;
                    return guard.Last = Result("unsupported", "目标核心不属于进程当前单一处理器组", false, recoveryPending);
                }
                if (acquired && row.Applied == targetMask && read.Mask == row.Applied)
                    return guard.Last = Result("bound", "已硬绑定辅助核", true, false);
                bool drift = acquired && read.Mask != row.Applied;
                if (drift)
                {
                    if (now - guard.WindowStart >= 60000 || now < guard.WindowStart) { guard.WindowStart = now; guard.Corrections = 0; }
                    if (guard.Corrections >= 3)
                    {
                        guard.Stopped = true;
                        bool cleared = Remove(row);
                        return guard.Last = Result("conflict", "亲和性持续冲突，本局停止硬绑定", false, !cleared);
                    }
                    guard.Corrections++;
                }
                if (!acquired && read.Mask == targetMask)
                    return guard.Last = Result("already-matched", "进程已有相同硬亲和性，未接管", false, false);
                Receipt prior = acquired ? row.Copy() : null;
                if (!acquired)
                {
                    row = new Receipt(); row.Identity = identity; row.Group = group; row.Original = read.Mask;
                    rows.Add(identity.Key, row);
                }
                if (drift) row.Original = read.Mask; // Latest foreign setting is the restore baseline.
                row.Previous = acquired && !drift ? row.Applied : 0;
                row.Applied = targetMask; row.Phase = 'P';
                if (!Save())
                {
                    if (prior == null) rows.Remove(identity.Key); else rows[identity.Key] = prior;
                    guard.Stopped = true;
                    return guard.Last = Result("journal-failed", "无法持久保存硬绑定恢复记录", false, false);
                }
                AntiCheatAffinityObservation after;
                bool wrote = platform.Write(identity, group, read.Mask, targetMask, out after);
                if (wrote && after != null && after.Read == AntiCheatAffinityRead.Known && after.Group == group && after.Mask == targetMask)
                {
                    row.Phase = 'A';
                    if (Save()) return guard.Last = Result(drift ? "reasserted" : "bound", drift ? "检测到亲和性被修改，已重新硬绑定" : "已硬绑定辅助核", true, false);
                }
                guard.Stopped = true; Debt(row);
                // Keep the prepared intent even when Set reports failure: the write may have occurred.
                return guard.Last = Result("write-unverified", after == null || after.Detail.Length == 0 ? "硬绑定未核验，等待恢复" : after.Detail, false, true);
            }
        }
        internal bool Release(AntiCheatAffinityIdentity identity)
        {
            lock (sync)
            {
                if (!Load()) { recoveryPending = true; return false; }
                Receipt row;
                if (identity == null) return true;
                if (!rows.TryGetValue(identity.Key, out row))
                {
                    platform.Forget(identity); guards.Remove(identity.Key);
                    if (rows.Count == 0) recoveryPending = false;
                    return true;
                }
                bool ok = Restore(row);
                if (!ok) recoveryPending = true;
                else
                {
                    guards.Remove(identity.Key);
                    if (rows.Count == 0) recoveryPending = false;
                }
                return ok;
            }
        }
        internal bool ReleaseAll()
        {
            lock (sync)
            {
                if (!Load()) { recoveryPending = true; return false; }
                bool ok = true;
                foreach (Receipt row in new List<Receipt>(rows.Values)) ok = Restore(row) && ok;
                recoveryPending = !ok || rows.Count != 0;
                if (!recoveryPending)
                {
                    foreach (Guard guard in guards.Values) platform.Forget(guard.Identity);
                    guards.Clear();
                }
                return !recoveryPending;
            }
        }

        private sealed class NativePlatform : IAntiCheatAffinityPlatform, IAntiCheatAffinityRestorePlatform
        {
            private readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            private readonly Stopwatch timer = Stopwatch.StartNew();
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
                    // A failure here blocks admission but not restoration of our separately owned hard mask.
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
                        return Failure(Marshal.GetLastWin32Error() == 87 ? AntiCheatAffinityRead.Gone : AntiCheatAffinityRead.Unavailable, "无法读取进程；权限拒绝时停止重试");
                    handles.Add(identity.Key, handle);
                }
                AntiCheatAffinityObservation read = ReadHandle(handle, identity);
                if (read.Read == AntiCheatAffinityRead.Gone || read.Read == AntiCheatAffinityRead.DifferentProcess) Forget(identity);
                return read;
            }
            public bool Write(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation observed)
            { return WriteMask(identity, group, expected, target, true, out observed); }
            public bool Restore(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, out AntiCheatAffinityObservation observed)
            { return WriteMask(identity, group, expected, target, false, out observed); }
            private bool WriteMask(AntiCheatAffinityIdentity identity, ushort group, ulong expected, ulong target, bool admission, out AntiCheatAffinityObservation observed)
            {
                observed = null;
                IntPtr handle = OpenProcess(0x1600, false, identity.ProcessId);
                if (handle == IntPtr.Zero) { observed = Failure(Marshal.GetLastWin32Error() == 87 ? AntiCheatAffinityRead.Gone : AntiCheatAffinityRead.Unavailable, "硬绑定权限被拒绝，保留恢复记录"); return false; }
                try
                {
                    observed = ReadHandle(handle, identity);
                    if (observed.Read != AntiCheatAffinityRead.Known || observed.Group != group || observed.Mask != expected || target == 0 || (target & ~observed.AllowedMask) != 0) return false;
                    if (admission && (!observed.CpuSetsKnown || !observed.CpuSetsEmpty)) return false;
                    bool ok = SetProcessAffinityMask(handle, new UIntPtr(target));
                    observed = ReadHandle(handle, identity);
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
        }
    }
}
