using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class ProtectedGameRoster
	{
		private const string Key = "AcProtectedGames";

		private static readonly object lk = new object();

		private static Dictionary<string, string> cache;

		private static string Norm(string rendererName)
		{
			if (string.IsNullOrEmpty(rendererName))
			{
				return null;
			}
			string text = rendererName;
			int num = text.LastIndexOfAny(new char[2] { '\\', '/' });
			if (num >= 0)
			{
				text = text.Substring(num + 1);
			}
			if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				text += ".exe";
			}
			text = text.Trim();
			if (text.Length != 0 && text.IndexOf(';') < 0 && text.IndexOf('|') < 0)
			{
				return text.ToLowerInvariant();
			}
			return null;
		}

		private static Dictionary<string, string> Load()
		{
			if (cache != null)
			{
				return cache;
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			string[] array = Settings.LoadStr("AcProtectedGames", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string text in array)
			{
				int num = text.LastIndexOf('|');
				if (num > 0 && num < text.Length - 1)
				{
					dictionary[text.Substring(0, num)] = text.Substring(num + 1);
				}
			}
			cache = dictionary;
			return cache;
		}

		private static void Persist()
		{
			List<string> list = new List<string>();
			foreach (KeyValuePair<string, string> item in cache)
			{
				list.Add(item.Key + "|" + item.Value);
			}
			Settings.SaveStr("AcProtectedGames", string.Join(";", list.ToArray()));
		}

		private static bool Current(string version)
		{
			// Write-denial observations are version scoped, not a data migration stamp.
			return string.Equals(version, App.Version, StringComparison.OrdinalIgnoreCase);
		}

		public static bool Contains(string rendererName)
		{
			string text = Norm(rendererName);
			if (text == null)
			{
				return false;
			}
			lock (lk)
			{
				string value;
				return Load().TryGetValue(text, out value) && Current(value);
			}
		}

		public static void Remember(string rendererName)
		{
			string text = Norm(rendererName);
			if (text == null)
			{
				return;
			}
			lock (lk)
			{
				Dictionary<string, string> dictionary = Load();
				string value;
				if (dictionary.TryGetValue(text, out value) && Current(value))
				{
					return;
				}
				dictionary[text] = App.Version;
				Persist();
			}
			Logger.Log(Lang.T("log.protectedgameroster.1") + text + Lang.T("log.protectedgameroster.2"));
		}

		public static bool Forget(string rendererName)
		{
			string text = Norm(rendererName);
			if (text == null)
			{
				return false;
			}
			lock (lk)
			{
				if (!Load().Remove(text))
				{
					return false;
				}
				Persist();
			}
			Logger.Log(Lang.T("log.protectedgameroster.3") + text);
			return true;
		}

		public static int Clear()
		{
			int count;
			lock (lk)
			{
				count = Load().Count;
				if (count == 0)
				{
					return 0;
				}
				cache.Clear();
				Persist();
			}
			Logger.Log(Lang.T("log.protectedgameroster.4") + count);
			return count;
		}

		public static string[] Names()
		{
			lock (lk)
			{
				List<string> list = new List<string>();
				foreach (KeyValuePair<string, string> item in Load())
				{
					if (Current(item.Value))
					{
						list.Add(item.Key);
					}
				}
				list.Sort(StringComparer.OrdinalIgnoreCase);
				return list.ToArray();
			}
		}
	}
}
