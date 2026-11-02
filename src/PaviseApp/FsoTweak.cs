using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class FsoTweak
	{
		private const string LayersKey = "Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers";

		private const string Token = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

		private const string Marker = "~";

		private const string ListKey = "FsoExeList";

		private const char ListSep = '\u001f';

		private static readonly object sync = new object();

		internal static string[] TrackedExes()
		{
			lock (sync)
			{
				string value;
				if (!Settings.TryLoadStr("FsoExeList", out value))
				{
					throw new InvalidOperationException("Cannot read the FSO recovery ledger");
				}
				return value.Split(new char[1] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries);
			}
		}

		public static bool HasResidue()
		{
			try
			{
				return TrackedExes().Length > 0;
			}
			catch
			{
				return true;
			}
		}

		public static bool RestoreAll()
		{
			lock (sync)
			{
				try
				{
					bool flag = true;
					string[] array = TrackedExes();
					foreach (string exePath in array)
					{
						if (!SetForExe(exePath, false))
						{
							flag = false;
						}
					}
					return flag && TrackedExes().Length == 0;
				}
				catch
				{
					return false;
				}
			}
		}

		private static bool Track(string exePath, bool disabled)
		{
			List<string> list = new List<string>();
			string[] array = TrackedExes();
			foreach (string text in array)
			{
				if (!string.Equals(text, exePath, StringComparison.OrdinalIgnoreCase))
				{
					list.Add(text);
				}
			}
			if (disabled)
			{
				list.Add(exePath);
			}
			string text2 = string.Join('\u001f'.ToString(), list.ToArray());
			string value;
			if (Settings.SaveStr("FsoExeList", text2) && Settings.TryLoadStr("FsoExeList", out value))
			{
				return value == text2;
			}
			return false;
		}

		private static bool IsTracked(string exePath)
		{
			string[] array = TrackedExes();
			foreach (string a in array)
			{
				if (string.Equals(a, exePath, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static string ReadLayer(string exePath)
		{
			using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers"))
			{
				object obj = ((registryKey == null) ? null : registryKey.GetValue(exePath));
				if (obj != null && !(obj is string))
				{
					throw new InvalidOperationException("Unexpected FSO compatibility-layer value type");
				}
				return obj as string;
			}
		}

		private static void WriteLayer(string exePath, string value)
		{
			using (RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers"))
			{
				if (registryKey == null)
				{
					throw new InvalidOperationException("Cannot open FSO compatibility layers");
				}
				if (value.Length == 0)
				{
					registryKey.DeleteValue(exePath, false);
				}
				else
				{
					registryKey.SetValue(exePath, value, RegistryValueKind.String);
				}
			}
		}

		private static void RollbackToken(string exePath)
		{
			try
			{
				string text = ReadLayer(exePath);
				if (HasToken(text))
				{
					WriteLayer(exePath, RemoveToken(text));
				}
				if (!HasToken(ReadLayer(exePath)))
				{
					Track(exePath, false);
				}
			}
			catch
			{
			}
		}

		public static bool IsDisabledForExe(string exePath)
		{
			if (string.IsNullOrEmpty(exePath))
			{
				return false;
			}
			lock (sync)
			{
				try
				{
					return HasToken(ReadLayer(exePath));
				}
				catch (Exception ex)
				{
					Logger.Log(Lang.T("log.fso.3") + " " + ex.Message);
					return false;
				}
			}
		}

		public static bool SetForExe(string exePath, bool disableFso)
		{
			if (string.IsNullOrEmpty(exePath))
			{
				return false;
			}
			lock (sync)
			{
				bool flag = false;
				try
				{
					string text = ReadLayer(exePath);
					bool flag2 = IsTracked(exePath);
					if (HasToken(text) == disableFso)
					{
						return disableFso || !flag2 || Track(exePath, false);
					}
					if (disableFso && !flag2 && !Track(exePath, true))
					{
						return false;
					}
					flag = disableFso;
					WriteLayer(exePath, disableFso ? AddToken(text) : RemoveToken(text));
					if (HasToken(ReadLayer(exePath)) != disableFso)
					{
						throw new InvalidOperationException("FSO compatibility-layer verification failed");
					}
					if (disableFso && !IsTracked(exePath))
					{
						throw new InvalidOperationException("FSO recovery ledger changed during apply");
					}
					if (!disableFso && flag2 && !Track(exePath, false))
					{
						return false;
					}
					Logger.Log(Lang.T(disableFso ? "log.fso.1" : "log.fso.2") + " " + exePath);
					return true;
				}
				catch (Exception ex)
				{
					if (flag)
					{
						RollbackToken(exePath);
					}
					Logger.Log(Lang.T("log.fso.3") + " " + ex.Message);
					return false;
				}
			}
		}

		private static string[] Split(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return new string[0];
			}
			return s.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		}

		private static bool HasToken(string s)
		{
			string[] array = Split(s);
			foreach (string a in array)
			{
				if (string.Equals(a, "DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}

		private static List<string> OtherTokens(string cur)
		{
			List<string> list = new List<string>();
			string[] array = Split(cur);
			foreach (string text in array)
			{
				if (!(text == "~") && !string.Equals(text, "DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
				{
					list.Add(text);
				}
			}
			return list;
		}

		private static string AddToken(string cur)
		{
			List<string> list = new List<string>();
			list.Add("~");
			List<string> list2 = list;
			list2.AddRange(OtherTokens(cur));
			list2.Add("DISABLEDXMAXIMIZEDWINDOWEDMODE");
			return string.Join(" ", list2.ToArray());
		}

		private static string RemoveToken(string cur)
		{
			List<string> list = OtherTokens(cur);
			if (list.Count == 0)
			{
				return "";
			}
			List<string> list2 = new List<string>();
			list2.Add("~");
			List<string> list3 = list2;
			list3.AddRange(list);
			return string.Join(" ", list3.ToArray());
		}
	}
}
