using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class SteamShortcut
	{
		private const string RunGamePrefix = "steam://rungameid/";

		public static bool TryResolve(string urlPath, out string executablePath, out string error)
		{
			string suggestedName;
			return TryResolve(urlPath, out executablePath, out error, out suggestedName);
		}

		public static bool TryResolve(string urlPath, out string executablePath, out string error, out string suggestedName)
		{
			executablePath = null;
			error = null;
			suggestedName = null;
			string content;
			try
			{
				content = File.ReadAllText(urlPath);
			}
			catch
			{
				error = Lang.T("t.steamshortcut.1");
				return false;
			}
			long appId;
			if (!TryParseUrlFile(content, out appId))
			{
				error = Lang.T("t.steamshortcut.2");
				return false;
			}
			string steamRoot = FindSteamRoot();
			if (steamRoot == null)
			{
				error = Lang.T("t.steamshortcut.3");
				return false;
			}
			string message = null;
			foreach (string item in LibraryRoots(steamRoot))
			{
				string path = Path.Combine(item, "steamapps\\appmanifest_" + appId + ".acf");
				try
				{
					if (!File.Exists(path))
					{
						continue;
					}
					string vdfValue = ParseVdfValue(File.ReadAllText(path), "installdir");
					if (!string.IsNullOrEmpty(vdfValue))
					{
						string combinedPath = Path.Combine(item, "steamapps\\common\\" + vdfValue);
						if (Directory.Exists(combinedPath))
						{
							message = combinedPath;
							break;
						}
					}
				}
				catch
				{
				}
			}
			if (message == null)
			{
				error = Lang.T("t.steamshortcut.4") + appId + Lang.T("t.steamshortcut.5");
				return false;
			}
			string pickMainExecutableResult = PickMainExecutable(message, Path.GetFileName(message));
			if (pickMainExecutableResult == null)
			{
				error = Lang.T("t.steamshortcut.6") + message;
				return false;
			}
			executablePath = pickMainExecutableResult;
			suggestedName = Path.GetFileName(message);
			return true;
		}

		internal static bool TryParseUrlFile(string content, out long appId)
		{
			appId = 0L;
			if (string.IsNullOrEmpty(content))
			{
				return false;
			}
			string[] contentParts = content.Split('\n');
			foreach (string entryText in contentParts)
			{
				string entryTextTrimmed = entryText.Trim();
				if (entryTextTrimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
				{
					string substringTrimmed = entryTextTrimmed.Substring(4).Trim();
					if (!substringTrimmed.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					string substringResult = substringTrimmed.Substring("steam://rungameid/".Length);
					int j;
					for (j = 0; j < substringResult.Length && char.IsDigit(substringResult[j]); j++)
					{
					}
					if (j == 0)
					{
						return false;
					}
					return long.TryParse(substringResult.Substring(0, j), out appId) && appId > 0;
				}
			}
			return false;
		}

		private static string FindSteamRoot()
		{
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
				{
					string getValueOffset = ((registryKey == null) ? null : (registryKey.GetValue("SteamPath") as string));
					if (!string.IsNullOrEmpty(getValueOffset))
					{
						getValueOffset = getValueOffset.Replace('/', '\\');
						if (Directory.Exists(getValueOffset))
						{
							return getValueOffset;
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				using (RegistryKey registryKey2 = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Valve\\Steam"))
				{
					string getValueOffset2 = ((registryKey2 == null) ? null : (registryKey2.GetValue("InstallPath") as string));
					if (!string.IsNullOrEmpty(getValueOffset2) && Directory.Exists(getValueOffset2))
					{
						return getValueOffset2;
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static IEnumerable<string> LibraryRoots(string steamRoot)
		{
			List<string> list = new List<string>();
			list.Add(steamRoot);
			List<string> list2 = list;
			string[] textEntries = new string[2]
			{
				Path.Combine(steamRoot, "steamapps\\libraryfolders.vdf"),
				Path.Combine(steamRoot, "config\\libraryfolders.vdf")
			};
			foreach (string path in textEntries)
			{
				try
				{
					if (!File.Exists(path))
					{
						continue;
					}
					foreach (string item in ParseLibraryPaths(File.ReadAllText(path)))
					{
						bool libraryRootsReady = false;
						foreach (string item2 in list2)
						{
							if (string.Equals(item2, item, StringComparison.OrdinalIgnoreCase))
							{
								libraryRootsReady = true;
								break;
							}
						}
						if (!libraryRootsReady && Directory.Exists(item))
						{
							list2.Add(item);
						}
					}
				}
				catch
				{
				}
			}
			return list2;
		}

		internal static List<string> ParseLibraryPaths(string vdfContent)
		{
			List<string> list = new List<string>();
			if (string.IsNullOrEmpty(vdfContent))
			{
				return list;
			}
			foreach (Match item in Regex.Matches(vdfContent, "\"path\"\\s+\"((?:\\\\.|[^\"\\\\])*)\""))
			{
				string replaceResult = item.Groups[1].Value.Replace("\\\\", "\\");
				if (replaceResult.Length > 0)
				{
					list.Add(replaceResult);
				}
			}
			return list;
		}

		internal static string ParseVdfValue(string content, string key)
		{
			if (string.IsNullOrEmpty(content))
			{
				return null;
			}
			Match match = Regex.Match(content, "\"" + Regex.Escape(key) + "\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase);
			if (!match.Success)
			{
				return null;
			}
			return match.Groups[1].Value.Replace("\\\\", "\\");
		}

		internal static string PickMainExecutable(string root, string installDirName)
		{
			return ExecutableCandidateProbe.PickMainExecutable(root);
		}
	}
}
