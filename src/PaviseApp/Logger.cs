using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class Logger
	{
		private sealed class RepeatedFailure
		{
			internal string Message;

			internal long Next;

			internal int Suppressed;
		}

		private const long RotateBytes = 524288L;

		public const string WritesEnabledKey = "LogWritesEnabled";

		public const string WarnTag = "WARN ";

		public const string FailTag = "FAIL ";

		public const string InfoTag = "INFO ";

		public const string SuccessTag = "PASS ";

		private static readonly object lk = new object();

		public static string LogPath;

		private static long version;

		private static long knownLength = -1L;

		private static string knownPath;

		private static bool writesSuspendedForReset;

		private static bool writesEnabled = true;

		private static readonly Dictionary<string, RepeatedFailure> repeated = new Dictionary<string, RepeatedFailure>();

		public static long Version
		{
			get
			{
				return Interlocked.Read(ref version);
			}
		}

		public static bool WritesEnabled
		{
			get
			{
				lock (lk)
				{
					return writesEnabled;
				}
			}
			set
			{
				lock (lk)
				{
					writesEnabled = value;
					if (!value)
					{
						repeated.Clear();
					}
				}
			}
		}

		internal static void SuspendWritesForReset()
		{
			lock (lk)
			{
				writesSuspendedForReset = true;
				repeated.Clear();
			}
		}

		public static void Info(string msg)
		{
			Log("INFO " + msg);
		}

		internal static void RuntimeIdentity()
		{
			Info("RUNTIME event=identity version=" + App.Version + " mvid=" + typeof(App).Module.ModuleVersionId.ToString("D")
				+ " os=" + Environment.OSVersion.Version + " arch=" + (Environment.Is64BitProcess ? "x64" : "x86")
				+ " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
		}

		public static void Warn(string msg)
		{
			Log("WARN " + msg);
		}

		public static void Error(string msg)
		{
			Log("FAIL " + msg);
		}

		internal static void RepeatedError(string source, string message, long now = -1L)
		{
			if (now < 0)
			{
				now = DateTime.UtcNow.Ticks;
			}
			lock (lk)
			{
				if (writesSuspendedForReset || !writesEnabled)
				{
					repeated.Remove(source);
					return;
				}
				RepeatedFailure value;
				if (repeated.TryGetValue(source, out value))
				{
					if (value.Message == message && now < value.Next)
					{
						if (value.Suppressed < int.MaxValue)
						{
							value.Suppressed++;
						}
						return;
					}
					FlushRepeated(source, value);
				}
				else
				{
					if (repeated.Count >= 64)
					{
						repeated.Clear();
					}
					value = new RepeatedFailure();
					repeated[source] = value;
				}
				value.Message = message;
				value.Next = now + TimeSpan.FromMinutes(1.0).Ticks;
				Error(message);
			}
		}

		private static void FlushRepeated(string source, RepeatedFailure state)
		{
			if (state.Suppressed > 0)
			{
				Warn(source + " | repeated=" + state.Suppressed + " | " + state.Message);
			}
			state.Suppressed = 0;
		}

		internal static void RepeatedRecovered(string source)
		{
			lock (lk)
			{
				RepeatedFailure value;
				if (writesSuspendedForReset || !writesEnabled)
				{
					repeated.Remove(source);
				}
				else if (repeated.TryGetValue(source, out value))
				{
					FlushRepeated(source, value);
					repeated.Remove(source);
					Info(source + " | recovered");
				}
			}
		}

		public static void Log(string msg)
		{
			try
			{
				lock (lk)
				{
					if (writesSuspendedForReset || !writesEnabled)
					{
						return;
					}
					string newLineOffset = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (msg ?? "").Replace("\r\n", " ⏎ ").Replace("\r", " ⏎ ").Replace("\n", " ⏎ ") + Environment.NewLine;
					if (knownLength < 0 || !string.Equals(knownPath, LogPath, StringComparison.OrdinalIgnoreCase))
					{
						FileInfo fileInfo = new FileInfo(LogPath);
						knownLength = (fileInfo.Exists ? fileInfo.Length : 0);
						knownPath = LogPath;
					}
					if (knownLength > 524288)
					{
						FileInfo fileInfo2 = new FileInfo(LogPath);
						if (fileInfo2.Exists && fileInfo2.Length > 524288)
						{
							string logPathOffset = LogPath + ".old";
							try
							{
								if (File.Exists(logPathOffset))
								{
									File.Delete(logPathOffset);
								}
								fileInfo2.MoveTo(logPathOffset);
							}
							catch
							{
								try
								{
									fileInfo2.Delete();
								}
								catch
								{
								}
							}
						}
						knownLength = 0L;
					}
					File.AppendAllText(LogPath, newLineOffset);
					knownLength += Encoding.UTF8.GetByteCount(newLineOffset);
					Interlocked.Increment(ref version);
				}
			}
			catch
			{
				lock (lk)
				{
					knownLength = -1L;
				}
			}
		}

		public static void LogFailure(string context, Exception error)
		{
			string messageOffset = context + " " + error.GetType().Name + " - " + error.Message;
			Log("FAIL " + messageOffset);
		}

		internal static void AppendCrash(string path, string details)
		{
			try
			{
				lock (lk)
				{
					if (!writesSuspendedForReset)
					{
						File.AppendAllText(path, details);
					}
				}
			}
			catch
			{
			}
		}

		public static void Clear()
		{
			try
			{
				lock (lk)
				{
					if (!writesSuspendedForReset)
					{
						File.WriteAllText(LogPath, "");
						repeated.Clear();
						Interlocked.Increment(ref version);
						knownLength = 0L;
						knownPath = LogPath;
					}
				}
			}
			catch
			{
				lock (lk)
				{
					knownLength = -1L;
				}
			}
		}

		internal static void ExportTo(string destination)
		{
			string output = Path.GetFullPath(destination);
			byte[] snapshot = ReadExportSnapshot(output);
			// A slow destination must not hold up runtime log writes or rotation.
			File.WriteAllBytes(output, snapshot);
		}

		internal static void ExportActionsTo(string destination, string activeSession)
		{
			string output = Path.GetFullPath(destination);
			byte[] snapshot = ReadExportSnapshot(output);
			string actions = ActionLogProjection.Export(Encoding.UTF8.GetString(snapshot).TrimStart('\uFEFF'), activeSession);
			File.WriteAllText(output, actions, new UTF8Encoding(false));
		}

		private static byte[] ReadExportSnapshot(string output)
		{
			byte[] snapshot;
			lock (lk)
			{
				if (string.IsNullOrEmpty(LogPath)) throw new FileNotFoundException("No runtime log is available.");
				string source = Path.GetFullPath(LogPath);
				if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(output, source + ".old", StringComparison.OrdinalIgnoreCase))
					throw new IOException("Choose a different file from the active runtime logs.");
				using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					snapshot = new byte[checked((int)input.Length)];
					int offset = 0;
					while (offset < snapshot.Length)
					{
						int count = input.Read(snapshot, offset, snapshot.Length - offset);
						if (count == 0) throw new EndOfStreamException("The runtime log changed while it was being read.");
						offset += count;
					}
				}
			}
			return snapshot;
		}

		public static string Tail(int maxLines)
		{
			try
			{
				lock (lk)
				{
					if (maxLines <= 0 || string.IsNullOrEmpty(LogPath) || !File.Exists(LogPath))
					{
						return "";
					}
					using (FileStream fileStream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
					{
						long positionValue = 0L;
						long positionValue2 = 0L;
						long length = fileStream.Length;
						if (length > 0)
						{
							byte[] rawBytes = new byte[4096];
							int processedCount = 0;
							long lengthSnapshot = length;
							while (lengthSnapshot > 0 && processedCount <= maxLines)
							{
								int lengthSnapshotLimit = (int)Math.Min(rawBytes.Length, lengthSnapshot);
								lengthSnapshot = (fileStream.Position = lengthSnapshot - lengthSnapshotLimit);
								int readResult = fileStream.Read(rawBytes, 0, lengthSnapshotLimit);
								for (int rawBytesIndex = readResult - 1; rawBytesIndex >= 0; rawBytesIndex--)
								{
									long lengthSnapshotOffset = lengthSnapshot + rawBytesIndex;
									if (rawBytes[rawBytesIndex] == 10 && lengthSnapshotOffset != length - 1)
									{
										processedCount++;
										if (processedCount == maxLines)
										{
											positionValue = lengthSnapshotOffset + 1;
										}
										else if (processedCount == maxLines + 1)
										{
											positionValue2 = lengthSnapshotOffset + 1;
											break;
										}
									}
								}
							}
						}
						fileStream.Position = positionValue;
						string readToEndTrimmed;
						using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true, 4096, true))
						{
							readToEndTrimmed = streamReader.ReadToEnd().TrimEnd('\r', '\n');
						}
						if (positionValue > positionValue2 && readToEndTrimmed.TrimStart().StartsWith("<Objs", StringComparison.Ordinal))
						{
							fileStream.Position = positionValue2;
							using (StreamReader streamReader2 = new StreamReader(fileStream, Encoding.UTF8, true, 4096, true))
							{
								string line = streamReader2.ReadLine();
								if (line != null && line.IndexOf("#< CLIXML", StringComparison.Ordinal) >= 0)
								{
									readToEndTrimmed = line + Environment.NewLine + readToEndTrimmed;
								}
							}
						}
						return readToEndTrimmed;
					}
				}
			}
			catch
			{
				return "";
			}
		}
	}
}
