using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal sealed class LibraryIgnoreTransaction
	{
		internal const string FileName = "Pavise.library-ignore.txn";

		internal const string IgnoreFileName = "Pavise.autoignore.txt";

		private const string Header = "PAVISE_LIBRARY_IGNORE_TXN_V1";

		private const int MaxReceiptBytes = 8388608;

		private readonly string profilePath;

		private readonly string ignorePath;

		private readonly string receiptPath;

		private readonly GameProfileStore store;

		internal bool RecoveryPending { get; private set; }

		internal LibraryIgnoreTransaction(string directory, GameProfileStore profileStore)
		{
			store = profileStore;
			profilePath = Path.Combine(directory, "Pavise.profiles.dat");
			ignorePath = Path.Combine(directory, "Pavise.autoignore.txt");
			receiptPath = Path.Combine(directory, "Pavise.library-ignore.txn");
			RecoveryPending = File.Exists(receiptPath);
		}

		internal bool Commit(IList<GameProfile> next, byte[] nextIgnore, Func<bool> canCommit)
		{
			if (nextIgnore == null)
			{
				return false;
			}
			if (!TryRecover())
			{
				return false;
			}
			byte[] bytes;
			try
			{
				bytes = GameProfileStore.SnapshotBytes(next);
			}
			catch (Exception error)
			{
				store.MarkSaveFailed(error);
				return false;
			}
			try
			{
				byte[] bytes2 = ReadOptional(profilePath);
				byte[] optional = ReadOptional(ignorePath);
				if (Same(optional, nextIgnore))
				{
					return store.Save(next, canCommit);
				}
				string hashResult = Hash(bytes2);
				string hashResult2 = Hash(bytes);
				if (hashResult == hashResult2)
				{
					WriteAtomic(ignorePath, nextIgnore, "apply-ignore-only", canCommit);
					return true;
				}
				string joinResult = string.Join("\n", "PAVISE_LIBRARY_IGNORE_TXN_V1", hashResult, hashResult2, Encode(optional), Encode(nextIgnore));
				byte[] bytes3 = Encoding.UTF8.GetBytes(joinResult + "\n" + Hash(Encoding.UTF8.GetBytes(joinResult)));
				if (bytes3.Length > 8388608)
				{
					throw new FormatException("Oversized library receipt");
				}
				WriteAtomic(receiptPath, bytes3, "prepare");
				RecoveryPending = true;
				WriteAtomic(ignorePath, nextIgnore, "apply-ignore");
				bool result = store.Save(next, canCommit);
				TryRecover();
				return result;
			}
			catch (OperationCanceledException)
			{
				return false;
			}
			catch (Exception error2)
			{
				Logger.LogFailure("Library/auto-ignore transaction could not commit", error2);
				if (RecoveryPending)
				{
					TryRecover();
				}
				return false;
			}
		}

		internal bool TryRecover()
		{
			if (!RecoveryPending)
			{
				return true;
			}
			try
			{
				FileInfo fileInfo = new FileInfo(receiptPath);
				if (fileInfo.Length > 8388608)
				{
					throw new FormatException("Oversized library receipt");
				}
				string[] getStringParts = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(receiptPath)).Split('\n');
				if (getStringParts.Length != 6 || getStringParts[0] != "PAVISE_LIBRARY_IGNORE_TXN_V1" || Hash(Encoding.UTF8.GetBytes(string.Join("\n", getStringParts, 0, 5))) != getStringParts[5])
				{
					throw new FormatException("Invalid library receipt");
				}
				byte[] decodeResult = Decode(getStringParts[3]);
				byte[] decodeResult2 = Decode(getStringParts[4]);
				if (decodeResult2 == null)
				{
					throw new FormatException("Missing target ignore snapshot");
				}
				string hashResult = Hash(ReadOptional(profilePath));
				byte[] rawBytes;
				if (hashResult == getStringParts[2])
				{
					rawBytes = decodeResult2;
				}
				else
				{
					if (!(hashResult == getStringParts[1]))
					{
						throw new IOException("Library changed outside the pending transaction; preserving receipt");
					}
					rawBytes = decodeResult;
				}
				byte[] a = ReadOptional(ignorePath);
				if (!Same(a, decodeResult) && !Same(a, decodeResult2))
				{
					throw new IOException("Auto-ignore changed outside the pending transaction; preserving receipt");
				}
				if (!Same(a, rawBytes))
				{
					BeforeWrite("restore-ignore");
					if (rawBytes == null)
					{
						File.Delete(ignorePath);
					}
					else
					{
						WriteAtomic(ignorePath, rawBytes, "restore-ignore-write");
					}
				}
				BeforeWrite("clear-receipt");
				File.Delete(receiptPath);
				RecoveryPending = false;
				return true;
			}
			catch (Exception error)
			{
				Logger.LogFailure("Library/auto-ignore recovery pending; further library writes paused", error);
				return false;
			}
		}

		private void BeforeWrite(string phase)
		{
		}

		private void WriteAtomic(string path, byte[] bytes, string phase, Func<bool> canCommit = null)
		{
			BeforeWrite(phase);
			string message = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using (FileStream fileStream = new FileStream(message, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				if (!File.Exists(path))
				{
					if (canCommit != null && !canCommit())
					{
						throw new OperationCanceledException();
					}
					File.Move(message, path);
					return;
				}
				int processedCount = 0;
				while (true)
				{
					try
					{
						if (canCommit != null && !canCommit())
						{
							throw new OperationCanceledException();
						}
						File.Replace(message, path, null);
						break;
					}
					catch (IOException error)
					{
						if (processedCount >= 2 || !GameProfileStore.IsRetryableReplaceError(error) || !File.Exists(path) || !File.Exists(message))
						{
							throw;
						}
						Thread.Sleep(25 * (processedCount + 1));
					}
					processedCount++;
				}
			}
			finally
			{
				try
				{
					if (File.Exists(message))
					{
						File.Delete(message);
					}
				}
				catch
				{
				}
			}
		}

		private static byte[] ReadOptional(string path)
		{
			try
			{
				return File.ReadAllBytes(path);
			}
			catch (FileNotFoundException)
			{
				return null;
			}
		}

		private static bool Same(byte[] a, byte[] b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}

		private static string Encode(byte[] bytes)
		{
			if (bytes != null)
			{
				return Convert.ToBase64String(bytes);
			}
			return "-";
		}

		private static byte[] Decode(string text)
		{
			if (!(text == "-"))
			{
				return Convert.FromBase64String(text);
			}
			return null;
		}

		private static string Hash(byte[] bytes)
		{
			if (bytes == null)
			{
				return "-";
			}
			using (SHA256 sHA = SHA256.Create())
			{
				return BitConverter.ToString(sHA.ComputeHash(bytes)).Replace("-", "");
			}
		}
	}
}
