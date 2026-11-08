using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class GameInstallScope
	{
		private sealed class Snapshot
		{
			internal readonly HashSet<string> Roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			internal readonly HashSet<string> Platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			internal readonly bool Complete;

			internal Snapshot(IList<GameInstallRecord> records, IList<string> platformRoots, bool complete = true)
			{
				int recordsCount = ((records != null) ? records.Count : 0);
				Complete = complete && recordsCount <= 8192;
				for (int i = 0; i < Math.Min(recordsCount, 8192); i++)
				{
					string rootFromRecordResult = RootFromRecord(records[i]);
					if (rootFromRecordResult != null && !UnsafeRoot(rootFromRecordResult))
					{
						Roots.Add(rootFromRecordResult);
					}
				}
				if (platformRoots == null)
				{
					return;
				}
				foreach (string platformRoot in platformRoots)
				{
					string normalizeResult = Normalize(platformRoot);
					if (normalizeResult != null)
					{
						Platforms.Add(normalizeResult);
					}
				}
			}
		}

		private const int MaxRecords = 8192;

		private const long CacheTicks = 1200000000L;

		private static readonly object sync = new object();

		private static Snapshot cached;

		private static long cachedAt;

		private static readonly Environment.SpecialFolder[] BroadFolders = new Environment.SpecialFolder[20]
		{
			Environment.SpecialFolder.ProgramFiles,
			Environment.SpecialFolder.ProgramFilesX86,
			Environment.SpecialFolder.CommonProgramFiles,
			Environment.SpecialFolder.CommonProgramFilesX86,
			Environment.SpecialFolder.CommonApplicationData,
			Environment.SpecialFolder.ApplicationData,
			Environment.SpecialFolder.LocalApplicationData,
			Environment.SpecialFolder.UserProfile,
			Environment.SpecialFolder.DesktopDirectory,
			Environment.SpecialFolder.CommonDesktopDirectory,
			Environment.SpecialFolder.Personal,
			Environment.SpecialFolder.CommonDocuments,
			Environment.SpecialFolder.MyMusic,
			Environment.SpecialFolder.MyPictures,
			Environment.SpecialFolder.MyVideos,
			Environment.SpecialFolder.CommonMusic,
			Environment.SpecialFolder.CommonPictures,
			Environment.SpecialFolder.CommonVideos,
			Environment.SpecialFolder.System,
			Environment.SpecialFolder.SystemX86
		};

		private static readonly HashSet<string> KnownBroadRoots = BuildBroadRoots();

		private static readonly string KnownWindowsRoot = KnownFolder(Environment.SpecialFolder.Windows);

		internal static string Resolve(string executablePath, string fallbackRoot)
		{
			string normalizeResult = Normalize(executablePath);
			if (normalizeResult == null || !normalizeResult.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return fallbackRoot;
			}
			try
			{
				Snapshot snapshot = CurrentSnapshot();
				return SelectSnapshotRoot(normalizeResult, fallbackRoot, snapshot, Directory.Exists, IsSafeExistingPath);
			}
			catch
			{
				return fallbackRoot;
			}
		}

		internal static string RestrictFallback(string executablePath, string fallbackRoot)
		{
			string normalizeResult = Normalize(executablePath);
			string normalizeResult2 = Normalize(fallbackRoot);
			if (normalizeResult == null || !normalizeResult.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || normalizeResult2 == null || !Under(normalizeResult, normalizeResult2) || UnsafeRoot(normalizeResult2))
			{
				return null;
			}
			try
			{
				Snapshot snapshot = CurrentSnapshot();
				if (ContainsPlatform(normalizeResult2, snapshot.Platforms) || ContainsOtherInstall(normalizeResult2, normalizeResult, snapshot.Roots))
				{
					return null;
				}
			}
			catch
			{
			}
			return normalizeResult2;
		}

		internal static string SelectRoot(string executablePath, string fallbackRoot, IList<GameInstallRecord> records, IList<string> platformRoots, Func<string, bool> directoryExists, Func<string, bool> safePath)
		{
			string normalizeResult = Normalize(executablePath);
			if (normalizeResult == null || !normalizeResult.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || records == null || records.Count > 8192 || directoryExists == null || safePath == null)
			{
				return fallbackRoot;
			}
			try
			{
				return SelectSnapshotRoot(normalizeResult, fallbackRoot, new Snapshot(records, platformRoots), directoryExists, safePath);
			}
			catch
			{
				return fallbackRoot;
			}
		}

		private static string SelectSnapshotRoot(string executable, string fallbackRoot, Snapshot snapshot, Func<string, bool> directoryExists, Func<string, bool> safePath)
		{
			try
			{
				if (!snapshot.Complete || !safePath(executable))
				{
					return fallbackRoot;
				}
				string rootSnapshot = null;
				foreach (string root in snapshot.Roots)
				{
					if (Under(executable, root) && (rootSnapshot == null || root.Length > rootSnapshot.Length) && !ContainsPlatform(root, snapshot.Platforms) && !ContainsOtherInstall(root, executable, snapshot.Roots) && directoryExists(root) && safePath(root))
					{
						rootSnapshot = root;
					}
				}
				return rootSnapshot ?? fallbackRoot;
			}
			catch
			{
				return fallbackRoot;
			}
		}

		private static string RootFromRecord(GameInstallRecord record)
		{
			if (record == null)
			{
				return null;
			}
			if (!string.IsNullOrWhiteSpace(record.InstallLocation))
			{
				return Normalize(record.InstallLocation);
			}
			string normalizeResult = Normalize(record.InstallSource);
			if (normalizeResult == null)
			{
				return null;
			}
			string b = UninstallDirectory(record.UninstallString);
			if (!string.Equals(normalizeResult, b, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			return normalizeResult;
		}

		private static string UninstallDirectory(string command)
		{
			if (string.IsNullOrWhiteSpace(command) || command.Length > 32767)
			{
				return null;
			}
			string commandTrimmed = command.Trim();
			string raw = null;
			if (commandTrimmed[0] == '"')
			{
				int matchIndex = commandTrimmed.IndexOf('"', 1);
				if (matchIndex > 1)
				{
					raw = commandTrimmed.Substring(1, matchIndex - 1);
				}
			}
			else
			{
				int uninstallDirectoryValue = 0;
				while (uninstallDirectoryValue < commandTrimmed.Length)
				{
					int matchIndex2 = commandTrimmed.IndexOf(".exe", uninstallDirectoryValue, StringComparison.OrdinalIgnoreCase);
					if (matchIndex2 < 0)
					{
						break;
					}
					matchIndex2 += 4;
					if (matchIndex2 == commandTrimmed.Length || char.IsWhiteSpace(commandTrimmed[matchIndex2]))
					{
						raw = commandTrimmed.Substring(0, matchIndex2);
						break;
					}
					uninstallDirectoryValue = matchIndex2;
				}
			}
			raw = Normalize(raw);
			if (raw == null || !raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			try
			{
				return Normalize(Path.GetDirectoryName(raw));
			}
			catch
			{
				return null;
			}
		}

		private static string Normalize(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32767)
			{
				return null;
			}
			try
			{
				string replaceResult = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).Replace('/', '\\');
				if (replaceResult.Length < 3 || !char.IsLetter(replaceResult[0]) || replaceResult[1] != ':' || replaceResult[2] != '\\')
				{
					return null;
				}
				for (int i = 0; i < replaceResult.Length; i++)
				{
					if (char.IsControl(replaceResult[i]) || replaceResult[i] == '"' || replaceResult[i] == '*' || replaceResult[i] == '?' || replaceResult[i] == '|' || replaceResult[i] == '<' || replaceResult[i] == '>' || (replaceResult[i] == ':' && i != 1))
					{
						return null;
					}
				}
				return Path.GetFullPath(replaceResult).TrimEnd('\\');
			}
			catch
			{
				return null;
			}
		}

		private static bool Under(string path, string root)
		{
			if (path != null && root != null)
			{
				return path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static string KnownFolder(Environment.SpecialFolder folder)
		{
			try
			{
				return Normalize(Environment.GetFolderPath(folder));
			}
			catch
			{
				return null;
			}
		}

		private static HashSet<string> BuildBroadRoots()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Environment.SpecialFolder[] broadFolders = BroadFolders;
			foreach (Environment.SpecialFolder folder in broadFolders)
			{
				string knownFolderResult = KnownFolder(folder);
				if (knownFolderResult != null)
				{
					hashSet.Add(knownFolderResult);
				}
			}
			try
			{
				string normalizeResult = Normalize(Environment.GetEnvironmentVariable("PUBLIC"));
				if (normalizeResult != null)
				{
					hashSet.Add(normalizeResult);
				}
			}
			catch
			{
			}
			try
			{
				string normalizeResult2 = Normalize(Path.GetTempPath());
				if (normalizeResult2 != null)
				{
					hashSet.Add(normalizeResult2);
				}
			}
			catch
			{
			}
			return hashSet;
		}

		private static bool UnsafeRoot(string root)
		{
			if (root.Length <= 3)
			{
				return true;
			}
			if (string.Equals(root, KnownWindowsRoot, StringComparison.OrdinalIgnoreCase) || Under(root, KnownWindowsRoot) || KnownBroadRoots.Contains(root))
			{
				return true;
			}
			string fileName = Path.GetFileName(root);
			if (!string.Equals(fileName, "Users", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "Games", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "Game", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "Downloads", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "SteamLibrary", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "steamapps", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "common", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "WeGameApps", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileName, "XboxGames", StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(fileName, "WindowsApps", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		private static bool ContainsPlatform(string root, HashSet<string> platforms)
		{
			foreach (string platform in platforms)
			{
				if (string.Equals(root, platform, StringComparison.OrdinalIgnoreCase) || Under(platform, root))
				{
					return true;
				}
			}
			return false;
		}

		private static bool ContainsOtherInstall(string root, string executable, HashSet<string> roots)
		{
			foreach (string root2 in roots)
			{
				if (Under(root2, root) && !Under(executable, root2))
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsSafeExistingPath(string path)
		{
			return IsSafePath(path, File.GetAttributes);
		}

		internal static bool IsSafePath(string path, Func<string, FileAttributes> attributes)
		{
			if (attributes == null)
			{
				return false;
			}
			try
			{
				string pathSnapshot = path;
				for (int i = 0; i < 128; i++)
				{
					if (string.IsNullOrEmpty(pathSnapshot))
					{
						break;
					}
					if ((attributes(pathSnapshot) & FileAttributes.ReparsePoint) != 0)
					{
						return false;
					}
					string directoryName = Path.GetDirectoryName(pathSnapshot);
					if (string.IsNullOrEmpty(directoryName))
					{
						return true;
					}
					pathSnapshot = directoryName;
				}
			}
			catch
			{
			}
			return false;
		}

		private static Snapshot CurrentSnapshot()
		{
			lock (sync)
			{
				long ticks = DateTime.UtcNow.Ticks;
				if (cached != null && ticks >= cachedAt && ticks - cachedAt < 1200000000)
				{
					return cached;
				}
				List<GameInstallRecord> list = new List<GameInstallRecord>();
				RegistryView[] registryViewItems = (Environment.Is64BitOperatingSystem ? new RegistryView[2]
				{
					RegistryView.Registry64,
					RegistryView.Registry32
				} : new RegistryView[1] { RegistryView.Registry32 });
				RegistryView[] registryViewItems2 = registryViewItems;
				foreach (RegistryView view in registryViewItems2)
				{
					ReadUninstallHive(RegistryHive.LocalMachine, view, list);
					ReadUninstallHive(RegistryHive.CurrentUser, view, list);
					if (list.Count > 8192)
					{
						break;
					}
				}
				List<string> list2 = new List<string>();
				bool complete = list.Count <= 8192;
				try
				{
					foreach (string item in GamePlatformCatalog.DetectedPlatforms())
					{
						list2.AddRange(GamePlatformCatalog.ResolvedRoots(item));
					}
				}
				catch
				{
					complete = false;
				}
				cached = new Snapshot(list, list2, complete);
				cachedAt = ticks;
				return cached;
			}
		}

		private static void ReadUninstallHive(RegistryHive hive, RegistryView view, List<GameInstallRecord> records)
		{
			if (records.Count > 8192)
			{
				return;
			}
			try
			{
				using (RegistryKey registryKey = RegistryKey.OpenBaseKey(hive, view))
				{
					using (RegistryKey registryKey2 = registryKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"))
					{
						if (registryKey2 == null)
						{
							return;
						}
						string[] subKeyNames = registryKey2.GetSubKeyNames();
						foreach (string name in subKeyNames)
						{
							if (records.Count >= 8192)
							{
								records.Add(null);
								break;
							}
							try
							{
								using (RegistryKey registryKey3 = registryKey2.OpenSubKey(name))
								{
									if (registryKey3 != null && registryKey3.GetValue("ParentKeyName") == null)
									{
										object value = registryKey3.GetValue("SystemComponent");
										if (!(value is int) || (int)value == 0)
										{
											records.Add(new GameInstallRecord
											{
												InstallLocation = (registryKey3.GetValue("InstallLocation") as string),
												InstallSource = (registryKey3.GetValue("InstallSource") as string),
												UninstallString = (registryKey3.GetValue("UninstallString") as string)
											});
										}
									}
								}
							}
							catch
							{
							}
						}
					}
				}
			}
			catch
			{
			}
		}
	}
}
