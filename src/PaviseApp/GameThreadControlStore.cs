using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PaviseApp
{
    internal sealed class GameThreadControlStore : IGameThreadControlStore
    {
        private readonly string path;
        private readonly HardThreadTarget target;
        private readonly string nonce, scopeHash;
        private readonly Action requireSource;
        private FileStream writer;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Pavise.GameThreadControl.V1");

        internal GameThreadControlStore(string root, HardThreadTarget target, string nonce, string scopeHash, Action requireSource)
        {
            if (!Path.IsPathRooted(root) || !Digest(nonce) || !Digest(scopeHash) || requireSource == null)
                throw new ArgumentException("统一线程账本缺少原守护范围");
            this.target = target; this.nonce = nonce; this.scopeHash = scopeHash; this.requireSource = requireSource;
            path = Path.Combine(Path.GetFullPath(root), "game-thread-control-" + target.SessionId.ToString("N") + ".xml");
            requireSource(); NoReparse(path);
            string lockPath = Path.ChangeExtension(path, ".lock"); NoReparse(lockPath);
            writer = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static bool Digest(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char item in value) if (!(item >= '0' && item <= '9') && !(item >= 'a' && item <= 'f')) return false;
            return true;
        }

        private static void NoReparse(string path)
        {
            for (DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(path)); directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("统一线程账本拒绝重解析目录");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("统一线程账本拒绝重解析文件");
        }

        public GameThreadControlJournal Load()
        {
            if (writer == null) throw new ObjectDisposedException("统一线程账本");
            requireSource(); NoReparse(path);
            if (!File.Exists(path)) return new GameThreadControlJournal { Target = target, Nonce = nonce, ScopeHash = scopeHash };
            return Read(path, target, nonce, scopeHash);
        }

        private static byte[] Payload(GameThreadControlJournal journal)
        {
            ValidatePriorityAuthority(journal);
            if (journal.Target.AllowedMask == 0 && journal.Rows.Exists(row => row.Ideal.Managed))
                throw new InvalidDataException("仅优先级目标不得保存理想处理器归属");
            using (var bytes = new MemoryStream())
            {
                using (XmlWriter xml = XmlWriter.Create(bytes, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false }))
                {
                    xml.WriteStartElement("gameThreadControl");
                    Attribute(xml, "version", 1); Attribute(xml, "session", journal.Target.SessionId.ToString("N"));
                    Attribute(xml, "pid", journal.Target.ProcessId); Attribute(xml, "birth", journal.Target.ProcessCreation);
                    Attribute(xml, "path", journal.Target.ExecutablePath); Attribute(xml, "allowed", journal.Target.AllowedMask);
                    Attribute(xml, "nonce", journal.Nonce); Attribute(xml, "scope", journal.ScopeHash);
                    if (!string.IsNullOrEmpty(journal.Rule)) Attribute(xml, "rule", journal.Rule);
                    Attribute(xml, "revision", journal.Revision); Attribute(xml, "stopping", journal.Stopping ? 1 : 0);
                    Attribute(xml, "recoveryPasses", journal.RecoveryPasses); Attribute(xml, "exitRecoveryPasses", journal.ExitRecoveryPasses);
                    Attribute(xml, "originalClass", journal.OriginalProcessClass); Attribute(xml, "observedClass", journal.ObservedProcessClass);
                    Attribute(xml, "processObserved", journal.ProcessObservedMilliseconds); Attribute(xml, "processKnown", journal.ProcessObservedKnown ? 1 : 0);
                    foreach (GameThreadControlRow row in journal.Rows)
                    {
                        xml.WriteStartElement("thread"); Attribute(xml, "tid", row.Thread.Tid); Attribute(xml, "birth", row.Thread.Creation);
                        Attribute(xml, "name", row.Name ?? "");
                        Attribute(xml, "priorityLimit", row.PriorityLimit);
                        WriteField(xml, "priority", row.Priority); WriteField(xml, "ideal", row.Ideal);
                        xml.WriteEndElement();
                    }
                    xml.WriteEndElement();
                }
                return bytes.ToArray();
            }
        }

        private static void Attribute(XmlWriter xml, string name, object value)
        { xml.WriteAttributeString(name, Convert.ToString(value, CultureInfo.InvariantCulture)); }

        private static void WriteField(XmlWriter xml, string name, GameThreadControlField field)
        {
            xml.WriteStartElement(name);
            Attribute(xml, "managed", field.Managed ? 1 : 0); Attribute(xml, "writtenKnown", field.HasWritten ? 1 : 0);
            Attribute(xml, "appliedKnown", field.AppliedKnown ? 1 : 0); Attribute(xml, "previousKnown", field.PreviousKnown ? 1 : 0);
            Attribute(xml, "admitted", field.InitialAdmitted ? 1 : 0); Attribute(xml, "terminal", field.Terminal ? 1 : 0);
            Attribute(xml, "original", field.Original); Attribute(xml, "desired", field.Desired); Attribute(xml, "applied", field.Applied);
            Attribute(xml, "observed", field.Observed); Attribute(xml, "observedKnown", field.ObservedKnown ? 1 : 0);
            Attribute(xml, "previous", field.Previous); Attribute(xml, "written", field.Written);
            Attribute(xml, "admissionFailures", field.AdmissionFailures); Attribute(xml, "restoreAttempts", field.RestoreAttempts);
            Attribute(xml, "phase", (int)field.Phase); Attribute(xml, "restoreValue", field.RestoreValue);
            Attribute(xml, "restoreValueKnown", field.RestoreValueKnown ? 1 : 0); Attribute(xml, "detail", field.Detail ?? "");
            Attribute(xml, "lastChecked", field.LastCheckedMilliseconds); Attribute(xml, "lastObserved", field.LastObservedMilliseconds);
            Attribute(xml, "lastVerified", field.LastVerifiedMilliseconds); Attribute(xml, "runtimeAttempts", field.RuntimeAttempts); Attribute(xml, "corrections", field.Corrections);
            Attribute(xml, "desiredClass", field.DesiredProcessClass);
            Attribute(xml, "policyVerified", field.PolicyVerified ? 1 : 0);
            xml.WriteEndElement();
        }

        private static long Number(XmlElement xml, string name) { return long.Parse(xml.GetAttribute(name), CultureInfo.InvariantCulture); }
        private static int Integer(XmlElement xml, string name) { return checked((int)Number(xml, name)); }
        private static bool Flag(XmlElement xml, string name)
        { long value = Number(xml, name); if (value != 0 && value != 1) throw new InvalidDataException("统一线程布尔字段无效"); return value == 1; }

        private static int ReadPriorityLimit(XmlElement xml)
        {
            int value = Integer(xml, "priorityLimit");
            if (!GameThreadControlPolicy.ValidPriorityLimit(value)) throw new InvalidDataException("统一线程角色优先级上限无效");
            return value;
        }

        private static void ValidatePriorityAuthority(GameThreadControlJournal journal)
        {
            bool timeCritical = GameThreadControlPolicy.TimeCriticalRule(journal.Rule);
            if (!string.IsNullOrEmpty(journal.Rule) && !timeCritical)
                throw new InvalidDataException("统一线程时间关键规则无效");
            if (timeCritical && (journal.Target.AllowedMask == 0 || journal.Rows.Count > 1))
                throw new InvalidDataException("时间关键账本必须属于单角色亲和性授权");
            foreach (GameThreadControlRow row in journal.Rows)
            {
                if (!GameThreadControlPolicy.ValidPriorityLimit(row.PriorityLimit)
                    || (row.PriorityLimit == 15) != timeCritical || timeCritical && row.Ideal.Managed)
                    throw new InvalidDataException("线程优先级上限与原规则不一致");
                if (timeCritical && row.Priority.Managed && (row.Priority.Original < -2 || row.Priority.Original > 2))
                    throw new InvalidDataException("时间关键账本不得接管游戏原有特殊优先级");
            }
        }

        private static GameThreadControlField ReadField(XmlElement xml, bool priority)
        {
            if (xml == null || xml.HasChildNodes) throw new InvalidDataException("统一线程属性格式无效");
            var field = new GameThreadControlField { Managed = Flag(xml, "managed"), HasWritten = Flag(xml, "writtenKnown"),
                AppliedKnown = Flag(xml, "appliedKnown"), PreviousKnown = Flag(xml, "previousKnown"),
                InitialAdmitted = Flag(xml, "admitted"), Terminal = Flag(xml, "terminal"), Original = Integer(xml, "original"),
                Desired = Integer(xml, "desired"), Applied = Integer(xml, "applied"), Observed = Integer(xml, "observed"),
                ObservedKnown = Flag(xml, "observedKnown"), Previous = Integer(xml, "previous"), Written = Integer(xml, "written"),
                AdmissionFailures = Integer(xml, "admissionFailures"), RestoreAttempts = Integer(xml, "restoreAttempts"),
                Phase = (GameThreadControlPhase)Integer(xml, "phase"), RestoreValue = Integer(xml, "restoreValue"),
                RestoreValueKnown = Flag(xml, "restoreValueKnown"), Detail = xml.GetAttribute("detail"),
                LastCheckedMilliseconds = Number(xml, "lastChecked"), LastObservedMilliseconds = Number(xml, "lastObserved"),
                LastVerifiedMilliseconds = Number(xml, "lastVerified"), RuntimeAttempts = Number(xml, "runtimeAttempts"), Corrections = Number(xml, "corrections"),
                DesiredProcessClass = checked((uint)Number(xml, "desiredClass")), PolicyVerified = Flag(xml, "policyVerified") };
            int minimum = priority ? -15 : 0, maximum = priority ? 15 : 0xFFFFFF;
            foreach (int value in new[] { field.Original, field.Desired, field.Applied, field.Previous, field.Written, field.RestoreValue })
                if (value < minimum || value > maximum) throw new InvalidDataException("统一线程属性值越界");
            if (!Enum.IsDefined(typeof(GameThreadControlPhase), field.Phase) || field.AdmissionFailures < 0 || field.AdmissionFailures > 3
                || field.RestoreAttempts < 0 || field.RestoreAttempts > 3 || field.Detail.Length > 2048
                || field.RuntimeAttempts < 0 || field.Corrections < 0 || field.LastCheckedMilliseconds < 0 || field.LastObservedMilliseconds < 0 || field.LastVerifiedMilliseconds < 0)
                throw new InvalidDataException("统一线程阶段或有限预算无效");
            if (priority && field.PreviousKnown) throw new InvalidDataException("优先级 API 不提供原子前值");
            return field;
        }

        private static GameThreadControlJournal Read(string path, HardThreadTarget expected, string nonce, string scopeHash)
        {
            NoReparse(path);
            byte[] sealedBytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 1 || stream.Length > 2097152) throw new InvalidDataException("统一线程账本超过读取预算");
                sealedBytes = new byte[(int)stream.Length]; int offset = 0;
                while (offset < sealedBytes.Length) { int read = stream.Read(sealedBytes, offset, sealedBytes.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
            }
            byte[] payload = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.LocalMachine);
            if (payload.Length > 1048576) throw new InvalidDataException("统一线程解封内容超过预算");
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new MemoryStream(payload), new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 1048576 })) document.Load(reader);
            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "gameThreadControl" || root.GetAttribute("version") != "1") throw new InvalidDataException("统一线程账本格式无效");
            var target = new HardThreadTarget(Guid.ParseExact(root.GetAttribute("session"), "N"), Integer(root, "pid"), Number(root, "birth"), root.GetAttribute("path"), ulong.Parse(root.GetAttribute("allowed"), CultureInfo.InvariantCulture));
            string actualNonce = root.GetAttribute("nonce"), actualScope = root.GetAttribute("scope");
            if (expected != null && !expected.Same(target) || nonce != null && actualNonce != nonce || scopeHash != null && actualScope != scopeHash
                || !Digest(actualNonce) || !Digest(actualScope)
                || Path.GetFileName(path) != "game-thread-control-" + target.SessionId.ToString("N") + ".xml") throw new InvalidDataException("统一线程账本不属于原守护范围");
            var journal = new GameThreadControlJournal { Target = target, Nonce = actualNonce, ScopeHash = actualScope, Revision = Number(root, "revision"), Stopping = Flag(root, "stopping"),
                RecoveryPasses = Integer(root, "recoveryPasses"), ExitRecoveryPasses = Integer(root, "exitRecoveryPasses"), Rule = root.GetAttribute("rule") };
            journal.OriginalProcessClass = checked((uint)Number(root, "originalClass")); journal.ObservedProcessClass = checked((uint)Number(root, "observedClass"));
            journal.ProcessObservedMilliseconds = Number(root, "processObserved"); journal.ProcessObservedKnown = Flag(root, "processKnown");
            if (journal.Revision <= 0) throw new InvalidDataException("统一线程账本版本无效");
            if (journal.RecoveryPasses < 0 || journal.RecoveryPasses > 3 || journal.ExitRecoveryPasses < 0 || journal.ExitRecoveryPasses > 3)
                throw new InvalidDataException("统一线程恢复阶段超过有限预算");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in root.ChildNodes)
            {
                XmlElement element = node as XmlElement;
                if (element == null || element.Name != "thread" || element.ChildNodes.Count != 2 || journal.Rows.Count >= 512)
                    throw new InvalidDataException("统一线程成员格式或数量无效");
                var identity = new HardThreadIdentity(Integer(element, "tid"), Number(element, "birth"));
                if (identity.Tid <= 0 || identity.Creation < target.ProcessCreation || !keys.Add(identity.Key)) throw new InvalidDataException("统一线程成员身份冲突");
                var row = new GameThreadControlRow { Thread = identity, Name = element.GetAttribute("name"),
                    PriorityLimit = element.HasAttribute("priorityLimit") ? ReadPriorityLimit(element) : 2,
                    Priority = ReadField(element["priority"], true), Ideal = ReadField(element["ideal"], false) };
                if (target.AllowedMask == 0 && row.Ideal.Managed)
                    throw new InvalidDataException("仅优先级目标包含越权的理想处理器归属");
                journal.Rows.Add(row);
            }
            ValidatePriorityAuthority(journal);
            return journal;
        }

        public void Save(long expectedRevision, GameThreadControlJournal journal)
        {
            GameThreadControlJournal current = Load();
            if (current.Revision != expectedRevision || !journal.Target.Same(target) || journal.Nonce != nonce || journal.ScopeHash != scopeHash
                || current.Revision != 0 && current.Rule != journal.Rule
                || journal.Revision != checked(expectedRevision + 1)) throw new InvalidDataException("统一线程账本写入代次不匹配");
            byte[] payload = Payload(journal);
            if (payload.Length > 1048576) throw new InvalidDataException("统一线程账本超过写入预算");
            byte[] bytes = ProtectedData.Protect(payload, Entropy, DataProtectionScope.LocalMachine);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            requireSource(); NoReparse(path);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            GameThreadControlJournal readback = Load();
            if (!Equal(Payload(readback), payload)) throw new IOException("统一线程账本实际读回未确认");
        }

        private static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }

        internal static bool HasDebt(string root, HardThreadTarget target, string nonce, string scopeHash, bool declared = false)
        {
            string path = Path.Combine(root, "game-thread-control-" + target.SessionId.ToString("N") + ".xml");
            if (!File.Exists(path)) return declared;
            try
            {
                GameThreadControlJournal journal = Read(path, target, nonce, scopeHash);
                return GameThreadControlPolicy.Pending(journal) || declared && !journal.Stopping;
            }
            catch { return true; }
        }

        internal static bool HasRecord(string root, HardThreadTarget target)
        {
            string path = Path.Combine(root, "game-thread-control-" + target.SessionId.ToString("N") + ".xml");
            return File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".lock"));
        }

        internal static string SettledHash(string root, HardThreadTarget target, string nonce, string scopeHash, bool declared)
        {
            if (!declared) return null;
            string path = Path.Combine(root, "game-thread-control-" + target.SessionId.ToString("N") + ".xml");
            GameThreadControlJournal journal = Read(path, target, nonce, scopeHash);
            if (!journal.Stopping || GameThreadControlPolicy.Pending(journal)) throw new InvalidDataException("已声明的统一线程账本尚未结清");
            return HashFile(path);
        }

        internal static void RequireDeclaredSource(string root, HardThreadTarget target, string nonce, string scopeHash)
        { Read(Path.Combine(root, "game-thread-control-" + target.SessionId.ToString("N") + ".xml"), target, nonce, scopeHash); }

        internal static bool MatchesSettledHash(string root, HardThreadTarget target, string expected)
        {
            string path = Path.Combine(root, "game-thread-control-" + target.SessionId.ToString("N") + ".xml");
            try
            {
                if (expected == null) return !File.Exists(path) || !GameThreadControlPolicy.Pending(Read(path, target, null, null));
                if (!File.Exists(path) || HashFile(path) != expected) return false;
                GameThreadControlJournal journal = Read(path, target, null, null);
                return journal.Stopping && !GameThreadControlPolicy.Pending(journal);
            }
            catch { return false; }
        }

        private static string HashFile(string path)
        {
            NoReparse(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // 仅由已持有原目标退出证明的守护调用；损坏源保留归档，不把缺失收据猜成已恢复。
        internal static void SettleGone(string root, HardThreadTarget target, string nonce, string scopeHash, Action requireGone)
        {
            requireGone();
            using (var store = new GameThreadControlStore(root, target, nonce, scopeHash, requireGone))
            {
                try { store.Load(); }
                catch
                {
                    NoReparse(store.path); requireGone();
                    if (File.Exists(store.path)) File.Move(store.path, store.path + ".gone-" + Guid.NewGuid().ToString("N") + ".bak");
                }
                requireGone(); MarkGone(store);
            }
        }

        internal static bool CleanupSettled(string root, HardThreadTarget target, string expectedHash, Action requireReleased, Func<string, bool> delete)
        {
            requireReleased();
            string path = Path.Combine(root, "game-thread-control-" + target.SessionId.ToString("N") + ".xml");
            string lockPath = Path.ChangeExtension(path, ".lock");
            NoReparse(path); NoReparse(lockPath);
            if (!File.Exists(path) && !File.Exists(lockPath)) return true;
            using (var pin = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (File.Exists(path))
                {
                    GameThreadControlJournal current = Read(path, target, null, null);
                    if (!current.Stopping && !(expectedHash == null && current.Rows.Count == 0) || GameThreadControlPolicy.Pending(current)
                        || expectedHash != null && HashFile(path) != expectedHash
                        || expectedHash == null && current.Rows.Count != 0) return false;
                    requireReleased();
                    if (!delete(path)) return false;
                }
            }
            requireReleased();
            return !File.Exists(lockPath) || delete(lockPath);
        }

        internal static bool SweepReleased(string root, Action requireRoot, Func<string, bool> delete)
        {
            int count = 0; long bytes = 0; bool complete = true;
            foreach (string path in Directory.EnumerateFiles(root, "game-thread-control-*.xml", SearchOption.TopDirectoryOnly))
            {
                if (++count > 1024 || (bytes = checked(bytes + new FileInfo(path).Length)) > 16777216) return false;
                requireRoot();
                GameThreadControlJournal journal = Read(path, null, null, null);
                if (GameThreadControlPolicy.Pending(journal) || !journal.Stopping && journal.Rows.Count != 0) continue;
                string id = journal.Target.SessionId.ToString("N");
                Action released = delegate
                {
                    requireRoot();
                    if (File.Exists(Path.Combine(root, "hard-thread-" + id + ".xml"))
                        || File.Exists(Path.Combine(root, "hard-thread-" + id + ".lock"))
                        || Directory.Exists(Path.Combine(root, "guards", id))) throw new IOException("原恢复源仍存在，保留统一线程结清记录");
                };
                if (File.Exists(Path.Combine(root, "hard-thread-" + id + ".xml"))
                    || File.Exists(Path.Combine(root, "hard-thread-" + id + ".lock")) || Directory.Exists(Path.Combine(root, "guards", id))) continue;
                string hash = !journal.Stopping && journal.Rows.Count == 0 ? null : HashFile(path);
                if (!CleanupSettled(root, journal.Target, hash, released, delete)) complete = false;
            }
            return complete;
        }

        internal static bool CanonicalRootHasRecoveryDebt(string root)
        {
            if (!Directory.Exists(root)) return false;
            int count = 0; long bytes = 0;
            foreach (string path in Directory.EnumerateFiles(root, "game-thread-control-*.xml", SearchOption.TopDirectoryOnly))
            {
                if (++count > 1024 || (bytes = checked(bytes + new FileInfo(path).Length)) > 16777216)
                    throw new InvalidDataException("统一线程历史账本超过有限读取预算");
                if (GameThreadControlPolicy.Pending(Read(path, null, null, null))) return true;
            }
            return false;
        }

        internal static void MarkGone(IGameThreadControlStore store)
        {
            GameThreadControlJournal journal = store.Load();
            foreach (GameThreadControlRow row in journal.Rows)
                foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal })
                { field.Terminal = true; field.Phase = GameThreadControlPhase.Gone; }
            journal.Stopping = true; long revision = journal.Revision; journal.Revision++;
            store.Save(revision, journal);
        }

        public void Dispose()
        { if (writer != null) { writer.Dispose(); writer = null; } }
    }
}
