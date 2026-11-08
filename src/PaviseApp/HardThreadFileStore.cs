using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PaviseApp
{
	internal sealed class HardThreadFileStore : IHardThreadStore, IDisposable, IHardThreadSettledStore
	{
		private readonly string root;

		private FileStream writer;

		private FileStream rootWriter;

		private bool disposed;

		private Guid session;

		private string sourceNonce;

		private string sourceScopeHash;

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
			string text = Path.Combine(root, "hard-thread-" + session.ToString("N") + ".xml");
			if (File.Exists(text) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("reparse ledger refused");
			}
			return text;
		}

		public HardThreadJournal Load(HardThreadTarget target)
		{
			string path = Bind(target);
			HardThreadJournal hardThreadJournal = ReadLedger(path, target, true);
			if (sourceScopeHash != null)
			{
				if (hardThreadJournal.Revision == 0)
				{
					hardThreadJournal.SourceNonce = sourceNonce;
					hardThreadJournal.SourceScopeHash = sourceScopeHash;
				}
				else if (hardThreadJournal.SourceNonce != sourceNonce || hardThreadJournal.SourceScopeHash != sourceScopeHash)
				{
					throw new InvalidDataException("ledger differs from the original live scope");
				}
			}
			return hardThreadJournal;
		}

		internal void BindOriginalScope(HardThreadTarget target, string nonce, string scopeHash)
		{
			if (!Digest(nonce) || !Digest(scopeHash) || (sourceScopeHash != null && (sourceNonce != nonce || sourceScopeHash != scopeHash)))
			{
				throw new InvalidDataException("original scope binding unavailable or changed");
			}
			Bind(target);
			sourceNonce = nonce;
			sourceScopeHash = scopeHash;
		}

		private static bool Digest(string value)
		{
			if (value == null || value.Length != 64)
			{
				return false;
			}
			foreach (char c in value)
			{
				if (c < '0' || (c > '9' && c < 'a') || c > 'f')
				{
					return false;
				}
			}
			return true;
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
			int num = 0;
			long num2 = 0L;
			foreach (string item in Directory.EnumerateFiles(root, "hard-thread-*.xml", SearchOption.TopDirectoryOnly))
			{
				if (++num > 1024 || (num2 = checked(num2 + new FileInfo(item).Length)) > 16777216)
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
			int num = 0;
			long num2 = 0L;
			List<HardThreadJournal> list = new List<HardThreadJournal>();
			foreach (string item in Directory.EnumerateFiles(root, "hard-thread-*.xml", SearchOption.TopDirectoryOnly))
			{
				if (++num > 1024 || (num2 = checked(num2 + new FileInfo(item).Length)) > 16777216 || (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
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

		internal HardThreadJournal ReadHistorical(HardThreadTarget target)
		{
			if (!HoldsRootWriter)
			{
				throw new ObjectDisposedException("original root writer");
			}
			RequireRoot();
			string path = LedgerPath(target);
			if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			{
				throw new InvalidDataException("historical ledger reparse source refused");
			}
			return ReadLedger(path, target, false);
		}

		internal string HistoricalHash(HardThreadTarget target)
		{
			if (!HoldsRootWriter)
			{
				throw new ObjectDisposedException("original root writer");
			}
			return JournalHash(LedgerPath(target), false);
		}

		internal void CommitHistoricalGone(CriticalThreadGuardian.NativeGoneProof original)
		{
			if (original == null)
			{
				throw new ArgumentNullException("original");
			}
			HardThreadJournal hardThreadJournal = ReadHistorical(original.Target);
			string text = HistoricalHash(hardThreadJournal.Target);
			original.RequireCommit(this, root, hardThreadJournal, text);
			HardThreadJournal hardThreadJournal2 = HardThreadHistoricalPolicy.MarkGone(hardThreadJournal, original.Reason);
			string text2 = Path.Combine(root, "hard-thread-gone-" + hardThreadJournal.Target.SessionId.ToString("N") + "-" + hardThreadJournal.Revision + "-" + text + ".bak");
			if (File.Exists(text2))
			{
				if ((File.GetAttributes(text2) & FileAttributes.ReparsePoint) != 0 || JournalHash(text2, false) != text)
				{
					throw new InvalidDataException("historical original archive differs");
				}
			}
			else
			{
				byte[] array;
				using (FileStream fileStream = new FileStream(LedgerPath(hardThreadJournal.Target), FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					if (fileStream.Length < 1 || fileStream.Length > 1048576)
					{
						throw new InvalidDataException("bounded original archive required");
					}
					array = new byte[(int)fileStream.Length];
					int num;
					for (int i = 0; i < array.Length; i += num)
					{
						num = fileStream.Read(array, i, array.Length - i);
						if (num <= 0)
						{
							throw new EndOfStreamException();
						}
					}
					if (fileStream.ReadByte() != -1)
					{
						throw new IOException("historical source changed while archiving");
					}
				}
				using (SHA256 sHA = SHA256.Create())
				{
					if (BitConverter.ToString(sHA.ComputeHash(array)).Replace("-", "").ToLowerInvariant() != text)
					{
						throw new IOException("original historical source changed before archive");
					}
				}
				string text3 = Path.Combine(root, ".historical-archive-" + Guid.NewGuid().ToString("N") + ".tmp");
				try
				{
					using (FileStream fileStream2 = new FileStream(text3, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
					{
						fileStream2.Write(array, 0, array.Length);
						fileStream2.Flush(true);
					}
					File.Move(text3, text2);
				}
				finally
				{
					if (File.Exists(text3))
					{
						File.Delete(text3);
					}
				}
			}
			original.RequireCommit(this, root, ReadHistorical(hardThreadJournal.Target), HistoricalHash(hardThreadJournal.Target));
			BindOriginalScope(hardThreadJournal.Target, hardThreadJournal.SourceNonce, hardThreadJournal.SourceScopeHash);
			Save(hardThreadJournal.Revision, hardThreadJournal2);
			HardThreadJournal hardThreadJournal3 = Load(hardThreadJournal.Target);
			if (hardThreadJournal3.Revision != hardThreadJournal2.Revision)
			{
				throw new IOException("historical gone metadata revision was not confirmed");
			}
			foreach (HardThreadReceipt row in hardThreadJournal3.Rows)
			{
				if (!row.Terminal)
				{
					throw new IOException("historical gone metadata was not confirmed");
				}
			}
		}

		internal static bool CanonicalRootHasRecoveryDebt(string canonicalRoot)
		{
			bool debt;
			return !TryCanonicalRootHasRecoveryDebt(canonicalRoot, out debt) || debt;
		}

		// Distinguish verified ordering debt from an unreadable journal.
		// Budget failed observations separately from native restore attempts.
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
				bool flag = false;
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
						flag = true;
					}
				}
				if (CriticalThreadGuardian.HasControllerDebt(fullPath))
				{
					debt = true;
					return true;
				}
				if (!flag)
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
				if (fileStream.Length > 1048576)
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
			HardThreadJournal hardThreadJournal3 = hardThreadJournal2;
			if (documentElement.GetAttribute("version") == "2")
			{
				hardThreadJournal3.SourceNonce = documentElement.GetAttribute("sourceNonce");
				hardThreadJournal3.SourceScopeHash = documentElement.GetAttribute("sourceScopeHash");
				if (!Digest(hardThreadJournal3.SourceNonce) || !Digest(hardThreadJournal3.SourceScopeHash))
				{
					throw new InvalidDataException("original machine scope binding missing");
				}
			}
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

		private static string JournalHash(string path, bool allowMissing)
		{
			try
			{
				using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					if (fileStream.Length > 1048576)
					{
						throw new InvalidDataException("bounded settled source required");
					}
					using (SHA256 sHA = SHA256.Create())
					{
						return BitConverter.ToString(sHA.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();
					}
				}
			}
			catch (FileNotFoundException)
			{
				if (allowMissing)
				{
					return null;
				}
				throw;
			}
		}

		public HardThreadSettledSource CaptureSettled(HardThreadTarget target, long revision)
		{
			HardThreadJournal hardThreadJournal = Load(target);
			if (hardThreadJournal.Revision != revision)
			{
				throw new InvalidDataException("settled original revision changed");
			}
			foreach (HardThreadReceipt row in hardThreadJournal.Rows)
			{
				if (!row.Terminal)
				{
					throw new InvalidDataException("original ownership is not settled");
				}
			}
			return new HardThreadSettledSource(root, hardThreadJournal.Target, revision, JournalHash(LedgerPath(target), revision == 0));
		}

		internal bool MatchesSettled(HardThreadSettledSource original, bool alreadyRemoved)
		{
			if (original == null || !string.Equals(root.TrimEnd('\\'), original.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			RequireRoot();
			string path = LedgerPath(original.Target);
			if (alreadyRemoved)
			{
				try
				{
					File.GetAttributes(path);
					return false;
				}
				catch (FileNotFoundException)
				{
					return true;
				}
			}
			HardThreadJournal hardThreadJournal = ReadLedger(path, original.Target, true);
			if (hardThreadJournal.Revision != original.Revision)
			{
				return false;
			}
			foreach (HardThreadReceipt row in hardThreadJournal.Rows)
			{
				if (!row.Terminal)
				{
					return false;
				}
			}
			return JournalHash(path, original.Revision == 0) == original.JournalHash;
		}

		public void Save(long expectedRevision, HardThreadJournal journal)
		{
			if (journal == null || journal.Target == null || journal.Rows.Count > 512 || journal.Revision != expectedRevision + 1)
			{
				throw new InvalidDataException("journal revision contract");
			}
			string text = Bind(journal.Target);
			if (sourceScopeHash != null && (journal.SourceNonce != sourceNonce || journal.SourceScopeHash != sourceScopeHash))
			{
				throw new InvalidDataException("journal lost its original scope");
			}
			if (Load(journal.Target).Revision != expectedRevision)
			{
				throw new IOException("original ledger revision changed");
			}
			string text2 = Path.Combine(root, ".hard-thread-" + Guid.NewGuid().ToString("N") + ".tmp");
			try
			{
				using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
				{
					using (XmlWriter xmlWriter = XmlWriter.Create(fileStream, new XmlWriterSettings
					{
						Encoding = new UTF8Encoding(false),
						Indent = false,
						CloseOutput = false
					}))
					{
						bool flag = Digest(journal.SourceNonce) && Digest(journal.SourceScopeHash);
						xmlWriter.WriteStartElement("hardThreads");
						Attribute(xmlWriter, "version", flag ? "2" : "1");
						Attribute(xmlWriter, "session", journal.Target.SessionId.ToString("D"));
						Attribute(xmlWriter, "pid", journal.Target.ProcessId);
						Attribute(xmlWriter, "birth", journal.Target.ProcessCreation);
						Attribute(xmlWriter, "path", journal.Target.ExecutablePath);
						Attribute(xmlWriter, "allowed", journal.Target.AllowedMask);
						Attribute(xmlWriter, "revision", journal.Revision);
						if (flag)
						{
							Attribute(xmlWriter, "sourceNonce", journal.SourceNonce);
							Attribute(xmlWriter, "sourceScopeHash", journal.SourceScopeHash);
						}
						foreach (HardThreadReceipt row in journal.Rows)
						{
							xmlWriter.WriteStartElement("thread");
							Attribute(xmlWriter, "tid", row.Thread.Tid);
							Attribute(xmlWriter, "birth", row.Thread.Creation);
							Attribute(xmlWriter, "group", row.Group);
							Attribute(xmlWriter, "captured", row.Captured);
							Attribute(xmlWriter, "applied", row.Applied);
							Attribute(xmlWriter, "previousGroup", row.PreviousGroup);
							Attribute(xmlWriter, "previous", row.Previous);
							Attribute(xmlWriter, "previousKnown", row.PreviousKnown ? "1" : "0");
							Attribute(xmlWriter, "restoreGroup", row.RestorePreviousGroup);
							Attribute(xmlWriter, "restorePrevious", row.RestorePrevious);
							Attribute(xmlWriter, "restoreKnown", row.RestorePreviousKnown ? "1" : "0");
							Attribute(xmlWriter, "phase", row.Phase.ToString());
							Attribute(xmlWriter, "detail", row.Detail ?? "");
							xmlWriter.WriteEndElement();
						}
						xmlWriter.WriteEndElement();
					}
					fileStream.Flush(true);
				}
				RequireRoot();
				if (File.Exists(text))
				{
					File.Replace(text2, text, null);
				}
				else
				{
					File.Move(text2, text);
				}
			}
			finally
			{
				if (File.Exists(text2))
				{
					File.Delete(text2);
				}
			}
		}

		private static void Attribute(XmlWriter writer, string key, object value)
		{
			writer.WriteAttributeString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
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
