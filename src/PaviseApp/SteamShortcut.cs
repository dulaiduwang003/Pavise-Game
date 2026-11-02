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
			string text = FindSteamRoot();
			if (text == null)
			{
				error = Lang.T("t.steamshortcut.3");
				return false;
			}
			string text2 = null;
			foreach (string item in LibraryRoots(text))
			{
				string path = Path.Combine(item, "steamapps\\appmanifest_" + appId + ".acf");
				try
				{
					if (!File.Exists(path))
					{
						continue;
					}
					string text3 = ParseVdfValue(File.ReadAllText(path), "installdir");
					if (!string.IsNullOrEmpty(text3))
					{
						string text4 = Path.Combine(item, "steamapps\\common\\" + text3);
						if (Directory.Exists(text4))
						{
							text2 = text4;
							break;
						}
					}
				}
				catch
				{
				}
			}
			if (text2 == null)
			{
				error = Lang.T("t.steamshortcut.4") + appId + Lang.T("t.steamshortcut.5");
				return false;
			}
			string text5 = PickMainExecutable(text2, Path.GetFileName(text2));
			if (text5 == null)
			{
				error = Lang.T("t.steamshortcut.6") + text2;
				return false;
			}
			executablePath = text5;
			suggestedName = Path.GetFileName(text2);
			return true;
		}

		internal static bool TryParseUrlFile(string content, out long appId)
		{
			appId = 0L;
			if (string.IsNullOrEmpty(content))
			{
				return false;
			}
			string[] array = content.Split('\n');
			foreach (string text in array)
			{
				string text2 = text.Trim();
				if (text2.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
				{
					string text3 = text2.Substring(4).Trim();
					if (!text3.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					string text4 = text3.Substring("steam://rungameid/".Length);
					int j;
					for (j = 0; j < text4.Length && char.IsDigit(text4[j]); j++)
					{
					}
					if (j == 0)
					{
						return false;
					}
					return long.TryParse(text4.Substring(0, j), out appId) && appId > 0;
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
					string text = ((registryKey == null) ? null : (registryKey.GetValue("SteamPath") as string));
					if (!string.IsNullOrEmpty(text))
					{
						text = text.Replace('/', '\\');
						if (Directory.Exists(text))
						{
							return text;
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
					string text2 = ((registryKey2 == null) ? null : (registryKey2.GetValue("InstallPath") as string));
					if (!string.IsNullOrEmpty(text2) && Directory.Exists(text2))
					{
						return text2;
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
			string[] array = new string[2]
			{
				Path.Combine(steamRoot, "steamapps\\libraryfolders.vdf"),
				Path.Combine(steamRoot, "config\\libraryfolders.vdf")
			};
			foreach (string path in array)
			{
				try
				{
					if (!File.Exists(path))
					{
						continue;
					}
					foreach (string item in ParseLibraryPaths(File.ReadAllText(path)))
					{
						bool flag = false;
						foreach (string item2 in list2)
						{
							if (string.Equals(item2, item, StringComparison.OrdinalIgnoreCase))
							{
								flag = true;
								break;
							}
						}
						if (!flag && Directory.Exists(item))
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
				string text = item.Groups[1].Value.Replace("\\\\", "\\");
				if (text.Length > 0)
				{
					list.Add(text);
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
