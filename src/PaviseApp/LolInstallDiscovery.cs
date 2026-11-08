using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class LolInstallDiscovery
	{
		private static readonly string[] LolRegistryKeys = new string[3] { "Software\\Tencent\\LOL", "Software\\WOW6432Node\\Tencent\\LOL", "Software\\Tencent\\WeGame\\LOL" };

		private static readonly string[] WeGameRegistryKeys = new string[4] { "Software\\Tencent\\WeGame", "Software\\WOW6432Node\\Tencent\\WeGame", "Software\\Tencent\\GameAssistant", "Software\\WOW6432Node\\Tencent\\GameAssistant" };

		private static readonly string[] RegistryValues = new string[4] { "InstallPath", "InstallDir", "Path", "InstallLocation" };

		public static string FindLolRoot(string preferred)
		{
			return FindLolRoot(preferred, true, null);
		}

		public static string FindLolRoot(string preferred, bool deep, Func<bool> cancelled)
		{
			string lolFromProcesses = FindLolFromProcesses();
			if (lolFromProcesses != null)
			{
				return lolFromProcesses;
			}
			lolFromProcesses = NormalizeLolRoot(preferred);
			if (lolFromProcesses != null)
			{
				return lolFromProcesses;
			}
			for (int i = 0; i < LolRegistryKeys.Length; i++)
			{
				lolFromProcesses = FindRegistryLolRoot(Registry.CurrentUser, LolRegistryKeys[i]);
				if (lolFromProcesses != null)
				{
					return lolFromProcesses;
				}
				lolFromProcesses = FindRegistryLolRoot(Registry.LocalMachine, LolRegistryKeys[i]);
				if (lolFromProcesses != null)
				{
					return lolFromProcesses;
				}
			}
			if (!deep || (cancelled != null && cancelled()))
			{
				return null;
			}
			return FindCommonLolRoot(cancelled);
		}

		public static string FindWeGameRoot(string preferred, string lolRoot)
		{
			return FindWeGameRoot(preferred, lolRoot, true, null);
		}

		public static string FindWeGameRoot(string preferred, string lolRoot, bool deep, Func<bool> cancelled)
		{
			string weGameFromProcesses = FindWeGameFromProcesses();
			if (weGameFromProcesses != null)
			{
				return weGameFromProcesses;
			}
			weGameFromProcesses = NormalizeWeGameRoot(preferred);
			if (weGameFromProcesses != null)
			{
				return weGameFromProcesses;
			}
			weGameFromProcesses = FindWeGameFromAppPaths();
			if (weGameFromProcesses != null)
			{
				return weGameFromProcesses;
			}
			for (int i = 0; i < WeGameRegistryKeys.Length; i++)
			{
				weGameFromProcesses = FindRegistryWeGameRoot(Registry.CurrentUser, WeGameRegistryKeys[i]);
				if (weGameFromProcesses != null)
				{
					return weGameFromProcesses;
				}
				weGameFromProcesses = FindRegistryWeGameRoot(Registry.LocalMachine, WeGameRegistryKeys[i]);
				if (weGameFromProcesses != null)
				{
					return weGameFromProcesses;
				}
			}
			weGameFromProcesses = FindWeGameFromLaunchFile(lolRoot);
			if (weGameFromProcesses != null)
			{
				return weGameFromProcesses;
			}
			if (!deep || (cancelled != null && cancelled()))
			{
				return null;
			}
			return FindCommonWeGameRoot(cancelled);
		}

		public static string FindWeGameExecutable(string root)
		{
			root = NormalizeWeGameRoot(root);
			if (root == null)
			{
				return null;
			}
			string[] textEntries = new string[4]
			{
				Path.Combine(root, "wegame.exe"),
				Path.Combine(root, "WeGame.exe"),
				Path.Combine(root, "WeGameLauncher.exe"),
				Path.Combine(root, "apps", "wegame.exe")
			};
			for (int i = 0; i < textEntries.Length; i++)
			{
				if (File.Exists(textEntries[i]))
				{
					return textEntries[i];
				}
			}
			try
			{
				string[] files = Directory.GetFiles(root, "wegame.exe", SearchOption.TopDirectoryOnly);
				if (files.Length > 0)
				{
					return files[0];
				}
			}
			catch
			{
			}
			return null;
		}

		public static bool IsValidLolRoot(string root)
		{
			try
			{
				if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
				{
					return false;
				}
				return (File.Exists(Path.Combine(root, "LeagueClient", "LeagueClient.exe")) || File.Exists(Path.Combine(root, "LeagueClient.exe"))) && Directory.Exists(Path.Combine(root, "Game"));
			}
			catch
			{
				return false;
			}
		}

		public static bool IsValidWeGameRoot(string root)
		{
			return FindWeGameExecutableUnchecked(root) != null;
		}

		private static string FindRegistryLolRoot(RegistryKey hive, string subKey)
		{
			try
			{
				using (RegistryKey registryKey = hive.OpenSubKey(subKey))
				{
					if (registryKey == null)
					{
						return null;
					}
					for (int i = 0; i < RegistryValues.Length; i++)
					{
						string lolRoot = NormalizeLolRoot(Convert.ToString(registryKey.GetValue(RegistryValues[i])));
						if (lolRoot != null)
						{
							return lolRoot;
						}
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static string FindRegistryWeGameRoot(RegistryKey hive, string subKey)
		{
			try
			{
				using (RegistryKey registryKey = hive.OpenSubKey(subKey))
				{
					if (registryKey == null)
					{
						return null;
					}
					for (int i = 0; i < RegistryValues.Length; i++)
					{
						string weGameRoot = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue(RegistryValues[i])));
						if (weGameRoot != null)
						{
							return weGameRoot;
						}
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static string FindWeGameFromAppPaths()
		{
			string[] textEntries = new string[2] { "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\wegame.exe", "Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths\\wegame.exe" };
			RegistryKey[] registryKeyItems = new RegistryKey[2]
			{
				Registry.CurrentUser,
				Registry.LocalMachine
			};
			for (int i = 0; i < registryKeyItems.Length; i++)
			{
				for (int j = 0; j < textEntries.Length; j++)
				{
					try
					{
						using (RegistryKey registryKey = registryKeyItems[i].OpenSubKey(textEntries[j]))
						{
							if (registryKey != null)
							{
								string weGameRoot = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue(null)));
								if (weGameRoot != null)
								{
									return weGameRoot;
								}
								weGameRoot = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue("Path")));
								if (weGameRoot != null)
								{
									return weGameRoot;
								}
								weGameRoot = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue("ExeFile")));
								if (weGameRoot != null)
								{
									return weGameRoot;
								}
								weGameRoot = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue("InstallPath")));
								if (weGameRoot != null)
								{
									return weGameRoot;
								}
							}
						}
					}
					catch
					{
					}
				}
			}
			return null;
		}

		private static string FindLolFromProcesses()
		{
			return FindRootFromProcesses(new string[3] { "LeagueClient", "LeagueClientUx", "League of Legends" }, LolRuntimeProcesses.IsCoreIdentityCandidateName, NormalizeLolRoot);
		}

		private static string FindWeGameFromProcesses()
		{
			return FindRootFromProcesses(new string[3] { "wegame", "wegame_env", "wegameclient" }, LolRuntimeProcesses.IsWeGameDiscoveryCandidateName, NormalizeWeGameRoot);
		}

		private static string FindRootFromProcesses(string[] names, Func<string, bool> candidateName, Func<string, string> normalize)
		{
			int sessionId;
			if (!LolRuntimeProcesses.TryGetCurrentSessionId(out sessionId))
			{
				return null;
			}
			for (int i = 0; i < names.Length; i++)
			{
				Process[] processesByName;
				try
				{
					processesByName = Process.GetProcessesByName(names[i]);
				}
				catch
				{
					continue;
				}
				try
				{
					Process[] processesByNameSnapshot = processesByName;
					foreach (Process process in processesByNameSnapshot)
					{
						string path;
						if (LolRuntimeProcesses.TryGetOwnedImagePath(process, sessionId, out path) && candidateName(Path.GetFileName(path)))
						{
							string normalizeResult = normalize(path);
							if (normalizeResult != null)
							{
								return normalizeResult;
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
			}
			return null;
		}

		private static string FindCommonLolRoot(Func<bool> cancelled)
		{
			try
			{
				DriveInfo[] drives = DriveInfo.GetDrives();
				foreach (DriveInfo driveInfo in drives)
				{
					if (cancelled != null && cancelled())
					{
						return null;
					}
					if (!driveInfo.IsReady || driveInfo.DriveType != DriveType.Fixed)
					{
						continue;
					}
					string fullName = driveInfo.RootDirectory.FullName;
					string[] textEntries = new string[4]
					{
						Path.Combine(fullName, "WeGameApps", "英雄联盟"),
						Path.Combine(fullName, "Program Files", "WeGameApps", "英雄联盟"),
						Path.Combine(fullName, "Program Files (x86)", "WeGameApps", "英雄联盟"),
						Path.Combine(fullName, "英雄联盟")
					};
					for (int j = 0; j < textEntries.Length; j++)
					{
						if (cancelled != null && cancelled())
						{
							return null;
						}
						string lolRoot = NormalizeLolRoot(textEntries[j]);
						if (lolRoot != null)
						{
							return lolRoot;
						}
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static string FindCommonWeGameRoot(Func<bool> cancelled)
		{
			List<string> list = new List<string>();
			try
			{
				list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WeGame"));
				list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WeGame"));
				DriveInfo[] drives = DriveInfo.GetDrives();
				foreach (DriveInfo driveInfo in drives)
				{
					if (driveInfo.IsReady && driveInfo.DriveType == DriveType.Fixed)
					{
						list.Add(Path.Combine(driveInfo.RootDirectory.FullName, "Program Files (x86)", "WeGame"));
						list.Add(Path.Combine(driveInfo.RootDirectory.FullName, "Program Files", "WeGame"));
						list.Add(Path.Combine(driveInfo.RootDirectory.FullName, "WeGame"));
					}
				}
			}
			catch
			{
			}
			for (int j = 0; j < list.Count; j++)
			{
				if (cancelled != null && cancelled())
				{
					return null;
				}
				string weGameRoot = NormalizeWeGameRoot(list[j]);
				if (weGameRoot != null)
				{
					return weGameRoot;
				}
			}
			return null;
		}

		private static string FindWeGameFromLaunchFile(string lolRoot)
		{
			if (!IsValidLolRoot(lolRoot))
			{
				return null;
			}
			string[] textEntries = new string[2]
			{
				Path.Combine(lolRoot, "TCLS", "wegame_launch.ini"),
				Path.Combine(lolRoot, "TCLS", "wegame_launch.tmp")
			};
			for (int i = 0; i < textEntries.Length; i++)
			{
				string content;
				if (!TryReadSmall(textEntries[i], out content))
				{
					continue;
				}
				MatchCollection matchCollection = Regex.Matches(content, "[A-Za-z]:\\\\[^\\\"\\r\\n]*?\\\\(?:wegame\\.exe|WeGameLauncher\\.exe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
				foreach (Match item in matchCollection)
				{
					string weGameRoot = NormalizeWeGameRoot(item.Value);
					if (weGameRoot != null)
					{
						return weGameRoot;
					}
				}
			}
			return null;
		}

		private static string NormalizeLolRoot(string value)
		{
			string path = NormalizePath(value);
			if (path == null)
			{
				return null;
			}
			if (File.Exists(path))
			{
				path = Path.GetDirectoryName(path);
			}
			DirectoryInfo directoryInfo;
			try
			{
				directoryInfo = new DirectoryInfo(path);
			}
			catch
			{
				return null;
			}
			int processedCount = 0;
			while (directoryInfo != null && processedCount < 9)
			{
				if (IsValidLolRoot(directoryInfo.FullName))
				{
					return directoryInfo.FullName.TrimEnd('\\');
				}
				processedCount++;
				directoryInfo = directoryInfo.Parent;
			}
			return null;
		}

		private static string NormalizeWeGameRoot(string value)
		{
			string path = NormalizePath(value);
			if (path == null)
			{
				return null;
			}
			try
			{
				if (File.Exists(path))
				{
					path = Path.GetDirectoryName(path);
				}
				DirectoryInfo directoryInfo = new DirectoryInfo(path);
				int processedCount = 0;
				while (directoryInfo != null && processedCount < 6)
				{
					if (FindWeGameExecutableUnchecked(directoryInfo.FullName) != null)
					{
						if (IsVolumeRoot(directoryInfo))
						{
							return null;
						}
						return directoryInfo.FullName.TrimEnd('\\');
					}
					processedCount++;
					directoryInfo = directoryInfo.Parent;
				}
			}
			catch
			{
			}
			return null;
		}

		private static bool IsVolumeRoot(DirectoryInfo directory)
		{
			if (directory == null)
			{
				return false;
			}
			try
			{
				return directory.Parent == null;
			}
			catch
			{
				return true;
			}
		}

		private static string FindWeGameExecutableUnchecked(string root)
		{
			if (string.IsNullOrEmpty(root))
			{
				return null;
			}
			try
			{
				string wegamePath = Path.Combine(root, "wegame.exe");
				if (File.Exists(wegamePath))
				{
					return wegamePath;
				}
				wegamePath = Path.Combine(root, "WeGame.exe");
				if (File.Exists(wegamePath))
				{
					return wegamePath;
				}
				wegamePath = Path.Combine(root, "WeGameLauncher.exe");
				if (File.Exists(wegamePath))
				{
					return wegamePath;
				}
				wegamePath = Path.Combine(root, "apps", "wegame.exe");
				return File.Exists(wegamePath) ? wegamePath : null;
			}
			catch
			{
				return null;
			}
		}

		private static string NormalizePath(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}
			try
			{
				string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
				return Path.GetFullPath(path);
			}
			catch
			{
				return null;
			}
		}

		private static bool TryReadSmall(string path, out string content)
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
					if (fileStream.Length > 1048576)
					{
						return false;
					}
					using (StreamReader streamReader = new StreamReader(fileStream, Encoding.UTF8, true))
					{
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
	}
}
