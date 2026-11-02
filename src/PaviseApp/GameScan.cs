using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class GameScan
	{
		private sealed class ShortcutDirectory
		{
			internal string Path;

			internal int Depth;
		}

		private const int ShortcutDirectoryLimit = 128;

		private const int ShortcutFileLimit = 1024;

		private const int ShortcutRootLimit = 128;

		private const int ShortcutDepthLimit = 4;

		private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"windows", "programdata", "$recycle.bin", "system volume information", "recovery", "perflogs", "onedrivetemp", "node_modules", ".git", "temp",
			"tmp", "cache", "__pycache__", "easyanticheat", "easyanticheat_eos", "battleye", "tenprotect"
		};

		private static readonly HashSet<string> GenericDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games", "retail",
			"shipping", "engine", "content", "data", "app"
		};

		private static readonly HashSet<string> GenericDirTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "game", "games", "retail",
			"shipping", "engine", "content", "data", "app", "client"
		};

		private static bool IsSystemWideDirName(string name)
		{
			if (string.IsNullOrEmpty(name) || name.EndsWith(":", StringComparison.Ordinal))
			{
				return true;
			}
			if (!name.Equals("program files", StringComparison.OrdinalIgnoreCase) && !name.Equals("program files (x86)", StringComparison.OrdinalIgnoreCase) && !name.Equals("common files", StringComparison.OrdinalIgnoreCase) && !name.Equals("users", StringComparison.OrdinalIgnoreCase) && !name.Equals("desktop", StringComparison.OrdinalIgnoreCase) && !name.Equals("downloads", StringComparison.OrdinalIgnoreCase))
			{
				return name.Equals("documents", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		internal static bool IsGenericDirName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (GenericDirs.Contains(name))
			{
				return true;
			}
			string[] array = name.Split('_', '-', ' ', '.');
			if (array.Length < 2)
			{
				return false;
			}
			int num = 0;
			string[] array2 = array;
			foreach (string text in array2)
			{
				if (text.Length != 0)
				{
					if (!GenericDirTokens.Contains(text))
					{
						return false;
					}
					num++;
				}
			}
			return num >= 2;
		}

		public static List<ScanHit> RunManifests(Func<bool> canceled)
		{
			List<ScanHit> list = new List<ScanHit>();
			HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			CollectManifests(null, list, roots, canceled);
			return list;
		}

		private static void CollectManifests(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
		{
			try
			{
				FromSteam(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromEpic(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromGog(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromUbisoft(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromRiot(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromWeGameApps(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromBattleNet(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromXbox(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromMicrosoftStore(root, hits, roots);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromInstalled(root, hits, roots, canceled);
			}
			catch
			{
			}
			if (Stop(canceled))
			{
				return;
			}
			try
			{
				FromShortcuts(root, hits, roots, canceled);
			}
			catch
			{
			}
		}

		private static bool Stop(Func<bool> canceled)
		{
			if (canceled != null)
			{
				return canceled();
			}
			return false;
		}

		private static bool UnderRoot(string dir, string root)
		{
			if (string.IsNullOrEmpty(root))
			{
				return dir != null;
			}
			string value = root.TrimEnd('\\') + "\\";
			if (dir != null)
			{
				return (dir.TrimEnd('\\') + "\\").StartsWith(value, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		internal static void AddManifestHit(string root, List<ScanHit> hits, HashSet<string> roots, string name, string dir, string exePath)
		{
			dir = CleanDir(dir);
			if (dir == null || !UnderRoot(dir, root) || !Directory.Exists(dir) || roots.Contains(dir))
			{
				return;
			}
			if (string.IsNullOrEmpty(name))
			{
				name = Path.GetFileName(dir);
			}
			string executablePath = null;
			if (!string.IsNullOrWhiteSpace(exePath))
			{
				try
				{
					string text = Environment.ExpandEnvironmentVariables(exePath.Trim().Trim('"')).Replace('/', '\\');
					if (!Path.IsPathRooted(text))
					{
						text = Path.Combine(dir, text);
					}
					text = Path.GetFullPath(text);
					if (UnderRoot(text, dir) && string.Equals(Path.GetExtension(text), ".exe", StringComparison.OrdinalIgnoreCase) && !IsJunkName(text))
					{
						string error;
						GameExecutableResolver.TryResolve(text, out executablePath, out error);
					}
				}
				catch
				{
				}
			}
			List<ExecutableCandidateFacts> list = null;
			if (executablePath == null)
			{
				list = ExecutableCandidateProbe.ListCandidates(dir, 24, out executablePath);
			}
			bool flag = executablePath != null;
			if (executablePath != null)
			{
				AddUniqueManifestEntry(hits, name, dir, executablePath, false);
			}
			else
			{
				foreach (ExecutableCandidateFacts item in list)
				{
					if (ExecutableCandidateProbe.Rank(item) > 0)
					{
						flag = true;
						AddUniqueManifestEntry(hits, name, dir, item.Path, true);
					}
				}
			}
			if (flag)
			{
				roots.Add(dir);
			}
		}

		private static void AddUniqueManifestEntry(List<ScanHit> hits, string name, string dir, string exe, bool needsChoice)
		{
			foreach (ScanHit hit in hits)
			{
				if (string.Equals(hit.Exe, exe, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}
			hits.Add(new ScanHit
			{
				Name = name,
				Proc = Path.GetFileNameWithoutExtension(exe),
				Root = dir,
				Exe = exe,
				NeedsChoice = needsChoice
			});
		}

		private static void FromInstalled(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
		{
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			RegistryKey[] array = new RegistryKey[3]
			{
				Registry.LocalMachine,
				Registry.LocalMachine,
				Registry.CurrentUser
			};
			string[] array2 = new string[3] { "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall" };
			for (int i = 0; i < array.Length; i++)
			{
				if (Stop(canceled))
				{
					break;
				}
				try
				{
					ScanUninstallHive(array[i], array2[i], root, hits, roots, seen, canceled);
				}
				catch
				{
				}
			}
		}

		internal static void ScanUninstallHive(RegistryKey hive, string path, string root, List<ScanHit> hits, HashSet<string> roots, HashSet<string> seen, Func<bool> canceled)
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
					if (Stop(canceled))
					{
						break;
					}
					try
					{
						using (RegistryKey registryKey2 = registryKey.OpenSubKey(name))
						{
							if (registryKey2 != null && (!(registryKey2.GetValue("SystemComponent") is int) || (int)registryKey2.GetValue("SystemComponent") == 0) && registryKey2.GetValue("ParentKeyName") == null)
							{
								string[] candidates = InstalledDirectoryCandidates(registryKey2.GetValue("InstallLocation") as string, registryKey2.GetValue("DisplayIcon") as string, registryKey2.GetValue("UninstallString") as string, registryKey2.GetValue("InstallSource") as string);
								ScanInstalledRecord(registryKey2.GetValue("DisplayName") as string, candidates, root, hits, roots, seen, canceled);
							}
						}
					}
					catch
					{
					}
				}
			}
		}

		internal static string[] InstalledDirectoryCandidates(string installLocation, string displayIcon, string uninstallString, string installSource)
		{
			List<string> list = new List<string>();
			HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			AddInstalledDirectoryCandidate(installLocation, list, unique);
			AddInstalledCommandDirectories(displayIcon, list, unique);
			AddInstalledCommandDirectories(uninstallString, list, unique);
			AddInstalledDirectoryCandidate(installSource, list, unique);
			return list.ToArray();
		}

		private static void AddInstalledDirectoryCandidate(string value, List<string> candidates, HashSet<string> unique)
		{
			string text = CleanDir(value);
			if (text != null && unique.Add(text))
			{
				candidates.Add(text);
			}
		}

		private static void AddInstalledCommandDirectories(string command, List<string> candidates, HashSet<string> unique)
		{
			string text = InstalledCommandTarget(command);
			if (text != null)
			{
				AddInstalledDirectoryCandidate(InferGameRoot(text), candidates, unique);
				AddInstalledDirectoryCandidate(Path.GetDirectoryName(text), candidates, unique);
			}
		}

		internal static void ScanInstalledRecord(string name, string[] candidates, string root, List<ScanHit> hits, HashSet<string> roots, HashSet<string> seen, Func<bool> canceled)
		{
			if (candidates == null)
			{
				return;
			}
			foreach (string value in candidates)
			{
				if (Stop(canceled))
				{
					break;
				}
				try
				{
					string text = CleanDir(value);
					if (text == null || !UnderRoot(text, root) || !Directory.Exists(text) || IsSystemOrTooBroad(text) || IsSystemWideDirName(Path.GetFileName(text)))
					{
						continue;
					}
					foreach (ScanHit hit in hits)
					{
						if (!string.IsNullOrEmpty(hit.Root) && UnderRoot(text, hit.Root))
						{
							return;
						}
					}
					if (roots.Contains(text))
					{
						foreach (ScanHit hit2 in hits)
						{
							if (!string.IsNullOrEmpty(hit2.Exe) && UnderRoot(hit2.Exe, text))
							{
								return;
							}
						}
					}
					else if (seen.Add(text) && LooksLikeGameDir(text, 3))
					{
						AddManifestHit(root, hits, roots, name, text, null);
						if (roots.Contains(text))
						{
							break;
						}
					}
				}
				catch
				{
				}
			}
		}

		private static string CleanDir(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return null;
			}
			try
			{
				string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
				if (!Path.IsPathRooted(path))
				{
					return null;
				}
				path = Path.GetFullPath(path).TrimEnd('\\');
				return (path.Length >= 4) ? path : null;
			}
			catch
			{
				return null;
			}
		}

		private static string InstalledCommandTarget(string command)
		{
			if (string.IsNullOrEmpty(command))
			{
				return null;
			}
			try
			{
				string text = Environment.ExpandEnvironmentVariables(command.Trim());
				if (text.StartsWith("\""))
				{
					int num = text.IndexOf('"', 1);
					if (num <= 1)
					{
						return null;
					}
					text = text.Substring(1, num - 1);
				}
				else
				{
					int num2 = 0;
					while (num2 < text.Length)
					{
						int num3 = text.IndexOf(".exe", num2, StringComparison.OrdinalIgnoreCase);
						if (num3 < 0)
						{
							break;
						}
						int num4 = num3 + 4;
						if (num4 == text.Length || char.IsWhiteSpace(text[num4]) || text[num4] == ',')
						{
							text = text.Substring(0, num4);
							break;
						}
						num2 = num4;
					}
					int num5 = text.LastIndexOf(',');
					int result;
					if (num5 > 0 && int.TryParse(text.Substring(num5 + 1).Trim(), out result))
					{
						text = text.Substring(0, num5);
					}
				}
				return CleanDir(text);
			}
			catch
			{
				return null;
			}
		}

		private static bool IsSystemOrTooBroad(string dir)
		{
			string text = null;
			string b = null;
			string b2 = null;
			string b3 = null;
			string b4 = null;
			try
			{
				text = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
				b = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				b2 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
				b3 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
				b4 = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			}
			catch
			{
			}
			if (!string.IsNullOrEmpty(text) && (Same(dir, text) || UnderRoot(dir, text)))
			{
				return true;
			}
			if (Same(dir, b) || Same(dir, b2) || Same(dir, b3) || Same(dir, b4))
			{
				return true;
			}
			try
			{
				string directoryName = Path.GetDirectoryName(dir.TrimEnd('\\'));
				if (string.IsNullOrEmpty(directoryName))
				{
					return true;
				}
			}
			catch
			{
				return true;
			}
			return false;
		}

		private static bool Same(string a, string b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
			{
				return false;
			}
			return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
		}

		internal static bool LooksLikeGameDir(string dir, int depth)
		{
			string[] files;
			string[] directories;
			try
			{
				if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}
				files = Directory.GetFiles(dir);
				directories = Directory.GetDirectories(dir);
			}
			catch
			{
				return false;
			}
			if (HasGameSignals(files, directories))
			{
				return true;
			}
			if (depth <= 1)
			{
				return false;
			}
			int num = 0;
			string[] array = directories;
			foreach (string text in array)
			{
				if (++num > 24)
				{
					break;
				}
				string fileName = Path.GetFileName(text);
				if (fileName.Length != 0 && fileName[0] != '.' && !SkipDirs.Contains(fileName) && LooksLikeGameDir(text, depth - 1))
				{
					return true;
				}
			}
			return false;
		}

		private static bool HasGameSignals(string[] files, string[] subs)
		{
			bool flag = false;
			bool flag2 = false;
			bool flag3 = false;
			foreach (string path in files)
			{
				string text = Path.GetFileName(path).ToLowerInvariant();
				switch (text)
				{
				case "unityplayer.dll":
				case "gameassembly.dll":
					return true;
				case "steam_api.dll":
				case "steam_api64.dll":
				case "steam_appid.txt":
					return true;
				case "eossdk-win64-shipping.dll":
					return true;
				case "data.win":
					return true;
				case "data.pck":
					return true;
				}
				if (text.EndsWith(".rpa"))
				{
					return true;
				}
				if (text.EndsWith(".vpk"))
				{
					return true;
				}
				if (text.StartsWith("pakchunk") && text.EndsWith(".pak"))
				{
					return true;
				}
				if (text == "fna.dll" || text == "monogame.framework.dll")
				{
					return true;
				}
				if (text.EndsWith("-win64-shipping.exe") || text.EndsWith("-win32-shipping.exe"))
				{
					return true;
				}
				if (text.EndsWith(".dll") && (text.StartsWith("bink") || text.StartsWith("fmod") || text.StartsWith("crysystem")))
				{
					return true;
				}
				if (text == "mss32.dll" || text == "mss64.dll")
				{
					return true;
				}
				if (text.StartsWith("goggame-"))
				{
					return true;
				}
				switch (text)
				{
				case "steam_emu.ini":
				case "onlinefix.ini":
				case "cream_api.ini":
					return true;
				}
				if (text.StartsWith("tersafe"))
				{
					return true;
				}
				if (text == ".build.info")
				{
					return true;
				}
				if (text == "nw.dll")
				{
					flag2 = true;
				}
				switch (text)
				{
				case "icudtl.dat":
				case "chrome_100_percent.pak":
				case "v8_context_snapshot.bin":
				case "app.asar":
					flag = true;
					break;
				}
			}
			foreach (string path2 in subs)
			{
				switch (Path.GetFileName(path2).ToLowerInvariant())
				{
				case "easyanticheat":
				case "easyanticheat_eos":
				case "battleye":
				case "tenprotect":
					return true;
				case "renpy":
					return true;
				case "www":
					flag3 = true;
					break;
				}
			}
			if (flag2 && flag3)
			{
				return true;
			}
			if (flag)
			{
				return false;
			}
			foreach (string text2 in files)
			{
				string text3 = Path.GetFileName(text2).ToLowerInvariant();
				if (!text3.EndsWith(".exe") || IsJunkName(text3))
				{
					continue;
				}
				try
				{
					if (new FileInfo(text2).Length >= 209715200)
					{
						return true;
					}
				}
				catch
				{
				}
			}
			return false;
		}

		internal static string InferGameRoot(string executablePath)
		{
			if (string.IsNullOrWhiteSpace(executablePath))
			{
				return null;
			}
			string text;
			try
			{
				string fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
				text = (Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath));
			}
			catch
			{
				return null;
			}
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			for (int i = 0; i < 4; i++)
			{
				string fileName;
				try
				{
					fileName = Path.GetFileName(text.TrimEnd('\\'));
				}
				catch
				{
					break;
				}
				if (string.IsNullOrEmpty(fileName) || !IsGenericDirName(fileName))
				{
					break;
				}
				string directoryName;
				try
				{
					directoryName = Path.GetDirectoryName(text.TrimEnd('\\'));
				}
				catch
				{
					break;
				}
				if (string.IsNullOrEmpty(directoryName))
				{
					break;
				}
				text = directoryName;
			}
			return text;
		}

		private static bool IsJunkName(string name)
		{
			return GameSessionDetector.ElectionVetoed(Path.GetFileNameWithoutExtension(name ?? ""), null);
		}

		internal static string PickMainExe(string dir)
		{
			return ExecutableCandidateProbe.PickMainExecutable(dir);
		}

		private static void FromSteam(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string text = null;
			try
			{
				text = Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null) as string;
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(text))
			{
				try
				{
					text = Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath", null) as string;
				}
				catch
				{
				}
			}
			if (!string.IsNullOrEmpty(text))
			{
				FromSteamLibraries(text.Replace('/', '\\'), root, hits, roots);
			}
		}

		internal static void FromSteamLibraries(string steam, string root, List<ScanHit> hits, HashSet<string> roots)
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			hashSet.Add(steam);
			string path = Path.Combine(steam, "steamapps\\libraryfolders.vdf");
			try
			{
				if (File.Exists(path))
				{
					foreach (Match item in Regex.Matches(File.ReadAllText(path), "\"(?:path|[0-9]+)\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
					{
						try
						{
							string text = item.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
							if (Path.IsPathRooted(text))
							{
								hashSet.Add(text);
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
			foreach (string item2 in hashSet)
			{
				string text2;
				string[] files;
				try
				{
					text2 = Path.Combine(item2, "steamapps");
					files = Directory.GetFiles(text2, "appmanifest_*.acf");
				}
				catch
				{
					continue;
				}
				string[] array = files;
				foreach (string path2 in array)
				{
					try
					{
						string input = File.ReadAllText(path2);
						Match match2 = Regex.Match(input, "\"name\"\\s+\"([^\"]+)\"");
						Match match3 = Regex.Match(input, "\"installdir\"\\s+\"([^\"]+)\"");
						if (match3.Success)
						{
							string name = (match2.Success ? match2.Groups[1].Value : null);
							AddManifestHit(root, hits, roots, name, Path.Combine(text2, "common\\" + match3.Groups[1].Value), null);
						}
					}
					catch
					{
					}
				}
			}
		}

		internal static string JsonStr(string json, string key)
		{
			if (string.IsNullOrEmpty(json) || key == null)
			{
				return null;
			}
			for (int i = 0; i < json.Length; i++)
			{
				if (json[i] != '"')
				{
					continue;
				}
				string value;
				if (!ReadJsonString(json, ref i, out value))
				{
					return null;
				}
				if (!string.Equals(value, key, StringComparison.Ordinal))
				{
					i--;
					continue;
				}
				int j;
				for (j = i; j < json.Length && char.IsWhiteSpace(json[j]); j++)
				{
				}
				if (j >= json.Length || json[j] != ':')
				{
					i--;
					continue;
				}
				for (j++; j < json.Length && char.IsWhiteSpace(json[j]); j++)
				{
				}
				string value2;
				if (!ReadJsonString(json, ref j, out value2))
				{
					return null;
				}
				return value2;
			}
			return null;
		}

		private static bool ReadJsonString(string json, ref int offset, out string value)
		{
			value = null;
			if (offset >= json.Length || json[offset] != '"')
			{
				return false;
			}
			offset++;
			StringBuilder stringBuilder = new StringBuilder();
			while (offset < json.Length)
			{
				char c = json[offset++];
				if (c == '"')
				{
					value = stringBuilder.ToString();
					return true;
				}
				if (c < ' ')
				{
					return false;
				}
				if (c != '\\')
				{
					stringBuilder.Append(c);
					continue;
				}
				if (offset >= json.Length)
				{
					return false;
				}
				switch (json[offset++])
				{
				case '"':
					stringBuilder.Append('"');
					break;
				case '\\':
					stringBuilder.Append('\\');
					break;
				case '/':
					stringBuilder.Append('/');
					break;
				case 'b':
					stringBuilder.Append('\b');
					break;
				case 'f':
					stringBuilder.Append('\f');
					break;
				case 'n':
					stringBuilder.Append('\n');
					break;
				case 'r':
					stringBuilder.Append('\r');
					break;
				case 't':
					stringBuilder.Append('\t');
					break;
				case 'u':
				{
					if (json.Length - offset < 4)
					{
						return false;
					}
					int num = 0;
					for (int i = 0; i < 4; i++)
					{
						char c2 = json[offset++];
						int num2 = ((c2 >= '0' && c2 <= '9') ? (c2 - 48) : ((c2 >= 'a' && c2 <= 'f') ? (c2 - 97 + 10) : ((c2 >= 'A' && c2 <= 'F') ? (c2 - 65 + 10) : (-1))));
						if (num2 < 0)
						{
							return false;
						}
						num = num * 16 + num2;
					}
					stringBuilder.Append((char)num);
					break;
				}
				default:
					return false;
				}
			}
			return false;
		}

		private static void FromEpic(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string mdir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic\\EpicGamesLauncher\\Data\\Manifests");
			FromEpicManifests(mdir, root, hits, roots);
		}

		internal static void FromEpicManifests(string mdir, string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string[] files;
			try
			{
				files = Directory.GetFiles(mdir, "*.item");
			}
			catch
			{
				return;
			}
			string[] array = files;
			foreach (string path in array)
			{
				try
				{
					string json = File.ReadAllText(path);
					string text = JsonStr(json, "InstallLocation");
					if (text != null)
					{
						string text2 = JsonStr(json, "LaunchExecutable");
						string exePath = ((text2 != null && text2.Length > 0) ? Path.Combine(text, text2.Replace('/', '\\')) : null);
						AddManifestHit(root, hits, roots, JsonStr(json, "DisplayName"), text, exePath);
					}
				}
				catch
				{
				}
			}
		}

		private static void FromGog(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string[] array = new string[2] { "SOFTWARE\\WOW6432Node\\GOG.com\\Games", "SOFTWARE\\GOG.com\\Games" };
			string[] array2 = array;
			foreach (string name in array2)
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(name))
				{
					if (registryKey == null)
					{
						continue;
					}
					string[] subKeyNames = registryKey.GetSubKeyNames();
					foreach (string name2 in subKeyNames)
					{
						try
						{
							using (RegistryKey registryKey2 = registryKey.OpenSubKey(name2))
							{
								if (registryKey2 != null)
								{
									string dir = registryKey2.GetValue("path") as string;
									string exePath = registryKey2.GetValue("exe") as string;
									AddManifestHit(root, hits, roots, registryKey2.GetValue("gameName") as string, dir, exePath);
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

		private static void FromUbisoft(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Ubisoft\\Launcher\\Installs"))
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
							if (registryKey2 != null)
							{
								AddManifestHit(root, hits, roots, null, registryKey2.GetValue("InstallDir") as string, null);
							}
						}
					}
					catch
					{
					}
				}
			}
		}

		private static string[] FixedDriveRoots()
		{
			List<string> list = new List<string>();
			try
			{
				DriveInfo[] drives = DriveInfo.GetDrives();
				foreach (DriveInfo driveInfo in drives)
				{
					try
					{
						if (driveInfo.IsReady)
						{
							list.Add(driveInfo.RootDirectory.FullName);
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
			return list.ToArray();
		}

		private static void FromXbox(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string[] array = FixedDriveRoots();
			foreach (string path in array)
			{
				string[] directories;
				try
				{
					directories = Directory.GetDirectories(Path.Combine(path, "XboxGames"));
				}
				catch
				{
					continue;
				}
				string[] array2 = directories;
				foreach (string text in array2)
				{
					try
					{
						string name = Path.GetFileName(text.TrimEnd('\\'));
						string text2 = Path.Combine(text, "Content");
						string text3 = (Directory.Exists(text2) ? text2 : text);
						string exePath = null;
						string path2 = Path.Combine(text3, "MicrosoftGame.config");
						if (File.Exists(path2))
						{
							string input = File.ReadAllText(path2);
							Match match = Regex.Match(input, "<Executable[^>]*\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
							if (match.Success)
							{
								string text4 = Path.Combine(text3, match.Groups[1].Value.Replace('/', '\\'));
								if (File.Exists(text4))
								{
									exePath = text4;
								}
							}
							Match match2 = Regex.Match(input, "DefaultDisplayName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
							if (match2.Success && match2.Groups[1].Value.Trim().Length > 0 && !match2.Groups[1].Value.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
							{
								name = match2.Groups[1].Value.Trim();
							}
						}
						AddManifestHit(root, hits, roots, name, text3, exePath);
					}
					catch
					{
					}
				}
			}
		}

		private static void FromMicrosoftStore(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			FromPackageRepository(root, "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages", hits, roots);
		}

		internal static void FromPackageRepository(string root, string repoKey, List<ScanHit> hits, HashSet<string> roots)
		{
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(repoKey))
			{
				if (registryKey == null)
				{
					return;
				}
				string[] subKeyNames = registryKey.GetSubKeyNames();
				foreach (string text in subKeyNames)
				{
					try
					{
						using (RegistryKey registryKey2 = registryKey.OpenSubKey(text))
						{
							if (registryKey2 == null)
							{
								continue;
							}
							string text2 = registryKey2.GetValue("PackageRootFolder") as string;
							if (string.IsNullOrEmpty(text2))
							{
								continue;
							}
							text2 = text2.Trim().TrimEnd('\\');
							if (text2.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0 || (!File.Exists(Path.Combine(text2, "MicrosoftGame.config")) && !File.Exists(Path.Combine(text2, "xboxservices.config"))))
							{
								continue;
							}
							string text3 = null;
							string path = Path.Combine(text2, "MicrosoftGame.config");
							if (File.Exists(path))
							{
								try
								{
									Match match = Regex.Match(File.ReadAllText(path), "<Executable[^>]*\\bName\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
									if (match.Success)
									{
										string text4 = Path.Combine(text2, match.Groups[1].Value.Replace('/', '\\'));
										if (File.Exists(text4))
										{
											text3 = text4;
										}
									}
								}
								catch
								{
								}
							}
							if (text3 == null)
							{
								string path2 = Path.Combine(text2, "AppxManifest.xml");
								if (File.Exists(path2))
								{
									try
									{
										Match match2 = Regex.Match(File.ReadAllText(path2), "<Application\\b[^>]*\\bExecutable\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
										if (match2.Success)
										{
											string text5 = Path.Combine(text2, match2.Groups[1].Value.Replace('/', '\\'));
											if (File.Exists(text5))
											{
												text3 = text5;
											}
										}
									}
									catch
									{
									}
								}
							}
							string text6 = registryKey2.GetValue("DisplayName") as string;
							if (string.IsNullOrEmpty(text6) || text6.StartsWith("@"))
							{
								text6 = PackageBaseName(text);
							}
							AddManifestHit(root, hits, roots, text6, text2, text3);
						}
					}
					catch
					{
					}
				}
			}
		}

		private static string PackageBaseName(string packageFullName)
		{
			string text = packageFullName ?? "";
			int num = text.IndexOf('_');
			if (num > 0)
			{
				text = text.Substring(0, num);
			}
			int num2 = text.IndexOf('.');
			if (num2 >= 0 && num2 < text.Length - 1)
			{
				text = text.Substring(num2 + 1);
			}
			return text;
		}

		private static void FromRiot(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games\\Metadata");
			string[] directories;
			try
			{
				directories = Directory.GetDirectories(path);
			}
			catch
			{
				return;
			}
			string[] array = directories;
			foreach (string path2 in array)
			{
				try
				{
					string[] files = Directory.GetFiles(path2, "*.yaml");
					foreach (string path3 in files)
					{
						Match match = Regex.Match(File.ReadAllText(path3), "product_install_full_path:\\s*\"?([^\"\\r\\n]+?)\"?\\s*$", RegexOptions.Multiline);
						if (match.Success)
						{
							string text = match.Groups[1].Value.Trim().Replace('/', '\\');
							AddManifestHit(root, hits, roots, Path.GetFileName(text.TrimEnd('\\')), text, null);
						}
					}
				}
				catch
				{
				}
			}
		}

		private static void FromWeGameApps(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string[] array = FixedDriveRoots();
			foreach (string path in array)
			{
				string[] array2 = new string[3]
				{
					Path.Combine(path, "WeGameApps"),
					Path.Combine(path, "Program Files\\WeGameApps"),
					Path.Combine(path, "Program Files (x86)\\WeGameApps")
				};
				string[] array3 = array2;
				foreach (string path2 in array3)
				{
					string[] directories;
					try
					{
						directories = Directory.GetDirectories(path2);
					}
					catch
					{
						continue;
					}
					string[] array4 = directories;
					foreach (string text in array4)
					{
						try
						{
							string fileName = Path.GetFileName(text.TrimEnd('\\'));
							if (fileName.Length != 0 && fileName[0] != '.')
							{
								AddManifestHit(root, hits, roots, fileName, text, null);
							}
						}
						catch
						{
						}
					}
				}
			}
		}

		private static void FromBattleNet(string root, List<ScanHit> hits, HashSet<string> roots)
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net\\Agent\\product.db");
			if (!File.Exists(path))
			{
				return;
			}
			string @string;
			try
			{
				@string = Encoding.UTF8.GetString(File.ReadAllBytes(path));
			}
			catch
			{
				return;
			}
			foreach (Match item in Regex.Matches(@string, "[A-Za-z]:[/\\\\][^\\x00-\\x1f\"|?*<>]{2,200}"))
			{
				try
				{
					string text = item.Value.Replace('/', '\\').TrimEnd('\\');
					if (Directory.Exists(text) && File.Exists(Path.Combine(text, ".build.info")))
					{
						AddManifestHit(root, hits, roots, Path.GetFileName(text), text, null);
					}
				}
				catch
				{
				}
			}
		}

		private static void FromShortcuts(string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
		{
			List<string> list = new List<string>();
			Environment.SpecialFolder[] array = new Environment.SpecialFolder[4]
			{
				Environment.SpecialFolder.DesktopDirectory,
				Environment.SpecialFolder.CommonDesktopDirectory,
				Environment.SpecialFolder.StartMenu,
				Environment.SpecialFolder.CommonStartMenu
			};
			foreach (Environment.SpecialFolder folder in array)
			{
				try
				{
					string folderPath = Environment.GetFolderPath(folder);
					if (!string.IsNullOrEmpty(folderPath))
					{
						list.Add(folderPath);
					}
				}
				catch
				{
				}
			}
			ScanShortcutDirectories(list, root, hits, roots, canceled);
		}

		internal static void ScanShortcutDirectories(IEnumerable<string> locations, string root, List<ScanHit> hits, HashSet<string> roots, Func<bool> canceled)
		{
			if (locations == null || Stop(canceled))
			{
				return;
			}
			Queue<ShortcutDirectory> queue = new Queue<ShortcutDirectory>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string location in locations)
			{
				if (!Stop(canceled) && hashSet.Count < 128)
				{
					string text = CleanDir(location);
					if (text != null && hashSet.Add(text))
					{
						queue.Enqueue(new ShortcutDirectory
						{
							Path = text
						});
					}
					continue;
				}
				break;
			}
			int num = 0;
			while (queue.Count > 0 && !Stop(canceled))
			{
				ShortcutDirectory shortcutDirectory = queue.Dequeue();
				try
				{
					if ((File.GetAttributes(shortcutDirectory.Path) & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
				}
				catch
				{
					continue;
				}
				try
				{
					foreach (string item in Directory.EnumerateFiles(shortcutDirectory.Path, "*.lnk"))
					{
						if (Stop(canceled) || num++ >= 1024)
						{
							return;
						}
						try
						{
							if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
							{
								continue;
							}
							string text2 = GameExecutableResolver.ResolveShortcut(item);
							if (string.IsNullOrWhiteSpace(text2))
							{
								continue;
							}
							text2 = Environment.ExpandEnvironmentVariables(text2.Trim().Trim('"')).Replace('/', '\\');
							if (!Path.IsPathRooted(text2) || text2.StartsWith("\\\\", StringComparison.Ordinal) || !string.Equals(Path.GetExtension(text2), ".exe", StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}
							text2 = Path.GetFullPath(text2);
							string executablePath;
							string error;
							if (!IsLocalShortcutTarget(text2) || !GameExecutableResolver.TryResolve(text2, out executablePath, out error))
							{
								continue;
							}
							string text3 = CleanDir(InferGameRoot(executablePath));
							if (text3 == null || !UnderRoot(text3, root) || IsSystemOrTooBroad(text3) || IsSystemWideDirName(Path.GetFileName(text3)))
							{
								continue;
							}
							bool flag = false;
							foreach (string root2 in roots)
							{
								if (UnderRoot(text3, root2))
								{
									flag = true;
									break;
								}
							}
							if (flag || hashSet2.Contains(text3))
							{
								continue;
							}
							if (hashSet2.Count >= 128)
							{
								return;
							}
							hashSet2.Add(text3);
							if (LooksLikeGameDir(text3, 3) && !Stop(canceled))
							{
								int count = hits.Count;
								AddManifestHit(root, hits, roots, Path.GetFileName(text3), text3, null);
								if (hits.Count == count + 1 && !hits[count].NeedsChoice && string.Equals(hits[count].Exe, executablePath, StringComparison.OrdinalIgnoreCase))
								{
									hits[count].Name = Path.GetFileNameWithoutExtension(item);
								}
							}
						}
						catch
						{
						}
					}
				}
				catch
				{
				}
				if (shortcutDirectory.Depth >= 4)
				{
					continue;
				}
				try
				{
					foreach (string item2 in Directory.EnumerateDirectories(shortcutDirectory.Path))
					{
						if (!Stop(canceled) && hashSet.Count < 128)
						{
							string fileName = Path.GetFileName(item2);
							if (fileName.Length != 0 && fileName[0] != '.' && !SkipDirs.Contains(fileName) && hashSet.Add(item2))
							{
								queue.Enqueue(new ShortcutDirectory
								{
									Path = item2,
									Depth = shortcutDirectory.Depth + 1
								});
							}
							continue;
						}
						break;
					}
				}
				catch
				{
				}
			}
		}

		private static bool IsLocalShortcutTarget(string target)
		{
			try
			{
				string pathRoot = Path.GetPathRoot(target);
				if (new DriveInfo(pathRoot).DriveType == DriveType.Network)
				{
					return false;
				}
				string text = Path.GetDirectoryName(target).Substring(pathRoot.Length);
				string text2 = pathRoot;
				string[] array = text.Split(new char[1] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (string path in array)
				{
					text2 = Path.Combine(text2, path);
					if ((File.GetAttributes(text2) & FileAttributes.ReparsePoint) != 0)
					{
						return false;
					}
				}
				return (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0;
			}
			catch
			{
				return false;
			}
		}
	}
}
