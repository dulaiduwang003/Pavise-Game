using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PaviseApp
{
    // 由 GameAffinityJobOperation 串行调用；独立 Job 守护负责前向写入，父进程保留句柄用于撤权后的恢复。
    internal static class SGuardThreadAffinityJournal
    {
        private const int MaximumThreads = 4096, MaximumNewThreads = 256, MaximumFailures = 3;
        private const uint ThreadAccess = 0x100060;
        private static readonly ConcurrentDictionary<string, IntPtr> retained = new ConcurrentDictionary<string, IntPtr>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, long> logTimes = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private sealed class Row
        {
            internal uint Tid;
            internal long Birth;
            internal ushort OriginalGroup, PreviousGroup, RecoveryGroup;
            internal ulong Original, Previous, RecoveryMask;
            internal bool PreviousKnown, RecoveryCaptured, RecoveryOwned, Acquired, RecoveryOnly;
            internal string CpuSetIds = "", CpuSetMasks = "";
            internal bool CpuSetsCaptured, CpuSetMasksObserved;
            internal int Failures;
            internal string Phase = "prepared";
            internal bool Terminal { get { return Phase == "restored" || Phase == "gone" || Phase == "external" || Phase == "not-applied"; } }
        }
        private sealed class Journal
        {
            internal string Name;
            internal int Pid, OwnerPid, PrepareAttempts, NewThreads, NewFailures, RecoverySnapshots;
            internal long Birth, OwnerBirth;
            internal ulong Original, Desired;
            internal bool Prepared, Acquired, Recovering, IndirectIntent;
            internal bool StrictThreadAdmission, CpuSetsCaptured, CpuSetMasksObserved;
            internal string ProcessCpuSetIds = "", ProcessCpuSetMasks = "";
            internal readonly List<Row> Rows = new List<Row>();
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity { internal UIntPtr Mask; internal ushort Group, R0, R1, R2; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry { internal uint Size, Usage, Tid, OwnerPid; internal int BasePriority, DeltaPriority; internal uint Flags; }
        private sealed class Fact : IDisposable
        {
            internal IntPtr Handle;
            internal ushort Group;
            internal ulong Mask;
            internal bool Gone;
            internal string CpuSetIds, CpuSetMasks;
            internal bool CpuSetMasksObserved;
            public void Dispose() { }
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessIdOfThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadTimes(IntPtr handle, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessAffinityMask(IntPtr handle, out UIntPtr process, out UIntPtr system);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadGroupAffinity(IntPtr handle, out GroupAffinity affinity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadGroupAffinity(IntPtr handle, ref GroupAffinity affinity, out GroupAffinity previous);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessDefaultCpuSets(IntPtr handle, [Out] uint[] ids, uint capacity, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadSelectedCpuSets(IntPtr handle, [Out] uint[] ids, uint capacity, out uint required);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessDefaultCpuSetMasks(IntPtr handle, [Out] GroupAffinity[] masks, ushort capacity, out ushort required);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadSelectedCpuSetMasks(IntPtr handle, [Out] GroupAffinity[] masks, ushort capacity, out ushort required);
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();

        private static string HandleKey(Journal journal, Row row) { return journal.Name + ":" + row.Tid + ":" + row.Birth; }
        private static string Location(GameAffinityJobRecord record)
        {
            if (record == null || !GameAffinityJobRecord.ValidName(record.Name, record.Pid, record.Birth)) throw new InvalidDataException("invalid-job-identity");
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaviseAuto", "SGuardThreadAffinityV1");
            Directory.CreateDirectory(root);
            for (DirectoryInfo path = new DirectoryInfo(root); path != null; path = path.Parent)
                if ((path.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse-journal-root");
            string digest;
            using (SHA256 sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(record.Name + "|"
                + record.Original.ToString(CultureInfo.InvariantCulture) + "|" + record.Desired.ToString(CultureInfo.InvariantCulture)))).Replace("-", "").ToLowerInvariant();
            string result = Path.Combine(root, "sguard-" + digest + ".xml");
            if (File.Exists(result) && (File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse-journal");
            return result;
        }
        private static void Attribute(XmlWriter writer, string name, object value)
        { writer.WriteAttributeString(name, Convert.ToString(value, CultureInfo.InvariantCulture)); }
        private static string Encode(Journal journal)
        {
            var text = new StringBuilder();
            using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("sguardThreads");
                Attribute(writer, "version", 1); Attribute(writer, "name", journal.Name); Attribute(writer, "pid", journal.Pid);
                Attribute(writer, "birth", journal.Birth); Attribute(writer, "ownerPid", journal.OwnerPid); Attribute(writer, "ownerBirth", journal.OwnerBirth);
                Attribute(writer, "original", journal.Original); Attribute(writer, "desired", journal.Desired); Attribute(writer, "group", 0);
                // CPU Sets 从不写入；普通反作弊可保留进程默认集合，日志记录实际快照及接口是否可读。
                Attribute(writer, "strictThreadAdmission", journal.StrictThreadAdmission);
                Attribute(writer, "processCpuSetsCaptured", journal.CpuSetsCaptured);
                Attribute(writer, "processCpuSetMasksObserved", journal.CpuSetMasksObserved);
                Attribute(writer, "processCpuSetIds", journal.ProcessCpuSetIds); Attribute(writer, "processCpuSetMasks", journal.ProcessCpuSetMasks);
                Attribute(writer, "prepareAttempts", journal.PrepareAttempts); Attribute(writer, "newThreads", journal.NewThreads); Attribute(writer, "newFailures", journal.NewFailures);
                Attribute(writer, "recoverySnapshots", journal.RecoverySnapshots);
                Attribute(writer, "prepared", journal.Prepared); Attribute(writer, "acquired", journal.Acquired); Attribute(writer, "recovering", journal.Recovering);
                Attribute(writer, "indirectIntent", journal.IndirectIntent);
                foreach (Row row in journal.Rows)
                {
                    writer.WriteStartElement("thread"); Attribute(writer, "tid", row.Tid); Attribute(writer, "birth", row.Birth);
                    Attribute(writer, "group", row.OriginalGroup); Attribute(writer, "original", row.Original); Attribute(writer, "applied", journal.Desired);
                    Attribute(writer, "cpuSetsCaptured", row.CpuSetsCaptured); Attribute(writer, "cpuSetMasksObserved", row.CpuSetMasksObserved);
                    Attribute(writer, "cpuSetIds", row.CpuSetIds); Attribute(writer, "cpuSetMasks", row.CpuSetMasks); Attribute(writer, "phase", row.Phase);
                    Attribute(writer, "previousKnown", row.PreviousKnown); Attribute(writer, "previousGroup", row.PreviousGroup); Attribute(writer, "previous", row.Previous);
                    Attribute(writer, "acquired", row.Acquired);
                    Attribute(writer, "recoveryOnly", row.RecoveryOnly);
                    Attribute(writer, "failures", row.Failures); Attribute(writer, "recoveryCaptured", row.RecoveryCaptured);
                    Attribute(writer, "recoveryOwned", row.RecoveryOwned); Attribute(writer, "recoveryGroup", row.RecoveryGroup); Attribute(writer, "recoveryMask", row.RecoveryMask);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            return text.ToString();
        }
        private static ulong Number(XmlElement node, string name) { return ulong.Parse(node.GetAttribute(name), CultureInfo.InvariantCulture); }
        private static bool Flag(XmlElement node, string name) { return bool.Parse(node.GetAttribute(name)); }
        private static Journal Load(GameAffinityJobRecord record, bool create)
        {
            string path = Location(record);
            if (!File.Exists(path))
            {
                if (!create) return null;
                return new Journal { Name = record.Name, Pid = record.Pid, Birth = record.Birth, OwnerPid = record.OwnerPid,
                    OwnerBirth = record.OwnerBirth, Original = record.Original, Desired = record.Desired, StrictThreadAdmission = record.StrictThreadAdmission };
            }
            if (new FileInfo(path).Length > 4194304) throw new InvalidDataException("journal-size-limit");
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "sguardThreads" || Number(root, "version") != 1 || Number(root, "group") != 0)
                throw new InvalidDataException("journal-format");
            var journal = new Journal { Name = root.GetAttribute("name"), Pid = checked((int)Number(root, "pid")), Birth = checked((long)Number(root, "birth")),
                OwnerPid = checked((int)Number(root, "ownerPid")), OwnerBirth = checked((long)Number(root, "ownerBirth")),
                Original = Number(root, "original"), Desired = Number(root, "desired"), PrepareAttempts = checked((int)Number(root, "prepareAttempts")),
                NewThreads = checked((int)Number(root, "newThreads")), NewFailures = checked((int)Number(root, "newFailures")),
                RecoverySnapshots = root.HasAttribute("recoverySnapshots") ? checked((int)Number(root, "recoverySnapshots")) : 0,
                Prepared = Flag(root, "prepared"), Acquired = Flag(root, "acquired"), Recovering = Flag(root, "recovering"),
                IndirectIntent = root.HasAttribute("indirectIntent") && Flag(root, "indirectIntent"),
                StrictThreadAdmission = root.HasAttribute("strictThreadAdmission") && Flag(root, "strictThreadAdmission"),
                CpuSetsCaptured = root.HasAttribute("processCpuSetsCaptured") ? Flag(root, "processCpuSetsCaptured") : Flag(root, "prepared"),
                CpuSetMasksObserved = root.HasAttribute("processCpuSetMasksObserved") && Flag(root, "processCpuSetMasksObserved"),
                ProcessCpuSetIds = root.GetAttribute("processCpuSetIds"), ProcessCpuSetMasks = root.GetAttribute("processCpuSetMasks") };
            if (journal.Name != record.Name || journal.Pid != record.Pid || journal.Birth != record.Birth || journal.OwnerPid != record.OwnerPid || journal.OwnerBirth != record.OwnerBirth
                || journal.Original != record.Original || journal.Desired != record.Desired || journal.StrictThreadAdmission != record.StrictThreadAdmission
                || !ValidCpuSetIds(journal.ProcessCpuSetIds) || !ValidCpuSetMasks(journal.ProcessCpuSetMasks)
                || (!journal.StrictThreadAdmission && (journal.ProcessCpuSetIds.Length != 0 || journal.ProcessCpuSetMasks.Length != 0))
                || (journal.Prepared && !journal.CpuSetsCaptured)
                || (!journal.CpuSetsCaptured && (journal.ProcessCpuSetIds.Length != 0 || journal.ProcessCpuSetMasks.Length != 0))
                || journal.PrepareAttempts > 3 || journal.NewThreads > MaximumNewThreads || journal.NewFailures > MaximumFailures || journal.RecoverySnapshots > 3)
                throw new InvalidDataException("journal-scope-or-budget-changed");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in root.ChildNodes)
            {
                XmlElement element = node as XmlElement;
                if (element == null || element.Name != "thread") throw new InvalidDataException("journal-row-format");
                var row = new Row { Tid = checked((uint)Number(element, "tid")), Birth = checked((long)Number(element, "birth")),
                    OriginalGroup = checked((ushort)Number(element, "group")), Original = Number(element, "original"), Phase = element.GetAttribute("phase"),
                    PreviousKnown = Flag(element, "previousKnown"), PreviousGroup = checked((ushort)Number(element, "previousGroup")), Previous = Number(element, "previous"),
                    Acquired = element.HasAttribute("acquired") ? Flag(element, "acquired") : element.GetAttribute("phase") == "applied",
                    RecoveryOnly = element.HasAttribute("recoveryOnly") && Flag(element, "recoveryOnly"),
                    CpuSetIds = element.GetAttribute("cpuSetIds"), CpuSetMasks = element.GetAttribute("cpuSetMasks"),
                    CpuSetsCaptured = !element.HasAttribute("cpuSetsCaptured") || Flag(element, "cpuSetsCaptured"),
                    CpuSetMasksObserved = element.HasAttribute("cpuSetMasksObserved") && Flag(element, "cpuSetMasksObserved"),
                    Failures = checked((int)Number(element, "failures")), RecoveryCaptured = Flag(element, "recoveryCaptured"), RecoveryOwned = Flag(element, "recoveryOwned"),
                    RecoveryGroup = checked((ushort)Number(element, "recoveryGroup")), RecoveryMask = Number(element, "recoveryMask") };
                if (row.Tid == 0 || row.Birth <= 0 || row.OriginalGroup != 0 || row.Original == 0 || (!row.RecoveryOnly && (row.Original & ~journal.Original) != 0)
                    || Number(element, "applied") != journal.Desired || !row.CpuSetsCaptured || !ValidCpuSetIds(row.CpuSetIds) || !ValidCpuSetMasks(row.CpuSetMasks)
                    || (!row.RecoveryOnly && (row.CpuSetIds.Length != 0 || row.CpuSetMasks.Length != 0))
                    || !identities.Add(row.Tid + ":" + row.Birth) || journal.Rows.Count >= MaximumThreads || row.Failures > MaximumFailures
                    || (row.Phase != "prepared" && row.Phase != "intent" && row.Phase != "applied" && row.Phase != "restore-intent" && !row.Terminal)
                    || (row.PreviousKnown && row.Previous == 0) || (row.RecoveryCaptured && row.RecoveryMask == 0)
                    || (row.RecoveryOnly && (!journal.Recovering || !row.RecoveryCaptured || row.Acquired || row.RecoveryOwned))) throw new InvalidDataException("journal-row-invalid");
                journal.Rows.Add(row);
            }
            return journal;
        }
        private static void Save(GameAffinityJobRecord record, Journal journal)
        {
            string path = Location(record), pending = path + ".pending", data = Encode(journal);
            if (File.Exists(pending) && (File.GetAttributes(pending) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse-pending-journal");
            byte[] bytes = new UTF8Encoding(false).GetBytes(data);
            if (bytes.Length > 4194304) throw new InvalidDataException("journal-size-limit");
            using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
            if (File.ReadAllText(path, Encoding.UTF8) != data || Encode(Load(record, false)) != data) throw new IOException("journal-readback-mismatch");
        }
        private static bool Failure(string phase, out string reason)
        { reason = "反作弊线程范围未确认：阶段=" + phase + " error=" + Marshal.GetLastWin32Error(); return false; }
        private static bool Error(string phase, Exception error, out string reason)
        { reason = "反作弊线程恢复日志未确认：阶段=" + phase + " 类型=" + error.GetType().Name + " detail=" + error.Message; return false; }
        private static bool ProcessKnown(GameAffinityJobRecord record, IntPtr process, out ulong mask, out bool gone, out string reason, bool forward = true)
        {
            mask = 0; gone = false; reason = null;
            if (forward && !SGuardAffinityOperations.ForwardAllowed(record))
            { reason = "反作弊线程写入资格已撤销：阶段=forward-revoked"; return false; }
            if (process == IntPtr.Zero)
            {
                if (GameAffinityJobNative.Probe(record.Pid, record.Birth) == 1) { gone = true; return true; }
                return Failure("process-handle-unavailable", out reason);
            }
            uint state = WaitForSingleObject(process, 0);
            if (state == 0) { gone = true; return true; }
            if (state != 258 || GetProcessId(process) != (uint)record.Pid) return Failure("process-identity", out reason);
            long birth, exit, kernel, user;
            if (!GetProcessTimes(process, out birth, out exit, out kernel, out user) || birth != record.Birth || exit != 0) return Failure("process-birth", out reason);
            UIntPtr affinity, system;
            if (GetActiveProcessorGroupCount() != 1 || !GetProcessAffinityMask(process, out affinity, out system) || affinity == UIntPtr.Zero
                || (record.Desired & ~system.ToUInt64()) != 0) return Failure("process-group-affinity", out reason);
            if (!CpuSetsKnown(process, false, forward && !record.StrictThreadAdmission, out reason)) return false;
            mask = affinity.ToUInt64(); return true;
        }
        private static bool ValidCpuSetIds(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            string[] fields = value.Split(',');
            if (fields.Length > 4096) return false;
            uint prior = 0;
            for (int index = 0; index < fields.Length; index++)
            {
                uint id;
                if (!uint.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out id)
                    || id.ToString(CultureInfo.InvariantCulture) != fields[index] || (index > 0 && id <= prior)) return false;
                prior = id;
            }
            return true;
        }
        private static bool ValidCpuSetMasks(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            string[] fields = value.Split(',');
            if (fields.Length > 64) return false;
            ushort priorGroup = 0; ulong priorMask = 0;
            for (int index = 0; index < fields.Length; index++)
            {
                string[] pair = fields[index].Split(':'); ushort group; ulong mask;
                if (pair.Length != 2 || !ushort.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out group)
                    || !ulong.TryParse(pair[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask) || mask == 0
                    || group.ToString(CultureInfo.InvariantCulture) != pair[0] || mask.ToString("X", CultureInfo.InvariantCulture) != pair[1]
                    || (index > 0 && (group < priorGroup || (group == priorGroup && mask <= priorMask)))) return false;
                priorGroup = group; priorMask = mask;
            }
            return true;
        }
        private static bool CpuSetsKnown(IntPtr handle, bool thread, bool requireEmpty, out string reason)
        {
            string ids, masks; bool masksObserved;
            return ReadCpuSets(handle, thread, requireEmpty, out ids, out masks, out masksObserved, out reason);
        }
        private static bool ReadCpuSets(IntPtr handle, bool thread, bool requireEmpty,
            out string idSnapshot, out string maskSnapshot, out bool masksObserved, out string reason)
        {
            idSnapshot = maskSnapshot = ""; masksObserved = false; reason = null; uint required;
            bool read = thread ? GetThreadSelectedCpuSets(handle, null, 0, out required) : GetProcessDefaultCpuSets(handle, null, 0, out required);
            int error = Marshal.GetLastWin32Error();
            if ((!read && error != 122) || required > 4096 || (requireEmpty && required != 0))
            { reason = "反作弊线程准入暂停：阶段=" + (thread ? "thread" : "process") + "-cpu-set-ids count=" + required + " error=" + Marshal.GetLastWin32Error(); return false; }
            if (required > 0)
            {
                uint[] ids = new uint[required]; uint actual;
                if (!(thread ? GetThreadSelectedCpuSets(handle, ids, required, out actual) : GetProcessDefaultCpuSets(handle, ids, required, out actual)) || actual > required)
                    return Failure("cpu-set-ids-readback", out reason);
                Array.Sort(ids, 0, checked((int)actual));
                var text = new StringBuilder();
                for (int index = 0; index < actual; index++)
                {
                    if (index > 0) text.Append(',');
                    text.Append(ids[index].ToString(CultureInfo.InvariantCulture));
                }
                idSnapshot = text.ToString();
                if (!ValidCpuSetIds(idSnapshot)) return Failure("cpu-set-ids-format", out reason);
            }
            else if (!read) return Failure("cpu-set-ids-empty-unconfirmed", out reason);
            try
            {
                ushort masks;
                read = thread ? GetThreadSelectedCpuSetMasks(handle, null, 0, out masks) : GetProcessDefaultCpuSetMasks(handle, null, 0, out masks);
                error = Marshal.GetLastWin32Error();
                if ((!read && error != 122) || masks > 64 || (requireEmpty && masks != 0))
                { reason = "反作弊线程准入暂停：阶段=" + (thread ? "thread" : "process") + "-cpu-set-masks count=" + masks + " error=" + Marshal.GetLastWin32Error(); return false; }
                if (masks > 0)
                {
                    GroupAffinity[] values = new GroupAffinity[masks]; ushort actual;
                    if (!(thread ? GetThreadSelectedCpuSetMasks(handle, values, masks, out actual) : GetProcessDefaultCpuSetMasks(handle, values, masks, out actual)) || actual > masks)
                        return Failure("cpu-set-masks-readback", out reason);
                    Array.Sort(values, 0, actual, Comparer<GroupAffinity>.Create(delegate(GroupAffinity left, GroupAffinity right)
                    {
                        int group = left.Group.CompareTo(right.Group);
                        return group != 0 ? group : left.Mask.ToUInt64().CompareTo(right.Mask.ToUInt64());
                    }));
                    var text = new StringBuilder();
                    for (int index = 0; index < actual; index++)
                    {
                        if (index > 0) text.Append(',');
                        text.Append(values[index].Group.ToString(CultureInfo.InvariantCulture)).Append(':')
                            .Append(values[index].Mask.ToUInt64().ToString("X", CultureInfo.InvariantCulture));
                    }
                    maskSnapshot = text.ToString();
                    if (!ValidCpuSetMasks(maskSnapshot)) return Failure("cpu-set-masks-format", out reason);
                }
                else if (!read) return Failure("cpu-set-masks-empty-unconfirmed", out reason);
                masksObserved = true;
            }
            catch (EntryPointNotFoundException) { }
            return true;
        }
        private static void CaptureThreadCpuSets(Row row, Fact fact)
        {
            row.CpuSetIds = fact.CpuSetIds; row.CpuSetMasks = fact.CpuSetMasks;
            row.CpuSetMasksObserved = fact.CpuSetMasksObserved; row.CpuSetsCaptured = true;
        }
        private static bool CaptureProcessCpuSets(GameAffinityJobRecord record, IntPtr process, Journal journal, out string reason)
        {
            if (!ReadCpuSets(process, false, !record.StrictThreadAdmission, out journal.ProcessCpuSetIds,
                out journal.ProcessCpuSetMasks, out journal.CpuSetMasksObserved, out reason)) return false;
            journal.CpuSetsCaptured = true; return true;
        }
        private static bool ProcessCpuSetsUnchanged(GameAffinityJobRecord record, IntPtr process, Journal journal, out string reason)
        {
            string ids, masks; bool masksObserved;
            if (!ReadCpuSets(process, false, !record.StrictThreadAdmission, out ids, out masks, out masksObserved, out reason)) return false;
            if (journal.CpuSetsCaptured && journal.ProcessCpuSetIds == ids && journal.ProcessCpuSetMasks == masks
                && journal.CpuSetMasksObserved == masksObserved) return true;
            reason = "反作弊进程准入暂停：阶段=process-cpu-sets-changed"; return false;
        }
        private static bool FreshThreadAllowed(Journal journal, Row row, Fact fact, ulong processMask, out string reason)
        {
            reason = null;
            if (journal.StrictThreadAdmission && !row.Acquired && fact.Mask != processMask)
            { reason = "反作弊新线程准入暂停：阶段=strict-custom-mask:TID=" + row.Tid; return false; }
            return true;
        }
        private static bool Ids(int pid, out HashSet<uint> ids, out string reason)
        {
            ids = new HashSet<uint>(); reason = null;
            IntPtr snapshot = CreateToolhelp32Snapshot(4, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return Failure("thread-snapshot", out reason);
            try
            {
                var entry = new ThreadEntry(); entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry)); int count = 0;
                bool more = Thread32First(snapshot, ref entry);
                while (more)
                {
                    if (++count > 32768) { reason = "反作弊线程准入停止：阶段=snapshot-entry-budget"; return false; }
                    if (entry.OwnerPid == (uint)pid && (!ids.Add(entry.Tid) || ids.Count > MaximumThreads))
                    { reason = "反作弊线程准入停止：阶段=thread-count-budget"; return false; }
                    entry.Size = (uint)Marshal.SizeOf(typeof(ThreadEntry)); more = Thread32Next(snapshot, ref entry);
                }
                if (Marshal.GetLastWin32Error() != 18 || ids.Count == 0) return Failure("thread-snapshot-complete", out reason);
                return true;
            }
            finally { CloseHandle(snapshot); }
        }
        private static bool Read(Journal journal, Row row, out Fact fact, out string reason, bool forward = true)
        {
            fact = new Fact(); reason = null; IntPtr handle; string key = HandleKey(journal, row);
            if (!retained.TryGetValue(key, out handle))
            {
                handle = OpenThread(ThreadAccess, false, row.Tid);
                if (handle == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 87 && row.Birth > 0) { fact.Gone = true; return true; }
                    return Failure("thread-open:TID=" + row.Tid, out reason);
                }
                long birth, exit, kernel, user; uint ownerPid = GetProcessIdOfThread(handle);
                if (ownerPid != (uint)journal.Pid)
                {
                    if (ownerPid != 0 && row.Birth > 0) { CloseHandle(handle); fact.Gone = true; return true; }
                    Failure("thread-owner:TID=" + row.Tid, out reason); CloseHandle(handle); return false;
                }
                if (!GetThreadTimes(handle, out birth, out exit, out kernel, out user))
                { Failure("thread-identity:TID=" + row.Tid, out reason); CloseHandle(handle); return false; }
                if (row.Birth > 0 && birth != row.Birth)
                { CloseHandle(handle); fact.Gone = true; return true; }
                if (birth <= 0) { CloseHandle(handle); return Failure("thread-birth:TID=" + row.Tid, out reason); }
                row.Birth = birth; key = HandleKey(journal, row);
                if (!retained.TryAdd(key, handle)) { CloseHandle(handle); if (!retained.TryGetValue(key, out handle)) return Failure("thread-handle-race:TID=" + row.Tid, out reason); }
            }
            uint state = WaitForSingleObject(handle, 0);
            if (state == 0) { fact.Gone = true; return true; }
            if (state != 258) return Failure("thread-alive:TID=" + row.Tid, out reason);
            GroupAffinity affinity;
            if (!GetThreadGroupAffinity(handle, out affinity)) return Failure("GetThreadGroupAffinity:TID=" + row.Tid, out reason);
            if (affinity.Group != 0 || affinity.Mask == UIntPtr.Zero
                || !ReadCpuSets(handle, true, forward && !(journal.StrictThreadAdmission && row.Acquired),
                    out fact.CpuSetIds, out fact.CpuSetMasks, out fact.CpuSetMasksObserved, out reason))
            { if (reason == null) reason = "反作弊线程准入暂停：阶段=thread-group:TID=" + row.Tid; return false; }
            fact.Handle = handle; fact.Group = affinity.Group; fact.Mask = affinity.Mask.ToUInt64(); return true;
        }
        private static void ReleaseTerminal(Journal journal)
        {
            foreach (Row row in journal.Rows)
            {
                if (!row.Terminal) continue;
                string key = HandleKey(journal, row); IntPtr handle;
                if (retained.TryGetValue(key, out handle) && CloseHandle(handle)) { IntPtr removed; retained.TryRemove(key, out removed); }
            }
        }
        private static void ReleaseUnrecorded(Journal journal)
        {
            if (journal == null) return;
            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (Row row in journal.Rows) owned.Add(HandleKey(journal, row));
            var remove = new List<string>();
            foreach (string key in retained.Keys)
                if (key.StartsWith(journal.Name + ":", StringComparison.Ordinal) && !owned.Contains(key)) remove.Add(key);
            foreach (string key in remove)
            {
                IntPtr handle, removed;
                if (retained.TryGetValue(key, out handle) && CloseHandle(handle)) retained.TryRemove(key, out removed);
            }
        }
        internal static bool Prepare(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null; Journal journal = null;
            try
            {
                journal = Load(record, true);
                if (journal.Recovering)
                {
                    foreach (Row row in journal.Rows)
                        if (!row.Terminal) { reason = "反作弊线程准入停止：阶段=recovery-pending"; return false; }
                    ReleaseTerminal(journal);
                    journal.Rows.RemoveAll(delegate(Row row) { return row.RecoveryOnly; });
                    // 历史身份与失败次数归属同一配置阶段，切回旧配置不重新快照未知成员。
                    journal.Prepared = false; journal.Acquired = false; journal.Recovering = false; journal.IndirectIntent = false; journal.RecoverySnapshots = 0;
                    journal.CpuSetsCaptured = false; journal.CpuSetMasksObserved = false;
                    journal.ProcessCpuSetIds = journal.ProcessCpuSetMasks = "";
                    Save(record, journal);
                }
                if (journal.Prepared) return PrepareMaintenance(record, process, out reason);
                bool reopening = journal.Rows.Count > 0;
                while (journal.PrepareAttempts < 3)
                {
                    journal.PrepareAttempts++; Save(record, journal);
                    ulong mask; bool gone;
                    if (!ProcessKnown(record, process, out mask, out gone, out reason) || gone || mask != record.Original)
                    { if (reason == null) reason = "反作弊线程准入暂停：阶段=original-process-range"; return false; }
                    if (!CaptureProcessCpuSets(record, process, journal, out reason)) return false;
                    HashSet<uint> before, after;
                    if (!Ids(record.Pid, out before, out reason)) return false;
                    var rows = new List<Row>(); bool unstable = false;
                    foreach (uint tid in before)
                    {
                        var row = new Row { Tid = tid }; Fact fact;
                        if (!Read(journal, row, out fact, out reason)) return false;
                        if (fact.Gone) { unstable = true; break; }
                        if (!FreshThreadAllowed(journal, row, fact, mask, out reason)) return false;
                        if ((fact.Mask & ~record.Original) != 0) { reason = "反作弊线程准入拒绝：阶段=original-thread-range:TID=" + tid; return false; }
                        row.OriginalGroup = fact.Group; row.Original = fact.Mask; CaptureThreadCpuSets(row, fact);
                        if (reopening)
                        {
                            Row historical = null;
                            foreach (Row previous in journal.Rows) if (previous.Tid == row.Tid && previous.Birth == row.Birth) { historical = previous; break; }
                            if (historical == null)
                            {
                                if (journal.NewThreads >= MaximumNewThreads || journal.NewFailures >= MaximumFailures || journal.Rows.Count >= MaximumThreads)
                                { reason = "反作弊线程再次准入停止：阶段=historical-new-thread-budget"; return false; }
                                journal.NewThreads++; row.Phase = "not-applied"; journal.Rows.Add(row); Save(record, journal);
                            }
                            else row.Failures = historical.Failures;
                        }
                        rows.Add(row);
                    }
                    if (unstable) continue;
                    if (!Ids(record.Pid, out after, out reason)) return false;
                    if (!before.SetEquals(after)) continue;
                    foreach (Row row in rows)
                    {
                        Fact fact; if (!Read(journal, row, out fact, out reason)) return false;
                        if (fact.Gone || fact.Group != row.OriginalGroup || fact.Mask != row.Original) { unstable = true; break; }
                    }
                    if (unstable) continue;
                    if (!ProcessKnown(record, process, out mask, out gone, out reason) || gone || mask != record.Original)
                    { if (reason == null) reason = "反作弊线程准入暂停：阶段=process-prewrite"; return false; }
                    if (!ProcessCpuSetsUnchanged(record, process, journal, out reason)) return false;
                    if (reopening)
                    {
                        foreach (Row row in rows)
                        {
                            for (int index = 0; index < journal.Rows.Count; index++)
                                if (journal.Rows[index].Tid == row.Tid && journal.Rows[index].Birth == row.Birth)
                                { row.Phase = "prepared"; journal.Rows[index] = row; break; }
                        }
                    }
                    else journal.Rows.AddRange(rows);
                    journal.Prepared = true;
                    // 预扣仅用于覆盖本次失败/崩溃；成功只归还本次预扣，不清除此前任何失败。
                    journal.PrepareAttempts--; Save(record, journal); return true;
                }
                reason = "反作弊线程准入停止：阶段=snapshot-budget-exhausted；保留恢复日志"; return false;
            }
            catch (Exception error) { return Error("prepare", error, out reason); }
            finally { ReleaseUnrecorded(journal); }
        }
        // 单独记录可能连带改变线程的原生调用意图；仅准备快照尚不能证明本方已经写入。
        internal static bool MarkIndirectWrite(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null;
            try
            {
                if (!PrepareMaintenance(record, process, out reason)) return false;
                Journal journal = Load(record, false);
                if (journal == null || !journal.Prepared || journal.Recovering) { reason = "反作弊进程写入暂停：阶段=indirect-journal-not-active"; return false; }
                ulong processMask; bool gone;
                if (!ProcessKnown(record, process, out processMask, out gone, out reason) || gone || (journal.Desired & ~journal.Original) != 0)
                { if (reason == null) reason = "反作弊进程写入暂停：阶段=indirect-target-range-conflict"; return false; }
                foreach (Row row in journal.Rows)
                {
                    if (row.Terminal) continue;
                    Fact fact;
                    if (!Read(journal, row, out fact, out reason)) return false;
                    if (!fact.Gone && !FreshThreadAllowed(journal, row, fact, processMask, out reason)) return false;
                    if (!fact.Gone && (fact.Group != 0 || (!row.Acquired && (fact.Mask & ~journal.Original) != 0)))
                    { reason = "反作弊进程写入暂停：阶段=indirect-thread-range:TID=" + row.Tid; return false; }
                }
                journal.IndirectIntent = true; Save(record, journal); return true;
            }
            catch (Exception error) { return Error("mark-indirect-write", error, out reason); }
        }
        // 任何会间接改变全部线程的进程/Job写入前，必须先把新成员纳入同一持久日志。
        internal static bool PrepareMaintenance(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null; Journal journal = null;
            try
            {
                journal = Load(record, false);
                if (journal == null || !journal.Prepared || journal.Recovering) { reason = "反作弊进程写入暂停：阶段=thread-journal-not-active"; return false; }
                ulong processMask; bool gone;
                if (!ProcessKnown(record, process, out processMask, out gone, out reason) || gone) return false;
                HashSet<uint> ids;
                if (!Ids(record.Pid, out ids, out reason))
                {
                    if (journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                    return false;
                }
                foreach (uint tid in ids)
                {
                    bool exists = false;
                    foreach (Row known in journal.Rows)
                    {
                        if (known.Tid != tid || known.Terminal) continue;
                        if (!known.Acquired && journal.NewFailures >= MaximumFailures)
                        { reason = "反作弊新线程准入停止：阶段=new-thread-failure-budget:TID=" + tid; return false; }
                        Fact current;
                        if (!Read(journal, known, out current, out reason))
                        {
                            if (!known.Acquired && journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                            return false;
                        }
                        if (!current.Gone && !FreshThreadAllowed(journal, known, current, processMask, out reason))
                        {
                            if (journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                            return false;
                        }
                        if (!current.Gone) { exists = true; break; }
                        known.Phase = "gone"; Save(record, journal);
                    }
                    if (exists) continue;
                    if (journal.NewThreads >= MaximumNewThreads || journal.NewFailures >= MaximumFailures || journal.Rows.Count >= MaximumThreads)
                    { reason = "反作弊进程写入暂停：阶段=new-thread-admission-budget；保留已接管目标"; return false; }
                    journal.NewThreads++; Save(record, journal);
                    var row = new Row { Tid = tid }; Fact fact;
                    if (!Read(journal, row, out fact, out reason)) { journal.NewFailures++; Save(record, journal); return false; }
                    if (fact.Gone) continue;
                    if (!FreshThreadAllowed(journal, row, fact, processMask, out reason))
                    { journal.NewFailures++; Save(record, journal); return false; }
                    if ((fact.Mask & ~record.Original) != 0)
                    { journal.NewFailures++; Save(record, journal); reason = "反作弊进程写入暂停：阶段=new-thread-original-range:TID=" + tid; return false; }
                    row.OriginalGroup = fact.Group; row.Original = fact.Mask; CaptureThreadCpuSets(row, fact); journal.Rows.Add(row); Save(record, journal);
                }
                HashSet<uint> final;
                if (!Ids(record.Pid, out final, out reason) || !ids.SetEquals(final))
                {
                    if (journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                    if (reason == null) reason = "反作弊进程写入暂停：阶段=thread-set-changed";
                    return false;
                }
                ReleaseTerminal(journal); return true;
            }
            catch (Exception error) { return Error("prepare-maintenance", error, out reason); }
            finally { ReleaseUnrecorded(journal); }
        }
        private static bool Write(GameAffinityJobRecord record, Journal journal, Row row, IntPtr process, Fact before, ulong target, string phase, out string reason)
        {
            reason = null; ulong processMask; bool gone;
            bool forward = phase != "restore-intent";
            if (!ProcessKnown(record, process, out processMask, out gone, out reason, forward) || gone || target == 0 || (target & ~processMask) != 0)
            { if (reason == null) reason = "反作弊线程写入暂停：阶段=effective-process-range:TID=" + row.Tid; return false; }
            row.Phase = phase; Save(record, journal);
            Fact preflight;
            if (!Read(journal, row, out preflight, out reason, forward) || preflight.Gone || preflight.Group != before.Group || preflight.Mask != before.Mask)
            { if (reason == null) reason = "反作弊线程写入暂停：阶段=thread-prewrite-drift:TID=" + row.Tid; return false; }
            if (!ProcessKnown(record, process, out processMask, out gone, out reason, forward) || gone || (target & ~processMask) != 0) return false;
            if (forward && !FreshThreadAllowed(journal, row, preflight, processMask, out reason)) return false;
            var affinity = new GroupAffinity { Group = 0, Mask = new UIntPtr(target) }; GroupAffinity previous;
            if (!SetThreadGroupAffinity(preflight.Handle, ref affinity, out previous)) return Failure("SetThreadGroupAffinity:TID=" + row.Tid, out reason);
            row.PreviousKnown = true; row.PreviousGroup = previous.Group; row.Previous = previous.Mask.ToUInt64(); Save(record, journal);
            Fact after;
            if (!Read(journal, row, out after, out reason, forward) || after.Gone || after.Group != 0 || after.Mask != target)
            { if (reason == null) reason = "反作弊线程读回漂移：阶段=thread-readback:TID=" + row.Tid; return false; }
            if (previous.Group != preflight.Group || previous.Mask.ToUInt64() != preflight.Mask)
            { reason = "反作弊线程API前值变化：阶段=thread-api-previous:TID=" + row.Tid; return false; }
            return true;
        }
        internal static bool Apply(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null;
            try
            {
                Journal journal = Load(record, false);
                if (journal == null || !journal.Prepared || journal.Recovering) { reason = "反作弊线程接管暂停：阶段=journal-not-prepared"; return false; }
                ulong mask; bool gone;
                if (!ProcessKnown(record, process, out mask, out gone, out reason) || gone || mask != record.Desired)
                { if (reason == null) reason = "反作弊线程接管暂停：阶段=applied-process-range"; return false; }
                bool complete = true; string firstFailure = null;
                foreach (Row row in journal.Rows)
                {
                    if (row.Terminal) continue;
                    if (!row.Acquired && journal.Acquired && journal.NewFailures >= MaximumFailures)
                    {
                        complete = false;
                        if (firstFailure == null) firstFailure = "反作弊新线程准入停止：阶段=new-thread-failure-budget:TID=" + row.Tid;
                        continue;
                    }
                    Fact fact;
                    if (!Read(journal, row, out fact, out reason))
                    {
                        if (row.Acquired || !journal.Acquired) return false;
                        if (journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                        complete = false; if (firstFailure == null) firstFailure = reason; continue;
                    }
                    if (fact.Gone) { row.Phase = "gone"; Save(record, journal); continue; }
                    if (!row.Acquired && row.Failures >= MaximumFailures)
                    {
                        reason = "反作弊线程首次接管预算耗尽：TID=" + row.Tid;
                        if (!journal.Acquired) return false;
                        complete = false; if (firstFailure == null) firstFailure = reason; continue;
                    }
                    if (!FreshThreadAllowed(journal, row, fact, mask, out reason))
                    {
                        if (!journal.Acquired) return false;
                        if (journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                        complete = false; if (firstFailure == null) firstFailure = reason; continue;
                    }
                    if (!row.Acquired || fact.Mask != record.Desired)
                    {
                        bool firstAcquisition = !row.Acquired;
                        if (firstAcquisition) { row.Failures++; Save(record, journal); }
                        if (!Write(record, journal, row, process, fact, record.Desired, "intent", out reason))
                        {
                            if (!row.Acquired && journal.Acquired && journal.NewFailures < MaximumFailures) { journal.NewFailures++; Save(record, journal); }
                            complete = false; if (firstFailure == null) firstFailure = reason;
                            if (!journal.Acquired) break;
                            continue;
                        }
                        if (firstAcquisition) row.Failures--;
                        row.Phase = "applied"; row.Acquired = true; Save(record, journal);
                        Trace(journal, row, "反作弊线程亲和性已纠正并读回");
                    }
                    else if (row.Phase != "applied") { row.Phase = "applied"; Save(record, journal); }
                }
                if (!complete) { reason = firstFailure; return false; }
                if (!journal.Acquired) { journal.Acquired = true; Save(record, journal); }
                ReleaseTerminal(journal); return AdmitNew(record, process, journal, out reason);
            }
            catch (Exception error) { return Error("apply", error, out reason); }
        }
        internal static bool Maintain(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null;
            try
            {
                Journal journal = Load(record, false);
                if (journal == null || !journal.Acquired || journal.Recovering) { reason = "反作弊线程维护暂停：阶段=not-acquired"; return false; }
                return Apply(record, process, out reason);
            }
            catch (Exception error) { return Error("maintain", error, out reason); }
        }
        private static bool AdmitNew(GameAffinityJobRecord record, IntPtr process, Journal journal, out string reason)
        {
            reason = null;
            if (journal.NewThreads >= MaximumNewThreads || journal.NewFailures >= MaximumFailures) return true;
            ulong processMask; bool gone;
            if (!ProcessKnown(record, process, out processMask, out gone, out reason) || gone) return false;
            HashSet<uint> ids;
            if (!Ids(record.Pid, out ids, out reason)) { journal.NewFailures++; Save(record, journal); return false; }
            foreach (uint tid in ids)
            {
                bool exists = false;
                foreach (Row known in journal.Rows) if (known.Tid == tid && !known.Terminal) { exists = true; break; }
                if (exists) continue;
                if (journal.NewThreads >= MaximumNewThreads || journal.Rows.Count >= MaximumThreads) return true;
                // 准入次数在读线程前落盘；读取失败、TID复用和线程重建都不会退还次数。
                journal.NewThreads++; Save(record, journal);
                var row = new Row { Tid = tid }; Fact fact;
                if (!Read(journal, row, out fact, out reason)) { journal.NewFailures++; Save(record, journal); return false; }
                bool seen = false;
                foreach (Row prior in journal.Rows) if (prior.Tid == row.Tid && prior.Birth == row.Birth) { seen = true; break; }
                if (seen || fact.Gone) continue;
                if (!FreshThreadAllowed(journal, row, fact, processMask, out reason)) { journal.NewFailures++; Save(record, journal); return false; }
                if ((fact.Mask & ~record.Original) != 0) { journal.NewFailures++; Save(record, journal); reason = "反作弊新线程准入拒绝：阶段=original-range:TID=" + tid; return false; }
                row.OriginalGroup = fact.Group; row.Original = fact.Mask; CaptureThreadCpuSets(row, fact); journal.Rows.Add(row); Save(record, journal);
                if (!Write(record, journal, row, process, fact, record.Desired, "intent", out reason)) { journal.NewFailures++; Save(record, journal); return false; }
                row.Phase = "applied"; row.Acquired = true; Save(record, journal);
            }
            return true;
        }
        internal static bool Verify(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null;
            try
            {
                Journal journal = Load(record, false);
                if (journal == null || !journal.Prepared || !journal.Acquired || journal.Recovering) { reason = "反作弊线程核验暂停：阶段=not-acquired"; return false; }
                ulong mask; bool gone;
                if (!ProcessKnown(record, process, out mask, out gone, out reason) || gone || mask != journal.Desired) return false;
                foreach (Row row in journal.Rows)
                {
                    if (row.Terminal) continue;
                    Fact fact; if (!Read(journal, row, out fact, out reason)) return false;
                    if (fact.Gone) continue;
                    if (!row.Acquired) { reason = "反作弊线程核验待确认：阶段=thread-not-acquired:TID=" + row.Tid; return false; }
                    if (fact.Group != 0 || fact.Mask != journal.Desired)
                    { reason = "反作弊线程读回漂移：TID=" + row.Tid + " 当前=0x" + fact.Mask.ToString("X") + " 目标=0x" + journal.Desired.ToString("X"); return false; }
                }
                HashSet<uint> ids;
                if (!Ids(record.Pid, out ids, out reason)) return false;
                foreach (uint tid in ids)
                {
                    bool admitted = false;
                    foreach (Row row in journal.Rows)
                    {
                        if (row.Tid != tid || row.Terminal || !row.Acquired) continue;
                        Fact fact;
                        if (!Read(journal, row, out fact, out reason)) return false;
                        if (!fact.Gone) { admitted = true; break; }
                    }
                    if (!admitted) { reason = "反作弊线程覆盖待确认：阶段=new-thread-not-admitted:TID=" + tid; return false; }
                }
                return true;
            }
            catch (Exception error) { return Error("verify", error, out reason); }
        }
        internal static bool CaptureRecovery(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null; Journal journal = null;
            try
            {
                journal = Load(record, false);
                if (journal == null || !journal.Prepared) return true;
                if (journal.Recovering) return true;
                bool hasWriteIntent = journal.IndirectIntent;
                foreach (Row row in journal.Rows) hasWriteIntent |= !row.Terminal && row.Phase != "prepared";
                if (!hasWriteIntent)
                {
                    foreach (Row row in journal.Rows) if (!row.Terminal) row.Phase = "not-applied";
                    journal.Recovering = true; Save(record, journal); ReleaseTerminal(journal); return true;
                }
                ulong mask; bool gone;
                if (!ProcessKnown(record, process, out mask, out gone, out reason, false)) return false;
                if (gone)
                {
                    foreach (Row row in journal.Rows) if (!row.Terminal) row.Phase = "gone";
                    journal.Recovering = true; Save(record, journal); ReleaseTerminal(journal); return true;
                }
                // 恢复进程范围也会连带改变未准入的新线程；仅为恢复冻结它们的外部值，不授予前向写入资格。
                while (journal.RecoverySnapshots < 3)
                {
                    journal.RecoverySnapshots++; Save(record, journal);
                    HashSet<uint> before, after;
                    if (!Ids(record.Pid, out before, out reason)) return false;
                    var candidates = new List<Row>();
                    var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
                    int additional = 0; bool unstable = false;
                    foreach (uint tid in before)
                    {
                        var observed = new Row { Tid = tid }; Fact fact;
                        if (!Read(journal, observed, out fact, out reason, false)) return false;
                        if (fact.Gone) { unstable = true; break; }
                        Row captured = null;
                        foreach (Row row in journal.Rows)
                            if (row.Tid == observed.Tid && row.Birth == observed.Birth) { captured = row; break; }
                        if (captured == null)
                        {
                            if (journal.Rows.Count + ++additional > MaximumThreads)
                            { reason = "反作弊线程恢复待确认：阶段=recovery-thread-count-budget；保留日志和句柄"; return false; }
                            captured = observed;
                        }
                        candidates.Add(captured); facts.Add(HandleKey(journal, captured), fact);
                    }
                    if (unstable) continue;
                    if (!Ids(record.Pid, out after, out reason)) return false;
                    if (!before.SetEquals(after)) continue;
                    foreach (Row row in candidates)
                    {
                        Fact current; Fact captured = facts[HandleKey(journal, row)];
                        if (!Read(journal, row, out current, out reason, false)) return false;
                        if (current.Gone || current.Group != captured.Group || current.Mask != captured.Mask) { unstable = true; break; }
                    }
                    if (unstable) continue;
                    ulong finalMask;
                    if (!ProcessKnown(record, process, out finalMask, out gone, out reason, false) || gone || finalMask != mask)
                    { if (reason == null) reason = "反作弊线程恢复待确认：阶段=recovery-process-scope-changed"; return false; }
                    foreach (Row row in journal.Rows)
                        if (!row.Terminal && !facts.ContainsKey(HandleKey(journal, row))) row.Phase = "gone";
                    foreach (Row row in candidates)
                    {
                        Fact fact = facts[HandleKey(journal, row)];
                        bool existing = journal.Rows.Contains(row);
                        if (!existing || row.Terminal)
                        {
                            row.RecoveryOnly = true; row.Acquired = false; row.PreviousKnown = false; row.Previous = 0;
                            row.OriginalGroup = fact.Group; row.Original = fact.Mask; CaptureThreadCpuSets(row, fact); row.Phase = "prepared";
                            if (!existing) journal.Rows.Add(row);
                        }
                        row.RecoveryCaptured = true;
                        row.RecoveryOwned = !row.RecoveryOnly && fact.Group == 0 && fact.Mask == journal.Desired;
                        row.RecoveryGroup = fact.Group; row.RecoveryMask = fact.Mask;
                    }
                    journal.Recovering = true; Save(record, journal); ReleaseTerminal(journal); return true;
                }
                reason = "反作弊线程恢复待确认：阶段=recovery-snapshot-budget-exhausted；保留日志和句柄"; return false;
            }
            catch (Exception error) { return Error("capture-recovery", error, out reason); }
            finally { ReleaseUnrecorded(journal); }
        }
        internal static bool Restore(GameAffinityJobRecord record, IntPtr process, out string reason)
        {
            reason = null;
            try
            {
                Journal journal = Load(record, false);
                if (journal == null || !journal.Prepared) return true;
                if (!journal.Recovering) { reason = "反作弊线程恢复暂停：阶段=recovery-not-captured"; return false; }
                ulong processMask; bool gone;
                if (!ProcessKnown(record, process, out processMask, out gone, out reason, false)) return false;
                foreach (Row row in journal.Rows)
                {
                    if (row.Terminal) continue;
                    Fact fact = null;
                    if (!gone && !Read(journal, row, out fact, out reason, false)) return false;
                    if (gone || fact.Gone) { row.Phase = "gone"; Save(record, journal); continue; }
                    ulong target = row.RecoveryOwned ? row.Original : row.RecoveryMask;
                    string terminal = row.RecoveryOwned ? "restored" : "external";
                    if (!row.RecoveryCaptured || target == 0 || (target & ~processMask) != 0)
                    { reason = "反作弊线程恢复待确认：阶段=original-range-conflict:TID=" + row.Tid; return false; }
                    if (fact.Group == 0 && fact.Mask == target) { row.Phase = terminal; Save(record, journal); continue; }
                    // 解除本方进程限制后的预期值只有本方目标或原进程范围。其他新值视为后来外改，不能覆盖。
                    if (fact.Group != 0 || (fact.Mask != journal.Desired && fact.Mask != journal.Original && fact.Mask != row.RecoveryMask))
                    { row.Phase = "external"; Save(record, journal); Trace(journal, row, "反作弊线程恢复已保留后续外部范围"); continue; }
                    if (!Write(record, journal, row, process, fact, target, "restore-intent", out reason)) return false;
                    row.Phase = terminal; Save(record, journal);
                    Trace(journal, row, terminal == "restored" ? "反作弊线程原范围已恢复并读回" : "反作弊线程恢复已保留外部范围");
                }
                ReleaseTerminal(journal); return true;
            }
            catch (Exception error) { return Error("restore", error, out reason); }
        }
        internal static bool HasPending(GameAffinityJobRecord record, out bool pending, out string reason)
        {
            pending = true; reason = null;
            try
            {
                Journal journal = Load(record, false);
                if (journal == null || !journal.Prepared) { pending = false; return true; }
                foreach (Row row in journal.Rows) if (!row.Terminal) return true;
                pending = false; return true;
            }
            catch (Exception error) { return Error("pending", error, out reason); }
        }
        private static void Trace(Journal journal, Row row, string message)
        {
            string key = journal.Name + ":" + message; long now = DateTime.UtcNow.Ticks, previous;
            if (logTimes.TryGetValue(key, out previous) && now - previous < TimeSpan.FromSeconds(30).Ticks) return;
            logTimes[key] = now;
            Logger.Info(message + "：单条已核验线程 PID=" + journal.Pid + "/" + journal.Birth + " TID=" + row.Tid + "/" + row.Birth
                + " 原范围=0x" + row.Original.ToString("X") + " 目标=0x" + journal.Desired.ToString("X") + " 阶段=" + row.Phase);
        }
    }
}
