using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace PaviseApp
{
	internal static class LolLcuCredentialSource
	{
		private const int RecentLogLimit = 8;

		private const int CredentialProbeLimit = 4;

		private const int LockfileTailBytes = 4096;

		private const int LogHeadBytes = 65536;

		private const int LogTailBytes = 458752;

		private static readonly object LogCacheLock = new object();

		private static string cachedLogRoot;

		private static string[] cachedLogPaths;

		private static DateTime cachedLogPathsUntilUtc;

		internal static long CredentialGenerationStamp(string lolRoot)
		{
			if (string.IsNullOrEmpty(lolRoot))
			{
				return 0L;
			}
			long numericValue = 17L;
			string[] textEntries = new string[3]
			{
				Path.Combine(lolRoot, "lockfile"),
				Path.Combine(lolRoot, "LeagueClient", "lockfile"),
				Path.Combine(lolRoot, "Riot Client", "lockfile")
			};
			for (int i = 0; i < textEntries.Length; i++)
			{
				try
				{
					FileInfo fileInfo = new FileInfo(textEntries[i]);
					if (fileInfo.Exists)
					{
						numericValue = (numericValue * 31) ^ fileInfo.LastWriteTimeUtc.Ticks;
						numericValue = (numericValue * 31) ^ fileInfo.Length;
					}
				}
				catch
				{
				}
			}
			numericValue = (numericValue * 31) ^ CredentialProcessStamp(lolRoot, "LeagueClient");
			return (numericValue * 31) ^ CredentialProcessStamp(lolRoot, "LeagueClientUx");
		}

		private static long CredentialProcessStamp(string lolRoot, string processName)
		{
			Process[] processesByName;
			try
			{
				processesByName = Process.GetProcessesByName(processName);
			}
			catch
			{
				return 0L;
			}
			long numericValue = 0L;
			int credentialProcessStampValue = 0;
			try
			{
				Process[] processesByNameSnapshot = processesByName;
				foreach (Process process in processesByNameSnapshot)
				{
					IntPtr intPtr = IntPtr.Zero;
					try
					{
						intPtr = Native.OpenProcess(4096, false, process.Id);
						if (!(intPtr == IntPtr.Zero))
						{
							string path = Native.ImagePath(intPtr);
							string fileName = Path.GetFileName(path);
							long creation;
							long cpu;
							ulong io;
							if (LolRuntimeProcesses.IsUnder(path, lolRoot) && string.Equals(fileName, processName + ".exe", StringComparison.OrdinalIgnoreCase) && Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation > numericValue)
							{
								numericValue = creation;
								credentialProcessStampValue = process.Id;
							}
						}
					}
					catch
					{
					}
					finally
					{
						if (intPtr != IntPtr.Zero)
						{
							Native.CloseHandle(intPtr);
						}
					}
				}
			}
			finally
			{
				Process[] processesByNameSnapshot2 = processesByName;
				foreach (Process process2 in processesByNameSnapshot2)
				{
					if (process2 != null)
					{
						try
						{
							process2.Dispose();
						}
						catch
						{
						}
					}
				}
			}
			return numericValue ^ ((long)credentialProcessStampValue << 32);
		}

		public static LolLcuCredentials Find(string lolRoot)
		{
			if (!LolInstallDiscovery.IsValidLolRoot(lolRoot))
			{
				return null;
			}
			HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
			int probed;
			LolLcuCredentials lolLcuCredentials = SelectReachable(ReadProcessCandidates(lolRoot), LolLcuClient.IsCredentialReachable, Math.Min(2, 4), seen, out probed);
			if (lolLcuCredentials != null || probed >= 4)
			{
				return lolLcuCredentials;
			}
			int probed2;
			lolLcuCredentials = SelectReachable(ReadLockfileCandidates(lolRoot), LolLcuClient.IsCredentialReachable, 4 - probed, seen, out probed2);
			probed += probed2;
			if (lolLcuCredentials != null || probed >= 4)
			{
				return lolLcuCredentials;
			}
			if (!LolRuntimeProcesses.HasExclusiveCredentialSourceSession(lolRoot))
			{
				return null;
			}
			int probed3;
			return SelectReachableLogCandidate(lolRoot, LolLcuClient.IsCredentialReachable, 4 - probed, seen, out probed3);
		}

		private static IList<LolLcuCredentials> ReadProcessCandidates(string lolRoot)
		{
			List<LolLcuCredentials> list = new List<LolLcuCredentials>();
			int sessionId;
			if (!LolRuntimeProcesses.TryGetCurrentSessionId(out sessionId))
			{
				return list;
			}
			try
			{
				using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT ProcessId, SessionId, CreationDate, ExecutablePath, CommandLine, Name FROM Win32_Process WHERE Name='LeagueClient.exe' OR Name='LeagueClientUx.exe'"))
				{
					managementObjectSearcher.Options.Timeout = TimeSpan.FromSeconds(3.0);
					using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
					{
						List<ManagementObject> list2 = new List<ManagementObject>();
						foreach (ManagementObject item in managementObjectCollection)
						{
							list2.Add(item);
						}
						list2.Sort(delegate(ManagementObject left, ManagementObject right)
						{
							string a = Convert.ToString(left["Name"]);
							string a2 = Convert.ToString(right["Name"]);
							bool valuesEqual = string.Equals(a, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase);
							bool valuesEqual2 = string.Equals(a2, "LeagueClient.exe", StringComparison.OrdinalIgnoreCase);
							if (valuesEqual != valuesEqual2)
							{
								if (!valuesEqual)
								{
									return 1;
								}
								return -1;
							}
							long result3;
							long.TryParse(Convert.ToString(left["ProcessId"]), out result3);
							long result4;
							long.TryParse(Convert.ToString(right["ProcessId"]), out result4);
							return result4.CompareTo(result3);
						});
						try
						{
							for (int i = 0; i < list2.Count; i++)
							{
								ManagementObject managementObject = list2[i];
								int result;
								int result2;
								long fileTimeUtc;
								int port;
								string token;
								if (int.TryParse(Convert.ToString(managementObject["ProcessId"]), out result) && int.TryParse(Convert.ToString(managementObject["SessionId"]), out result2) && TryParseWmiCreation(managementObject["CreationDate"], out fileTimeUtc) && LolRuntimeProcesses.IsExpectedSession(sessionId, result2) && LolRuntimeProcesses.IsOwnedCredentialSourceProcess(lolRoot, result, sessionId, fileTimeUtc) && LolCredentialParser.TryParseCommandLine(Convert.ToString(managementObject["CommandLine"]), out port, out token))
								{
									list.Add(new LolLcuCredentials(port, token));
								}
							}
						}
						finally
						{
							foreach (ManagementObject item2 in list2)
							{
								if (item2 != null)
								{
									try
									{
										item2.Dispose();
									}
									catch
									{
									}
								}
							}
						}
					}
				}
			}
			catch
			{
			}
			return list;
		}

		private static bool TryParseWmiCreation(object value, out long fileTimeUtc)
		{
			fileTimeUtc = 0L;
			try
			{
				string convertText = Convert.ToString(value);
				if (string.IsNullOrWhiteSpace(convertText))
				{
					return false;
				}
				fileTimeUtc = ManagementDateTimeConverter.ToDateTime(convertText).ToUniversalTime().ToFileTimeUtc();
				return fileTimeUtc > 0;
			}
			catch
			{
				return false;
			}
		}

		private static IList<LolLcuCredentials> ReadLockfileCandidates(string lolRoot)
		{
			int sessionId;
			if (!LolRuntimeProcesses.TryGetCurrentSessionId(out sessionId))
			{
				return new List<LolLcuCredentials>();
			}
			string[] paths = new string[3]
			{
				Path.Combine(lolRoot, "lockfile"),
				Path.Combine(lolRoot, "LeagueClient", "lockfile"),
				Path.Combine(lolRoot, "Riot Client", "lockfile")
			};
			paths = OrderExistingCredentialFiles(paths);
			List<LolLcuCredentials> list = new List<LolLcuCredentials>(paths.Length);
			for (int i = 0; i < paths.Length; i++)
			{
				string content;
				int pid;
				int port;
				string token;
				if (TryReadTail(paths[i], 4096, out content) && LolCredentialParser.TryParseLockfile(content, out pid, out port, out token) && LolRuntimeProcesses.IsOwnedCredentialSourceProcess(lolRoot, pid, sessionId))
				{
					list.Add(new LolLcuCredentials(port, token));
				}
			}
			return list;
		}

		private static LolLcuCredentials SelectReachableLogCandidate(string lolRoot, Func<LolLcuCredentials, bool> probe, int maximumProbes, HashSet<string> seen, out int probed)
		{
			probed = 0;
			if (probe == null || maximumProbes <= 0)
			{
				return null;
			}
			if (seen == null)
			{
				seen = new HashSet<string>(StringComparer.Ordinal);
			}
			string[] cachedLogPathsSnapshot;
			lock (LogCacheLock)
			{
				if (cachedLogPaths != null && DateTime.UtcNow < cachedLogPathsUntilUtc && string.Equals(cachedLogRoot, lolRoot, StringComparison.OrdinalIgnoreCase))
				{
					cachedLogPathsSnapshot = cachedLogPaths;
				}
				else
				{
					cachedLogPathsSnapshot = SelectRecentLogPaths(lolRoot, 8);
					cachedLogRoot = lolRoot;
					cachedLogPaths = cachedLogPathsSnapshot;
					cachedLogPathsUntilUtc = DateTime.UtcNow.AddSeconds(10.0);
				}
			}
			for (int i = 0; i < cachedLogPathsSnapshot.Length; i++)
			{
				if (probed >= maximumProbes)
				{
					break;
				}
				string content;
				int port;
				string token;
				if (!TryReadHeadAndTail(cachedLogPathsSnapshot[i], 65536, 458752, out content) || !LolCredentialParser.TryParseCommandLine(content, out port, out token))
				{
					continue;
				}
				string item = port + "\n" + token;
				if (!seen.Add(item) || !LolRuntimeProcesses.IsOwnedLcuListenerPort(lolRoot, port))
				{
					continue;
				}
				LolLcuCredentials lolLcuCredentials = new LolLcuCredentials(port, token);
				probed++;
				try
				{
					if (probe(lolLcuCredentials))
					{
						return lolLcuCredentials;
					}
				}
				catch
				{
				}
			}
			return null;
		}

		internal static string[] OrderExistingCredentialFiles(string[] paths)
		{
			if (paths == null || paths.Length == 0)
			{
				return new string[0];
			}
			List<FileInfo> list = new List<FileInfo>(paths.Length);
			for (int i = 0; i < paths.Length; i++)
			{
				try
				{
					FileInfo fileInfo = new FileInfo(paths[i]);
					if (fileInfo.Exists)
					{
						list.Add(fileInfo);
					}
				}
				catch
				{
				}
			}
			list.Sort(delegate(FileInfo left, FileInfo right)
			{
				int compareToResult = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
				return (compareToResult == 0) ? StringComparer.OrdinalIgnoreCase.Compare(left.FullName, right.FullName) : compareToResult;
			});
			string[] textEntries = new string[list.Count];
			for (int j = 0; j < list.Count; j++)
			{
				textEntries[j] = list[j].FullName;
			}
			return textEntries;
		}

		internal static LolLcuCredentials SelectReachable(IList<LolLcuCredentials> candidates, Func<LolLcuCredentials, bool> probe, int maximumProbes, out int probed)
		{
			return SelectReachable(candidates, probe, maximumProbes, new HashSet<string>(StringComparer.Ordinal), out probed);
		}

		internal static LolLcuCredentials SelectReachable(IList<LolLcuCredentials> candidates, Func<LolLcuCredentials, bool> probe, int maximumProbes, HashSet<string> seen, out int probed)
		{
			probed = 0;
			if (candidates == null || probe == null || maximumProbes <= 0)
			{
				return null;
			}
			if (seen == null)
			{
				seen = new HashSet<string>(StringComparer.Ordinal);
			}
			for (int i = 0; i < candidates.Count; i++)
			{
				LolLcuCredentials lolLcuCredentials = candidates[i];
				if (lolLcuCredentials == null || lolLcuCredentials.Port <= 0 || string.IsNullOrEmpty(lolLcuCredentials.Token))
				{
					continue;
				}
				if (probed >= maximumProbes)
				{
					break;
				}
				string item = lolLcuCredentials.Port + "\n" + lolLcuCredentials.Token;
				if (!seen.Add(item))
				{
					continue;
				}
				probed++;
				try
				{
					if (probe(lolLcuCredentials))
					{
						return lolLcuCredentials;
					}
				}
				catch
				{
				}
			}
			return null;
		}

		internal static string[] SelectRecentLogPaths(string lolRoot, int limit)
		{
			if (string.IsNullOrEmpty(lolRoot) || limit <= 0)
			{
				return new string[0];
			}
			List<FileInfo> list = new List<FileInfo>(limit);
			AddRecentLogs(list, Path.Combine(lolRoot, "LeagueClient"), limit);
			AddRecentLogs(list, Path.Combine(lolRoot, "LeagueClient", "Logs"), limit);
			string[] textEntries = new string[list.Count];
			for (int i = 0; i < list.Count; i++)
			{
				textEntries[i] = list[i].FullName;
			}
			return textEntries;
		}

		private static void AddRecentLogs(List<FileInfo> output, string directory, int limit)
		{
			try
			{
				if (!Directory.Exists(directory))
				{
					return;
				}
				foreach (string item in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
				{
					string fileName = Path.GetFileName(item);
					if (fileName.IndexOf("LeagueClient", StringComparison.OrdinalIgnoreCase) < 0 || fileName.IndexOf("LeagueClientUxHelper", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						continue;
					}
					FileInfo fileInfo = new FileInfo(item);
					int i;
					for (i = 0; i < output.Count && output[i].LastWriteTimeUtc >= fileInfo.LastWriteTimeUtc; i++)
					{
					}
					if (i < limit)
					{
						output.Insert(i, fileInfo);
						if (output.Count > limit)
						{
							output.RemoveAt(output.Count - 1);
						}
					}
				}
			}
			catch
			{
			}
		}

		private static bool TryReadTail(string path, int maximumBytes, out string content)
		{
			content = null;
			try
			{
				if (!File.Exists(path))
				{
					return false;
				}
				using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					long lowerBound = Math.Max(0L, fileStream.Length - maximumBytes);
					fileStream.Seek(lowerBound, SeekOrigin.Begin);
					using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true))
					{
						if (lowerBound > 0)
						{
							streamReader.ReadLine();
						}
						content = streamReader.ReadToEnd();
					}
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryReadHeadAndTail(string path, int headBytes, int tailBytes, out string content)
		{
			content = null;
			if (headBytes <= 0 || tailBytes < 0)
			{
				return false;
			}
			try
			{
				using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					long headBytesOffset = (long)headBytes + (long)tailBytes;
					if (fileStream.Length <= headBytesOffset)
					{
						using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true))
						{
							content = streamReader.ReadToEnd();
						}
						return true;
					}
					byte[] rawBytes = new byte[headBytes];
					int count = ReadAtMost(fileStream, rawBytes, rawBytes.Length);
					fileStream.Seek(-tailBytes, SeekOrigin.End);
					byte[] rawBytes2 = new byte[tailBytes];
					int count2 = ReadAtMost(fileStream, rawBytes2, rawBytes2.Length);
					string stringValue = Encoding.UTF8.GetString(rawBytes, 0, count);
					if (stringValue.Length > 0 && stringValue[0] == '\ufeff')
					{
						stringValue = stringValue.Substring(1);
					}
					content = stringValue + Environment.NewLine + Encoding.UTF8.GetString(rawBytes2, 0, count2);
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		private static int ReadAtMost(Stream stream, byte[] buffer, int count)
		{
			int i;
			int atMostValue;
			for (i = 0; i < count; i += atMostValue)
			{
				atMostValue = stream.Read(buffer, i, count - i);
				if (atMostValue <= 0)
				{
					break;
				}
			}
			return i;
		}
	}
}
