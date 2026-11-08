using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class IfeoStore
	{
		private const string Root = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";

		internal static readonly RegistryKey Hive = Registry.LocalMachine;

		internal static readonly string RootOverride = null;

		public static string RootPath
		{
			get
			{
				return RootOverride ?? "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options";
			}
		}

		public static string[] ParseList(string raw)
		{
			return (raw ?? "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		}

		public static void RemoveFromList(string listKey, string exe)
		{
			List<string> list = new List<string>();
			string[] list2 = ParseList(Settings.LoadStr(listKey, ""));
			foreach (string entryText in list2)
			{
				if (!string.Equals(entryText, exe, StringComparison.OrdinalIgnoreCase))
				{
					list.Add(entryText);
				}
			}
			Settings.SaveStr(listKey, string.Join(";", list.ToArray()));
		}

		public static void CleanupEmpty(string exe, string subKey, bool ourSub, bool ourExe)
		{
			try
			{
				using (RegistryKey registryKey = Hive.OpenSubKey(RootPath, true))
				{
					if (registryKey == null)
					{
						return;
					}
					if (subKey != null && ourSub)
					{
						using (RegistryKey registryKey2 = registryKey.OpenSubKey(exe, true))
						{
							if (registryKey2 != null)
							{
								using (RegistryKey registryKey3 = registryKey2.OpenSubKey(subKey))
								{
									if (registryKey3 != null && registryKey3.ValueCount == 0 && registryKey3.SubKeyCount == 0)
									{
										registryKey2.DeleteSubKey(subKey, false);
									}
								}
							}
						}
					}
					if (!ourExe)
					{
						return;
					}
					using (RegistryKey registryKey4 = registryKey.OpenSubKey(exe))
					{
						if (registryKey4 == null || registryKey4.ValueCount != 0 || registryKey4.SubKeyCount != 0)
						{
							return;
						}
					}
					registryKey.DeleteSubKey(exe, false);
				}
			}
			catch
			{
			}
		}
	}
}
