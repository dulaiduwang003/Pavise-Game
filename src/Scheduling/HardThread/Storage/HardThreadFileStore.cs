using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PaviseApp
{
    internal sealed class HardThreadFileStore : IHardThreadStore, IDisposable
    {
        private const int MaximumLedgerBytes = 1048576;

        private static readonly Encoding LedgerEncoding = new UTF8Encoding(false);

        private readonly string root;

        private FileStream writer;

        private FileStream rootWriter;

        private bool disposed;

        private Guid session;

        private readonly List<EncodedReceipt> encodedReceipts = new List<EncodedReceipt>();

        private StringBuilder receiptText;

        private XmlWriter receiptEncoder;

        private sealed class BoundedReceiptTextWriter : StringWriter
        {
            internal BoundedReceiptTextWriter(StringBuilder text) : base(text, CultureInfo.InvariantCulture) { }

            private void RequireSpace(int count)
            {
                if (count > MaximumLedgerBytes - GetStringBuilder().Length)
                    throw new InvalidDataException("bounded original journal required");
            }

            public override void Write(char value)
            {
                RequireSpace(1);
                base.Write(value);
            }

            public override void Write(char[] buffer, int index, int count)
            {
                RequireSpace(count);
                base.Write(buffer, index, count);
            }

            public override void Write(string value)
            {
                if (value != null) RequireSpace(value.Length);
                base.Write(value);
            }
        }

        private sealed class BoundedLedgerStream : Stream
        {
            private readonly FileStream output;

            internal BoundedLedgerStream(FileStream output) { this.output = output; }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return output.CanWrite; } }
            public override long Length { get { return output.Length; } }
            public override long Position { get { return output.Position; } set { throw new NotSupportedException(); } }

            public override void Flush() { output.Flush(); }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > MaximumLedgerBytes - output.Position)
                    throw new InvalidDataException("bounded original journal required");
                output.Write(buffer, offset, count);
            }

            public override void WriteByte(byte value)
            {
                if (output.Position >= MaximumLedgerBytes)
                    throw new InvalidDataException("bounded original journal required");
                output.WriteByte(value);
            }
        }

        private sealed class EncodedReceipt
        {
            internal HardThreadReceipt Snapshot;
            internal string Xml;

            internal bool Matches(HardThreadReceipt row)
            {
                HardThreadReceipt saved = Snapshot;
                return saved.Thread.Tid == row.Thread.Tid && saved.Thread.Creation == row.Thread.Creation
                    && saved.Group == row.Group && saved.Captured == row.Captured && saved.Applied == row.Applied
                    && saved.PreviousGroup == row.PreviousGroup && saved.Previous == row.Previous
                    && saved.PreviousKnown == row.PreviousKnown && saved.RestorePreviousGroup == row.RestorePreviousGroup
                    && saved.RestorePrevious == row.RestorePrevious && saved.RestorePreviousKnown == row.RestorePreviousKnown
                    && saved.Phase == row.Phase && string.Equals(saved.Detail ?? "", row.Detail ?? "", StringComparison.Ordinal);
            }
        }

        private sealed class RootWriterBusyException : IOException
        {
            internal RootWriterBusyException(IOException inner) : base("original root writer is active", inner) { }
        }

        private static FileStream OpenObservationWriter(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException error)
            {
                int code = error.HResult & 65535;
                if (code == 32 || code == 33) throw new RootWriterBusyException(error);
                throw;
            }
        }

        internal bool HoldsRootWriter
        {
            get
            {
                if (!disposed)
                {
                    return rootWriter != null;
                }
                return false;
            }
        }

        internal HardThreadFileStore(string originalCanonicalStore)
            : this(originalCanonicalStore, false)
        {
        }

        private HardThreadFileStore(string originalCanonicalStore, bool observeOnly)
        {
            if (string.IsNullOrEmpty(originalCanonicalStore) || !Path.IsPathRooted(originalCanonicalStore))
            {
                throw new ArgumentException("original canonical store required");
            }
            root = Path.GetFullPath(originalCanonicalStore);
            RequireRoot();
            string path = Path.Combine(root, "hard-thread-root.lock");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("reparse root writer refused");
            }
            if (observeOnly)
            {
                rootWriter = OpenObservationWriter(path);
            }
            else
            {
                rootWriter = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
        }

        private void RequireRoot()
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(root);
            if (!directoryInfo.Exists)
            {
                throw new IOException("original store does not exist");
            }
            for (DirectoryInfo directoryInfo2 = directoryInfo; directoryInfo2 != null; directoryInfo2 = directoryInfo2.Parent)
            {
                if ((directoryInfo2.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("reparse store path refused");
                }
            }
        }

        private string Bind(HardThreadTarget target)
        {
            if (disposed || rootWriter == null)
            {
                throw new ObjectDisposedException("original hard thread store");
            }
            RequireRoot();
            if (session != Guid.Empty && session != target.SessionId)
            {
                throw new InvalidOperationException("store belongs to another original session");
            }
            if (writer == null)
            {
                string path = Path.Combine(root, "hard-thread-" + target.SessionId.ToString("N") + ".lock");
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("reparse lock refused");
                }
                writer = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                session = target.SessionId;
            }
            string combinedPath = Path.Combine(root, "hard-thread-" + session.ToString("N") + ".xml");
            if (File.Exists(combinedPath) && (File.GetAttributes(combinedPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("reparse ledger refused");
            }
            return combinedPath;
        }

        public HardThreadJournal Load(HardThreadTarget target)
        {
            string path = Bind(target);
            return ReadLedger(path, target, true);
        }

        internal void RequireForwardAdmission(HardThreadTarget target)
        {
            try
            {
                Bind(target);
                if (HistoricalDebt())
                {
                    throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original historical hard affinity recovery is pending");
                }
            }
            catch (HardThreadOperationException)
            {
                throw;
            }
            catch (Exception ex2)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "historical hard affinity ownership cannot be verified: " + ex2.Message);
            }
        }

        private bool HistoricalDebt()
        {
            if (disposed || rootWriter == null)
            {
                throw new ObjectDisposedException("original root writer");
            }
            RequireRoot();
            int historicalDebtValue = 0;
            long numericValue = 0L;
            foreach (string item in Directory.EnumerateFiles(root, "hard-thread-*.xml", SearchOption.TopDirectoryOnly))
            {
                if (++historicalDebtValue > 1024 || (numericValue = checked(numericValue + new FileInfo(item).Length)) > 16777216)
                {
                    throw new InvalidDataException("historical hard affinity ledger review exceeds the bounded admission budget");
                }
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("historical ledger is a reparse source");
                }
                HardThreadJournal hardThreadJournal = ReadLedger(item, null, false);
                foreach (HardThreadReceipt row in hardThreadJournal.Rows)
                {
                    if (!row.Terminal)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal HardThreadJournal[] HistoricalJournals()
        {
            if (!HoldsRootWriter)
            {
                throw new ObjectDisposedException("original root writer");
            }
            RequireRoot();
            int historicalJournalsValue = 0;
            long numericValue = 0L;
            List<HardThreadJournal> list = new List<HardThreadJournal>();
            foreach (string item in Directory.EnumerateFiles(root, "hard-thread-*.xml", SearchOption.TopDirectoryOnly))
            {
                if (++historicalJournalsValue > 1024 || (numericValue = checked(numericValue + new FileInfo(item).Length)) > 16777216 || (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("bounded historical source required");
                }
                list.Add(ReadLedger(item, null, false));
            }
            return list.ToArray();
        }

        internal static HardThreadJournal[] ObserveHistoricalJournals(string canonicalRoot)
        {
            using (var store = new HardThreadFileStore(canonicalRoot, true)) return store.HistoricalJournals();
        }

        // 原游戏已退出或进程编号已复用 未结清的行全部记为已退出
        internal void CommitHistoricalGone(HardThreadTarget target, HardThreadGoneReason reason)
        {
            HardThreadJournal journal = Load(target);
            Save(journal.Revision, HardThreadHistoricalPolicy.MarkGone(journal, reason));
        }

        // 全部结清后删除回执与会话锁 还有未结清的行时拒绝 锁文件被他人占用时留着 下次启动再删
        internal void RemoveSettled(HardThreadTarget target)
        {
            HardThreadJournal journal = Load(target);
            foreach (HardThreadReceipt row in journal.Rows)
            {
                if (!row.Terminal) throw new InvalidDataException("hard thread ledger is not settled");
            }
            string ledger = LedgerPath(target);
            if (File.Exists(ledger)) File.Delete(ledger);
            string lockPath = Path.Combine(root, "hard-thread-" + session.ToString("N") + ".lock");
            if (writer != null)
            {
                writer.Dispose();
                writer = null;
            }
            session = Guid.Empty;
            try { File.Delete(lockPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool CanonicalRootHasRecoveryDebt(string canonicalRoot)
        {
            bool debt;
            return !TryCanonicalRootHasRecoveryDebt(canonicalRoot, out debt) || debt;
        }

        // 区分已确认的顺序等待与不可读日志 失败观测和原生恢复分别计预算

        internal static bool TryCanonicalRootHasRecoveryDebt(string canonicalRoot, out bool debt)
        {
            debt = false;
            try
            {
                if (string.IsNullOrEmpty(canonicalRoot) || !Path.IsPathRooted(canonicalRoot))
                {
                    return false;
                }
                string fullPath = Path.GetFullPath(canonicalRoot);
                bool rootExists = false;
                for (DirectoryInfo directoryInfo = new DirectoryInfo(fullPath); directoryInfo != null; directoryInfo = directoryInfo.Parent)
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(directoryInfo.FullName);
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    {
                        return false;
                    }
                    if (string.Equals(directoryInfo.FullName.TrimEnd('\\'), fullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        rootExists = true;
                    }
                }

                if (!rootExists)
                {
                    return true;
                }
                try
                {
                    using (HardThreadFileStore hardThreadFileStore = new HardThreadFileStore(fullPath, true))
                    {
                        debt = hardThreadFileStore.HistoricalDebt();
                    }
                }
                catch (RootWriterBusyException) { debt = true; return true; }
                return true;
            }
            catch
            {
                    debt = false;
                    return false;
            }
        }

        private static HardThreadJournal ReadLedger(string path, HardThreadTarget target, bool allowMissing)
        {
            FileStream fileStream;
            try
            {
                fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (FileNotFoundException)
            {
                if (!allowMissing || target == null)
                {
                    throw;
                }
                HardThreadJournal hardThreadJournal = new HardThreadJournal();
                hardThreadJournal.Target = target;
                return hardThreadJournal;
            }
            XmlDocument xmlDocument = new XmlDocument();
            xmlDocument.XmlResolver = null;
            XmlDocument xmlDocument2 = xmlDocument;
            using (fileStream)
            {
                if (fileStream.Length > MaximumLedgerBytes)
                {
                    throw new InvalidDataException("bounded original journal required");
                }
                using (XmlReader reader = XmlReader.Create(fileStream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1048576L
                }))
                {
                    xmlDocument2.Load(reader);
                }
            }
            XmlElement documentElement = xmlDocument2.DocumentElement;
            if (documentElement == null || documentElement.Name != "hardThreads" || (documentElement.GetAttribute("version") != "1" && documentElement.GetAttribute("version") != "2"))
            {
                throw new InvalidDataException("journal format");
            }
            HardThreadTarget hardThreadTarget = new HardThreadTarget(Guid.Parse(documentElement.GetAttribute("session")), Number(documentElement, "pid"), Long(documentElement, "birth"), documentElement.GetAttribute("path"), Mask(documentElement, "allowed"));
            if (target != null && !target.Same(hardThreadTarget))
            {
                throw new InvalidDataException("journal original target mismatch");
            }
            if (!string.Equals(Path.GetFileName(path), "hard-thread-" + hardThreadTarget.SessionId.ToString("N") + ".xml", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("journal filename differs from original session");
            }
            HardThreadJournal hardThreadJournal2 = new HardThreadJournal();
            hardThreadJournal2.Target = hardThreadTarget;
            hardThreadJournal2.Revision = Long(documentElement, "revision");
            // 旧版回执带有独立守护的作用域属性 只读取线程行 这些属性忽略
            HardThreadJournal hardThreadJournal3 = hardThreadJournal2;
            if (hardThreadJournal3.Revision < 1)
            {
                throw new InvalidDataException("journal revision");
            }
            HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode childNode in documentElement.ChildNodes)
            {
                XmlElement xmlElement = childNode as XmlElement;
                if (xmlElement == null || xmlElement.Name != "thread" || hardThreadJournal3.Rows.Count >= 512)
                {
                    throw new InvalidDataException("journal row");
                }
                HardThreadReceipt hardThreadReceipt = new HardThreadReceipt();
                hardThreadReceipt.Thread = new HardThreadIdentity(Number(xmlElement, "tid"), Long(xmlElement, "birth"));
                hardThreadReceipt.Group = Group(xmlElement, "group");
                hardThreadReceipt.Captured = Mask(xmlElement, "captured");
                hardThreadReceipt.Applied = Mask(xmlElement, "applied");
                hardThreadReceipt.PreviousGroup = Group(xmlElement, "previousGroup");
                hardThreadReceipt.Previous = Mask(xmlElement, "previous");
                hardThreadReceipt.PreviousKnown = Flag(xmlElement, "previousKnown");
                hardThreadReceipt.RestorePreviousGroup = Group(xmlElement, "restoreGroup");
                hardThreadReceipt.RestorePrevious = Mask(xmlElement, "restorePrevious");
                hardThreadReceipt.RestorePreviousKnown = Flag(xmlElement, "restoreKnown");
                hardThreadReceipt.Detail = xmlElement.GetAttribute("detail");
                HardThreadReceipt hardThreadReceipt2 = hardThreadReceipt;
                HardThreadPhase result;
                if (!Enum.TryParse<HardThreadPhase>(xmlElement.GetAttribute("phase"), false, out result) || !Enum.IsDefined(typeof(HardThreadPhase), result) || hardThreadReceipt2.Captured == 0 || hardThreadReceipt2.Applied == 0 || (hardThreadReceipt2.Applied & ~hardThreadTarget.AllowedMask) != 0 || (hardThreadReceipt2.PreviousKnown && hardThreadReceipt2.Previous == 0) || (hardThreadReceipt2.RestorePreviousKnown && (!hardThreadReceipt2.PreviousKnown || hardThreadReceipt2.RestorePrevious == 0)))
                {
                    throw new InvalidDataException("journal receipt contract");
                }
                hardThreadReceipt2.Phase = result;
                if ((!hardThreadReceipt2.Terminal && !hashSet.Add(hardThreadReceipt2.Thread.Key)) || (hardThreadReceipt2.Terminal && hashSet.Contains(hardThreadReceipt2.Thread.Key)))
                {
                    throw new InvalidDataException("overlapping thread receipts");
                }
                if ((result == HardThreadPhase.PreviousRecorded || result == HardThreadPhase.Applied || result == HardThreadPhase.RestoreIntent || result == HardThreadPhase.RestorePreviousRecorded || result == HardThreadPhase.Restored) && !hardThreadReceipt2.PreviousKnown)
                {
                    throw new InvalidDataException("journal missing native previous");
                }
                if (result == HardThreadPhase.RestorePreviousRecorded && !hardThreadReceipt2.RestorePreviousKnown)
                {
                    throw new InvalidDataException("journal missing restore previous");
                }
                hardThreadJournal3.Rows.Add(hardThreadReceipt2);
            }
            return hardThreadJournal3;
        }

        private string LedgerPath(HardThreadTarget target)
        {
            return Path.Combine(root, "hard-thread-" + target.SessionId.ToString("N") + ".xml");
        }

        public void Save(long expectedRevision, HardThreadJournal journal)
        {
            if (journal == null || journal.Target == null || journal.Rows.Count > 512 || journal.Revision != expectedRevision + 1)
            {
                throw new InvalidDataException("journal revision contract");
            }
            if (journal.Target.AllowedMask == 0 && journal.Rows.Count != 0)
                throw new InvalidDataException(Logger.Text("仅优先级目标不得保存亲和性恢复行", "priority-only targets must not save affinity restore rows"));
            string bindResult = Bind(journal.Target);
            if (Load(journal.Target).Revision != expectedRevision)
            {
                throw new IOException("original ledger revision changed");
            }
            string combinedPath = Path.Combine(root, ".hard-thread-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    using (XmlWriter xmlWriter = XmlWriter.Create(new BoundedLedgerStream(fileStream), new XmlWriterSettings
                    {
                        Encoding = LedgerEncoding,
                        Indent = false,
                        CloseOutput = false
                    }))
                    {
                        xmlWriter.WriteStartElement("hardThreads");
                        Attribute(xmlWriter, "version", "1");
                        Attribute(xmlWriter, "session", journal.Target.SessionId.ToString("D"));
                        Attribute(xmlWriter, "pid", journal.Target.ProcessId);
                        Attribute(xmlWriter, "birth", journal.Target.ProcessCreation);
                        Attribute(xmlWriter, "path", journal.Target.ExecutablePath);
                        Attribute(xmlWriter, "allowed", journal.Target.AllowedMask);
                        Attribute(xmlWriter, "revision", journal.Revision);
                        // 每次提交仍写完整耐久账本 只复用字段完全相同的行编码 不缓存磁盘版本或恢复判定
                        if (encodedReceipts.Count > journal.Rows.Count)
                            encodedReceipts.RemoveRange(journal.Rows.Count, encodedReceipts.Count - journal.Rows.Count);
                        for (int index = 0; index < journal.Rows.Count; index++)
                            WriteCachedReceipt(xmlWriter, journal.Rows[index], index);
                        xmlWriter.WriteEndElement();
                    }
                    fileStream.Flush(true);
                }
                RequireRoot();
                if (File.Exists(bindResult))
                {
                    File.Replace(combinedPath, bindResult, null);
                }
                else
                {
                    File.Move(combinedPath, bindResult);
                }
            }
            catch
            {
                // 失败的编码不能占住大缓冲或成为重试依据 下次仍从正式账本核对版本
                encodedReceipts.Clear();
                ResetReceiptEncoder();
                throw;
            }
            finally
            {
                if (File.Exists(combinedPath))
                {
                    File.Delete(combinedPath);
                }
            }
        }

        private static void Attribute(XmlWriter writer, string key, object value)
        {
            writer.WriteAttributeString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private void WriteCachedReceipt(XmlWriter writer, HardThreadReceipt row, int index)
        {
            EncodedReceipt encoded = index < encodedReceipts.Count ? encodedReceipts[index] : null;
            if (encoded == null || !encoded.Matches(row))
            {
                try
                {
                    if (receiptEncoder == null)
                    {
                        receiptText = new StringBuilder(384);
                        receiptEncoder = XmlWriter.Create(new BoundedReceiptTextWriter(receiptText), new XmlWriterSettings
                        {
                            ConformanceLevel = ConformanceLevel.Fragment,
                            OmitXmlDeclaration = true,
                            Indent = false,
                            CloseOutput = true
                        });
                    }
                    XmlWriter fragment = receiptEncoder;
                    fragment.WriteStartElement("thread");
                    Attribute(fragment, "tid", row.Thread.Tid);
                    Attribute(fragment, "birth", row.Thread.Creation);
                    Attribute(fragment, "group", row.Group);
                    Attribute(fragment, "captured", row.Captured);
                    Attribute(fragment, "applied", row.Applied);
                    Attribute(fragment, "previousGroup", row.PreviousGroup);
                    Attribute(fragment, "previous", row.Previous);
                    Attribute(fragment, "previousKnown", row.PreviousKnown ? "1" : "0");
                    Attribute(fragment, "restoreGroup", row.RestorePreviousGroup);
                    Attribute(fragment, "restorePrevious", row.RestorePrevious);
                    Attribute(fragment, "restoreKnown", row.RestorePreviousKnown ? "1" : "0");
                    Attribute(fragment, "phase", row.Phase.ToString());
                    Attribute(fragment, "detail", row.Detail ?? "");
                    fragment.WriteEndElement();
                    // 片段模式允许连续根节点 先排空编码器再清文本 保留内部缓冲供下一行和下一次提交复用
                    fragment.Flush();
                    string xml = receiptText.ToString();
                    receiptText.Length = 0;
                    if (xml.Length > MaximumLedgerBytes / 3 && LedgerEncoding.GetByteCount(xml) > MaximumLedgerBytes)
                        throw new InvalidDataException("bounded original journal required");
                    encoded = new EncodedReceipt { Snapshot = row.Clone(), Xml = xml };
                    if (index < encodedReceipts.Count) encodedReceipts[index] = encoded;
                    else encodedReceipts.Add(encoded);
                }
                catch
                {
                    ResetReceiptEncoder();
                    throw;
                }
            }
            // 片段只能来自上方 XmlWriter 字段转义和字符检查与完整写入保持一致
            writer.WriteRaw(encoded.Xml);
        }

        private void ResetReceiptEncoder()
        {
            XmlWriter previous = receiptEncoder;
            receiptEncoder = null;
            receiptText = null;
            // 错误状态的 XmlWriter 可能在关闭时再次抛错 不能覆盖原始编码或持久化失败
            if (previous != null) { try { previous.Dispose(); } catch { } }
        }

        private static int Number(XmlElement node, string key)
        {
            return int.Parse(node.GetAttribute(key), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static long Long(XmlElement node, string key)
        {
            return long.Parse(node.GetAttribute(key), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static ulong Mask(XmlElement node, string key)
        {
            return ulong.Parse(node.GetAttribute(key), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static ushort Group(XmlElement node, string key)
        {
            return ushort.Parse(node.GetAttribute(key), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static bool Flag(XmlElement node, string key)
        {
            string attribute = node.GetAttribute(key);
            if (attribute != "0" && attribute != "1")
            {
                throw new InvalidDataException("journal flag");
            }
            return attribute == "1";
        }

        public void Dispose()
        {
            disposed = true;
            encodedReceipts.Clear();
            ResetReceiptEncoder();
            List<Exception> list = new List<Exception>();
            if (writer != null)
            {
                try
                {
                    writer.Dispose();
                    writer = null;
                }
                catch (Exception item)
                {
                    list.Add(item);
                }
            }
            if (rootWriter != null)
            {
                try
                {
                    rootWriter.Dispose();
                    rootWriter = null;
                }
                catch (Exception item2)
                {
                    list.Add(item2);
                }
            }
            if (list.Count > 0)
            {
                throw new AggregateException("original ledger handles did not close", list);
            }
        }
    }
}
