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
					string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (msg ?? "").Replace("\r\n", " ⏎ ").Replace("\r", " ⏎ ").Replace("\n", " ⏎ ") + Environment.NewLine;
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
							string text2 = LogPath + ".old";
							try
							{
								if (File.Exists(text2))
								{
									File.Delete(text2);
								}
								fileInfo2.MoveTo(text2);
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
					File.AppendAllText(LogPath, text);
					knownLength += Encoding.UTF8.GetByteCount(text);
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
			string text = context + " " + error.GetType().Name + " - " + error.Message;
			Log("FAIL " + text);
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
						long num = 0L;
						long num2 = 0L;
						long length = fileStream.Length;
						if (length > 0)
						{
							byte[] array = new byte[4096];
							int num3 = 0;
							long num4 = length;
							while (num4 > 0 && num3 <= maxLines)
							{
								int num5 = (int)Math.Min(array.Length, num4);
								num4 = (fileStream.Position = num4 - num5);
								int num7 = fileStream.Read(array, 0, num5);
								for (int num8 = num7 - 1; num8 >= 0; num8--)
								{
									long num9 = num4 + num8;
									if (array[num8] == 10 && num9 != length - 1)
									{
										num3++;
										if (num3 == maxLines)
										{
											num = num9 + 1;
										}
										else if (num3 == maxLines + 1)
										{
											num2 = num9 + 1;
											break;
										}
									}
								}
							}
						}
						fileStream.Position = num;
						string text;
						using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true, 4096, true))
						{
							text = streamReader.ReadToEnd().TrimEnd('\r', '\n');
						}
						if (num > num2 && text.TrimStart().StartsWith("<Objs", StringComparison.Ordinal))
						{
							fileStream.Position = num2;
							using (StreamReader streamReader2 = new StreamReader(fileStream, Encoding.UTF8, true, 4096, true))
							{
								string text2 = streamReader2.ReadLine();
								if (text2 != null && text2.IndexOf("#< CLIXML", StringComparison.Ordinal) >= 0)
								{
									text = text2 + Environment.NewLine + text;
								}
							}
						}
						return text;
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
