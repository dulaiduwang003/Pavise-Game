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
			string text = FindLolFromProcesses();
			if (text != null)
			{
				return text;
			}
			text = NormalizeLolRoot(preferred);
			if (text != null)
			{
				return text;
			}
			for (int i = 0; i < LolRegistryKeys.Length; i++)
			{
				text = FindRegistryLolRoot(Registry.CurrentUser, LolRegistryKeys[i]);
				if (text != null)
				{
					return text;
				}
				text = FindRegistryLolRoot(Registry.LocalMachine, LolRegistryKeys[i]);
				if (text != null)
				{
					return text;
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
			string text = FindWeGameFromProcesses();
			if (text != null)
			{
				return text;
			}
			text = NormalizeWeGameRoot(preferred);
			if (text != null)
			{
				return text;
			}
			text = FindWeGameFromAppPaths();
			if (text != null)
			{
				return text;
			}
			for (int i = 0; i < WeGameRegistryKeys.Length; i++)
			{
				text = FindRegistryWeGameRoot(Registry.CurrentUser, WeGameRegistryKeys[i]);
				if (text != null)
				{
					return text;
				}
				text = FindRegistryWeGameRoot(Registry.LocalMachine, WeGameRegistryKeys[i]);
				if (text != null)
				{
					return text;
				}
			}
			text = FindWeGameFromLaunchFile(lolRoot);
			if (text != null)
			{
				return text;
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
			string[] array = new string[4]
			{
				Path.Combine(root, "wegame.exe"),
				Path.Combine(root, "WeGame.exe"),
				Path.Combine(root, "WeGameLauncher.exe"),
				Path.Combine(root, "apps", "wegame.exe")
			};
			for (int i = 0; i < array.Length; i++)
			{
				if (File.Exists(array[i]))
				{
					return array[i];
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
						string text = NormalizeLolRoot(Convert.ToString(registryKey.GetValue(RegistryValues[i])));
						if (text != null)
						{
							return text;
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
						string text = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue(RegistryValues[i])));
						if (text != null)
						{
							return text;
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
			string[] array = new string[2] { "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths\\wegame.exe", "Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths\\wegame.exe" };
			RegistryKey[] array2 = new RegistryKey[2]
			{
				Registry.CurrentUser,
				Registry.LocalMachine
			};
			for (int i = 0; i < array2.Length; i++)
			{
				for (int j = 0; j < array.Length; j++)
				{
					try
					{
						using (RegistryKey registryKey = array2[i].OpenSubKey(array[j]))
						{
							if (registryKey != null)
							{
								string text = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue(null)));
								if (text != null)
								{
									return text;
								}
								text = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue("Path")));
								if (text != null)
								{
									return text;
								}
								text = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue("ExeFile")));
								if (text != null)
								{
									return text;
								}
								text = NormalizeWeGameRoot(Convert.ToString(registryKey.GetValue("InstallPath")));
								if (text != null)
								{
									return text;
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
					Process[] array = processesByName;
					foreach (Process process in array)
					{
						string path;
						if (LolRuntimeProcesses.TryGetOwnedImagePath(process, sessionId, out path) && candidateName(Path.GetFileName(path)))
						{
							string text = normalize(path);
							if (text != null)
							{
								return text;
							}
						}
					}
				}
				finally
				{
					Process[] array2 = processesByName;
					foreach (Process process2 in array2)
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
					string[] array = new string[4]
					{
						Path.Combine(fullName, "WeGameApps", "英雄联盟"),
						Path.Combine(fullName, "Program Files", "WeGameApps", "英雄联盟"),
						Path.Combine(fullName, "Program Files (x86)", "WeGameApps", "英雄联盟"),
						Path.Combine(fullName, "英雄联盟")
					};
					for (int j = 0; j < array.Length; j++)
					{
						if (cancelled != null && cancelled())
						{
							return null;
						}
						string text = NormalizeLolRoot(array[j]);
						if (text != null)
						{
							return text;
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
				string text = NormalizeWeGameRoot(list[j]);
				if (text != null)
				{
					return text;
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
			string[] array = new string[2]
			{
				Path.Combine(lolRoot, "TCLS", "wegame_launch.ini"),
				Path.Combine(lolRoot, "TCLS", "wegame_launch.tmp")
			};
			for (int i = 0; i < array.Length; i++)
			{
				string content;
				if (!TryReadSmall(array[i], out content))
				{
					continue;
				}
				MatchCollection matchCollection = Regex.Matches(content, "[A-Za-z]:\\\\[^\\\"\\r\\n]*?\\\\(?:wegame\\.exe|WeGameLauncher\\.exe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
				foreach (Match item in matchCollection)
				{
					string text = NormalizeWeGameRoot(item.Value);
					if (text != null)
					{
						return text;
					}
				}
			}
			return null;
		}

		private static string NormalizeLolRoot(string value)
		{
			string text = NormalizePath(value);
			if (text == null)
			{
				return null;
			}
			if (File.Exists(text))
			{
				text = Path.GetDirectoryName(text);
			}
			DirectoryInfo directoryInfo;
			try
			{
				directoryInfo = new DirectoryInfo(text);
			}
			catch
			{
				return null;
			}
			int num = 0;
			while (directoryInfo != null && num < 9)
			{
				if (IsValidLolRoot(directoryInfo.FullName))
				{
					return directoryInfo.FullName.TrimEnd('\\');
				}
				num++;
				directoryInfo = directoryInfo.Parent;
			}
			return null;
		}

		private static string NormalizeWeGameRoot(string value)
		{
			string text = NormalizePath(value);
			if (text == null)
			{
				return null;
			}
			try
			{
				if (File.Exists(text))
				{
					text = Path.GetDirectoryName(text);
				}
				DirectoryInfo directoryInfo = new DirectoryInfo(text);
				int num = 0;
				while (directoryInfo != null && num < 6)
				{
					if (FindWeGameExecutableUnchecked(directoryInfo.FullName) != null)
					{
						if (IsVolumeRoot(directoryInfo))
						{
							return null;
						}
						return directoryInfo.FullName.TrimEnd('\\');
					}
					num++;
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
				string text = Path.Combine(root, "wegame.exe");
				if (File.Exists(text))
				{
					return text;
				}
				text = Path.Combine(root, "WeGame.exe");
				if (File.Exists(text))
				{
					return text;
				}
				text = Path.Combine(root, "WeGameLauncher.exe");
				if (File.Exists(text))
				{
					return text;
				}
				text = Path.Combine(root, "apps", "wegame.exe");
				return File.Exists(text) ? text : null;
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
