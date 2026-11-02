using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class DpiTweak
	{
		private const string LayersKey = "Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers";

		private const string Token = "HIGHDPIAWARE";

		private const string Marker = "~";

		private const string ListKey = "DpiExeList";

		private const char ListSep = '\u001f';

		private static readonly object sync = new object();

		internal static string[] TrackedExes()
		{
			lock (sync)
			{
				string value;
				if (!Settings.TryLoadStr("DpiExeList", out value))
				{
					throw new InvalidOperationException("Cannot read the DPI recovery ledger");
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

		public static bool ScalingActive()
		{
			try
			{
				using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
				{
					return graphics.DpiX > 96.5f;
				}
			}
			catch
			{
				return false;
			}
		}

		private static bool Track(string exePath, bool aware)
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
			if (aware)
			{
				list.Add(exePath);
			}
			string text2 = string.Join('\u001f'.ToString(), list.ToArray());
			string value;
			if (Settings.SaveStr("DpiExeList", text2) && Settings.TryLoadStr("DpiExeList", out value))
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
					throw new InvalidOperationException("Unexpected DPI compatibility-layer value type");
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
					throw new InvalidOperationException("Cannot open DPI compatibility layers");
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

		public static bool IsAwareForExe(string exePath)
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
					Logger.Log(Lang.T("log.dpi.3") + " " + ex.Message);
					return false;
				}
			}
		}

		public static bool SetForExe(string exePath, bool aware)
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
					if (HasToken(text) == aware)
					{
						return aware || !flag2 || Track(exePath, false);
					}
					if (aware && !flag2 && !Track(exePath, true))
					{
						return false;
					}
					flag = aware;
					WriteLayer(exePath, aware ? AddToken(text) : RemoveToken(text));
					if (HasToken(ReadLayer(exePath)) != aware)
					{
						throw new InvalidOperationException("DPI compatibility-layer verification failed");
					}
					if (aware && !IsTracked(exePath))
					{
						throw new InvalidOperationException("DPI recovery ledger changed during apply");
					}
					if (!aware && flag2 && !Track(exePath, false))
					{
						return false;
					}
					Logger.Log(Lang.T(aware ? "log.dpi.1" : "log.dpi.2") + " " + exePath);
					return true;
				}
				catch (Exception ex)
				{
					if (flag)
					{
						RollbackToken(exePath);
					}
					Logger.Log(Lang.T("log.dpi.3") + " " + ex.Message);
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

		internal static bool HasToken(string s)
		{
			string[] array = Split(s);
			foreach (string a in array)
			{
				if (string.Equals(a, "HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase))
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
				if (!(text == "~") && !string.Equals(text, "HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase))
				{
					list.Add(text);
				}
			}
			return list;
		}

		internal static string AddToken(string cur)
		{
			List<string> list = new List<string>();
			list.Add("~");
			List<string> list2 = list;
			list2.AddRange(OtherTokens(cur));
			list2.Add("HIGHDPIAWARE");
			return string.Join(" ", list2.ToArray());
		}

		internal static string RemoveToken(string cur)
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
