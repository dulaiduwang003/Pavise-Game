using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class GamePlatformCatalog
	{
		private sealed class Platform
		{
			public readonly string Id;

			public readonly string[] RegistryRoots;

			public readonly string[] FolderRoots;

			public readonly string[] UninstallTags;

			public List<string> Roots;

			public Platform(string id, string[] registryRoots, string[] folderRoots, string[] uninstallTags)
			{
				Id = id;
				RegistryRoots = registryRoots ?? new string[0];
				FolderRoots = folderRoots ?? new string[0];
				UninstallTags = uninstallTags ?? new string[0];
			}
		}

		private const string Pf = "pf";

		private const string Pf86 = "pf86";

		private const string Common = "common";

		private const string Common86 = "common86";

		private const string Data = "data";

		private const string Local = "local";

		private const string Roaming = "roaming";

		private const string Drive = "drive";

		private const int RootRefreshMs = 600000;

		private static readonly Platform[] Platforms = new Platform[18]
		{
			new Platform("Steam", new string[3] { "U|Software\\Valve\\Steam|SteamPath", "M|SOFTWARE\\WOW6432Node\\Valve\\Steam|InstallPath", "M|SOFTWARE\\Valve\\Steam|InstallPath" }, new string[4] { "common86|Steam", "common|Steam", "pf86|Steam", "pf|Steam" }, null),
			new Platform("Epic Games", null, new string[2] { "pf86|Epic Games\\Launcher", "pf|Epic Games\\Launcher" }, new string[1] { "Epic Games Launcher" }),
			new Platform("EA app", null, new string[7] { "pf|Electronic Arts\\EA Desktop", "pf86|Electronic Arts\\EA Desktop", "common|Electronic Arts", "common86|Electronic Arts", "data|Electronic Arts", "pf|Origin", "pf86|Origin" }, new string[2] { "EA app", "EA Desktop" }),
			new Platform("Ubisoft Connect", new string[2] { "M|SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher|InstallDir", "M|SOFTWARE\\Ubisoft\\Launcher|InstallDir" }, new string[2] { "pf86|Ubisoft\\Ubisoft Game Launcher", "pf|Ubisoft\\Ubisoft Game Launcher" }, new string[1] { "Ubisoft Connect" }),
			new Platform("Battle.net", null, new string[4] { "pf86|Battle.net", "pf|Battle.net", "data|Battle.net", "data|Blizzard Entertainment" }, new string[1] { "Battle.net" }),
			new Platform("GOG Galaxy", new string[2] { "M|SOFTWARE\\WOW6432Node\\GOG.com\\GalaxyClient\\paths|client", "M|SOFTWARE\\GOG.com\\GalaxyClient\\paths|client" }, new string[3] { "pf86|GOG Galaxy", "pf|GOG Galaxy", "data|GOG.com\\Galaxy" }, new string[1] { "GOG GALAXY" }),
			new Platform("Rockstar Games", new string[1] { "M|SOFTWARE\\WOW6432Node\\Rockstar Games\\Launcher|InstallFolder" }, new string[4] { "pf|Rockstar Games\\Launcher", "pf86|Rockstar Games\\Launcher", "pf|Rockstar Games\\Social Club", "pf86|Rockstar Games\\Social Club" }, new string[1] { "Rockstar Games Launcher" }),
			new Platform("Riot Client", null, new string[4] { "drive|Riot Games\\Riot Client", "pf|Riot Games\\Riot Client", "pf86|Riot Games\\Riot Client", "data|Riot Games" }, new string[1] { "Riot Client" }),
			new Platform("WeGame", null, new string[4] { "pf86|WeGame", "pf|WeGame", "pf86|Tencent\\WeGame", "pf|Tencent\\WeGame" }, new string[2] { "WeGame", "腾讯游戏平台" }),
			new Platform("Xbox", null, new string[1] { "pf|WindowsApps" }, null),
			new Platform("HoYoPlay", null, new string[6] { "pf|HoYoPlay", "pf86|HoYoPlay", "pf|miHoYo Launcher", "pf86|miHoYo Launcher", "pf|miHoYo", "pf86|miHoYo" }, new string[3] { "HoYoPlay", "miHoYo Launcher", "米哈游启动器" }),
			new Platform("Amazon Games", null, new string[2] { "local|Amazon Games", "pf|Amazon Games" }, new string[1] { "Amazon Games" }),
			new Platform("itch.io", null, new string[2] { "local|itch", "roaming|itch" }, null),
			new Platform("Garena", null, new string[3] { "pf86|Garena", "pf|Garena", "local|Garena" }, new string[1] { "Garena" }),
			new Platform("Nexon", null, new string[5] { "pf86|Nexon", "pf|Nexon", "data|NexonUS\\NGM", "data|NexonEU\\NGM", "data|Nexon" }, new string[2] { "Nexon Launcher", "Nexon Game Manager" }),
			new Platform("NCSOFT PURPLE", null, new string[4] { "pf|NCSOFT\\Purple", "pf86|NCSOFT\\Purple", "pf|NCSOFT", "pf86|NCSOFT" }, new string[1] { "NCSOFT" }),
			new Platform("DMM GAME PLAYER", null, new string[5] { "pf86|DMMGamePlayer", "pf|DMMGamePlayer", "local|DMMGamePlayer", "roaming|DMMGamePlayer", "roaming|DMMGamePlayerFastLauncher" }, new string[1] { "DMM Game" }),
			new Platform("NetEase", null, new string[5] { "pf|Netease", "pf86|Netease", "pf|NetEase Games", "pf86|NetEase Games", "data|Netease" }, new string[2] { "网易游戏", "NetEase Games" })
		};

		private static readonly object sync = new object();

		private static bool rootsResolved;

		private static long lastResolveTicks;

		private static readonly Environment.SpecialFolder[] BroadFolders = new Environment.SpecialFolder[8]
		{
			Environment.SpecialFolder.ProgramFiles,
			Environment.SpecialFolder.ProgramFilesX86,
			Environment.SpecialFolder.CommonProgramFiles,
			Environment.SpecialFolder.CommonProgramFilesX86,
			Environment.SpecialFolder.CommonApplicationData,
			Environment.SpecialFolder.LocalApplicationData,
			Environment.SpecialFolder.ApplicationData,
			Environment.SpecialFolder.UserProfile
		};

		internal static List<string> ResolvedRoots(string platformId)
		{
			EnsureRoots();
			List<string> list = new List<string>();
			lock (sync)
			{
				Platform[] platforms = Platforms;
				foreach (Platform platform in platforms)
				{
					if (string.Equals(platform.Id, platformId, StringComparison.OrdinalIgnoreCase) && platform.Roots != null)
					{
						list.AddRange(platform.Roots);
					}
				}
				return list;
			}
		}

		internal static List<string> DetectedPlatforms()
		{
			EnsureRoots();
			List<string> list = new List<string>();
			lock (sync)
			{
				Platform[] platforms = Platforms;
				foreach (Platform platform in platforms)
				{
					if (platform.Roots != null && platform.Roots.Count > 0)
					{
						list.Add(platform.Id);
					}
				}
				return list;
			}
		}

		private static void EnsureRoots()
		{
			lock (sync)
			{
				long ticks = DateTime.UtcNow.Ticks;
				if (!rootsResolved || lastResolveTicks <= 0 || ticks < lastResolveTicks || ticks - lastResolveTicks >= 6000000000L)
				{
					ResolveRootsLocked();
				}
			}
		}

		private static void ResolveRootsLocked()
		{
			Dictionary<string, List<string>> dictionary = ScanUninstallLocations();
			Platform[] platforms = Platforms;
			foreach (Platform platform in platforms)
			{
				List<string> list = new List<string>();
				string[] registryRoots = platform.RegistryRoots;
				foreach (string spec in registryRoots)
				{
					AddRegistryRoot(list, spec);
				}
				string[] folderRoots = platform.FolderRoots;
				foreach (string spec2 in folderRoots)
				{
					AddFolderRoot(list, spec2);
				}
				string[] uninstallTags = platform.UninstallTags;
				foreach (string key in uninstallTags)
				{
					List<string> value;
					if (!dictionary.TryGetValue(key, out value))
					{
						continue;
					}
					foreach (string item in value)
					{
						AddRoot(list, item);
					}
				}
				platform.Roots = list;
			}
			rootsResolved = true;
			lastResolveTicks = DateTime.UtcNow.Ticks;
		}

		private static void AddRegistryRoot(List<string> into, string spec)
		{
			try
			{
				string[] array = spec.Split('|');
				if (array.Length != 3)
				{
					return;
				}
				RegistryKey registryKey = ((array[0] == "U") ? Registry.CurrentUser : Registry.LocalMachine);
				using (RegistryKey registryKey2 = registryKey.OpenSubKey(array[1]))
				{
					if (registryKey2 != null)
					{
						AddRoot(into, registryKey2.GetValue(array[2]) as string);
					}
				}
			}
			catch
			{
			}
		}

		private static void AddFolderRoot(List<string> into, string spec)
		{
			try
			{
				int num = spec.IndexOf('|');
				if (num > 0)
				{
					string text = BaseFolder(spec.Substring(0, num));
					if (!string.IsNullOrEmpty(text))
					{
						AddRoot(into, Path.Combine(text, spec.Substring(num + 1)));
					}
				}
			}
			catch
			{
			}
		}

		private static string BaseFolder(string token)
		{
			try
			{
				switch (token)
				{
				case "pf":
					return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				case "pf86":
					return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
				case "common":
					return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
				case "common86":
					return Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
				case "data":
					return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
				case "local":
					return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				case "roaming":
					return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				case "drive":
					return SystemDrive();
				}
			}
			catch
			{
			}
			return null;
		}

		private static string SystemDrive()
		{
			string text = null;
			try
			{
				text = Environment.GetEnvironmentVariable("SystemDrive");
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(text))
			{
				try
				{
					text = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
				}
				catch
				{
				}
			}
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			return text.TrimEnd('\\') + "\\";
		}

		private static void AddRoot(List<string> into, string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return;
			}
			string text;
			try
			{
				text = Path.GetFullPath(raw.Trim().Trim('"').Replace('/', '\\')).TrimEnd('\\');
			}
			catch
			{
				return;
			}
			if (text.Length <= 3 || IsTooBroad(text))
			{
				return;
			}
			try
			{
				if (!Directory.Exists(text))
				{
					return;
				}
			}
			catch
			{
				return;
			}
			foreach (string item in into)
			{
				if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}
			into.Add(text);
		}

		private static bool IsTooBroad(string dir)
		{
			Environment.SpecialFolder[] broadFolders = BroadFolders;
			foreach (Environment.SpecialFolder folder in broadFolders)
			{
				string folderPath;
				try
				{
					folderPath = Environment.GetFolderPath(folder);
				}
				catch
				{
					continue;
				}
				if (!string.IsNullOrEmpty(folderPath) && string.Equals(dir, folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			string folderPath2;
			try
			{
				folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			}
			catch
			{
				return false;
			}
			if (string.IsNullOrEmpty(folderPath2))
			{
				return false;
			}
			folderPath2 = folderPath2.TrimEnd('\\');
			if (!string.Equals(dir, folderPath2, StringComparison.OrdinalIgnoreCase))
			{
				return dir.StartsWith(folderPath2 + "\\", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		private static Dictionary<string, List<string>> ScanUninstallLocations()
		{
			Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			List<string> list = new List<string>();
			Platform[] platforms = Platforms;
			foreach (Platform platform in platforms)
			{
				string[] uninstallTags = platform.UninstallTags;
				foreach (string item in uninstallTags)
				{
					if (!list.Contains(item))
					{
						list.Add(item);
					}
				}
			}
			if (list.Count == 0)
			{
				return dictionary;
			}
			ScanUninstallHive(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", list, dictionary);
			ScanUninstallHive(Registry.LocalMachine, "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", list, dictionary);
			ScanUninstallHive(Registry.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", list, dictionary);
			return dictionary;
		}

		private static void ScanUninstallHive(RegistryKey hive, string path, List<string> tags, Dictionary<string, List<string>> into)
		{
			try
			{
				using (RegistryKey registryKey = hive.OpenSubKey(path))
				{
					if (registryKey == null)
					{
						return;
					}
					string[] subKeyNames = registryKey.GetSubKeyNames();
					foreach (string name in subKeyNames)
					{
						try
						{
							using (RegistryKey registryKey2 = registryKey.OpenSubKey(name))
							{
								if (registryKey2 == null)
								{
									continue;
								}
								string text = registryKey2.GetValue("DisplayName") as string;
								if (string.IsNullOrEmpty(text))
								{
									continue;
								}
								string text2 = registryKey2.GetValue("InstallLocation") as string;
								if (string.IsNullOrEmpty(text2))
								{
									text2 = DirFromCommand(registryKey2.GetValue("UninstallString") as string);
								}
								if (string.IsNullOrEmpty(text2))
								{
									text2 = DirFromCommand(registryKey2.GetValue("DisplayIcon") as string);
								}
								if (string.IsNullOrEmpty(text2))
								{
									continue;
								}
								foreach (string tag in tags)
								{
									if (text.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
									{
										List<string> value;
										if (!into.TryGetValue(tag, out value))
										{
											value = (into[tag] = new List<string>());
										}
										value.Add(text2);
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
			catch
			{
			}
		}

		internal static string DirFromCommand(string command)
		{
			if (string.IsNullOrEmpty(command))
			{
				return null;
			}
			string text = command.Trim();
			string text2 = null;
			if (text.Length > 1 && text[0] == '"')
			{
				int num = text.IndexOf('"', 1);
				if (num > 1)
				{
					text2 = text.Substring(1, num - 1);
				}
			}
			else
			{
				int num2 = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
				if (num2 > 0)
				{
					text2 = text.Substring(0, num2 + 4);
				}
			}
			text2 = ((text2 == null) ? null : text2.Trim());
			if (string.IsNullOrEmpty(text2))
			{
				return null;
			}
			try
			{
				string directoryName = Path.GetDirectoryName(text2);
				return string.IsNullOrEmpty(directoryName) ? null : directoryName;
			}
			catch
			{
				return null;
			}
		}
	}
}
