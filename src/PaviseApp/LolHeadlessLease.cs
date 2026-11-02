using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class LolHeadlessLease
	{
		private const string Key = "LolHeadlessEngagedRoot";

		private const string Prefix = "V2|";

		private const string GlobalMutex = "Global\\Pavise_LolHeadlessLease";

		private const string LocalMutex = "Pavise_LolHeadlessLease";

		public static bool Exists()
		{
			LolHeadlessLeaseInfo info;
			return TryRead(out info);
		}

		public static bool MatchesRoot(string root)
		{
			LolHeadlessLeaseInfo info;
			if (TryRead(out info))
			{
				return SameRoot(info.Root, root);
			}
			return false;
		}

		public static bool Matches(string root, int pid, long creation)
		{
			LolHeadlessLeaseInfo info;
			if (!TryRead(out info) || !SameRoot(info.Root, root))
			{
				return false;
			}
			if (pid > 0 && creation > 0 && info.GamePid == pid)
			{
				return info.GameCreation == creation;
			}
			return false;
		}

		public static bool Set(string root, int pid, long creation)
		{
			string value;
			if (!TrySerialize(root, pid, creation, out value))
			{
				return false;
			}
			return WithLeaseLock(delegate
			{
				Settings.SaveStr("LolHeadlessEngagedRoot", value);
				return string.Equals(Settings.LoadStr("LolHeadlessEngagedRoot", ""), value, StringComparison.Ordinal);
			});
		}

		public static void Clear()
		{
			WithLeaseLock(() => Settings.SaveStr("LolHeadlessEngagedRoot", "") && string.IsNullOrEmpty(Settings.LoadStr("LolHeadlessEngagedRoot", "")));
		}

		public static bool ClearIfMatches(string root, int pid, long creation)
		{
			return WithLeaseLock(delegate
			{
				LolHeadlessLeaseInfo info;
				if (!TryRead(out info) || !SameRoot(info.Root, root) || info.GamePid != pid || info.GameCreation != creation)
				{
					return false;
				}
				return Settings.SaveStr("LolHeadlessEngagedRoot", "") && string.IsNullOrEmpty(Settings.LoadStr("LolHeadlessEngagedRoot", ""));
			});
		}

		public static bool TryRead(out LolHeadlessLeaseInfo info)
		{
			return TryParse(Settings.LoadStr("LolHeadlessEngagedRoot", ""), out info);
		}

		internal static bool TrySerialize(string root, int pid, long creation, out string value)
		{
			value = null;
			string text = NormalizeRoot(root);
			if (text == null || pid <= 0 || creation <= 0)
			{
				return false;
			}
			value = "V2|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "|" + pid + "|" + creation;
			return true;
		}

		internal static bool TryParse(string value, out LolHeadlessLeaseInfo info)
		{
			info = null;
			if (string.IsNullOrWhiteSpace(value))
			{
				return false;
			}
			string text = value.Trim();
			if (!text.StartsWith("V2|", StringComparison.Ordinal))
			{
				return false;
			}
			string[] array = text.Split('|');
			int result;
			long result2;
			if (array.Length != 4 || !int.TryParse(array[2], out result) || result <= 0 || !long.TryParse(array[3], out result2) || result2 <= 0)
			{
				return false;
			}
			string @string;
			try
			{
				@string = Encoding.UTF8.GetString(Convert.FromBase64String(array[1]));
			}
			catch
			{
				return false;
			}
			@string = NormalizeRoot(@string);
			if (@string == null)
			{
				return false;
			}
			info = new LolHeadlessLeaseInfo
			{
				Root = @string,
				GamePid = result,
				GameCreation = result2
			};
			return true;
		}

		internal static string StableHash(string value)
		{
			uint num = 2166136261u;
			string text = (value ?? "").ToUpperInvariant();
			for (int i = 0; i < text.Length; i++)
			{
				num ^= text[i];
				num *= 16777619;
			}
			return num.ToString("X8");
		}

		private static bool WithLeaseLock(Func<bool> action)
		{
			Mutex mutex = null;
			bool flag = false;
			try
			{
				try
				{
					mutex = new Mutex(false, "Global\\Pavise_LolHeadlessLease");
				}
				catch
				{
					mutex = new Mutex(false, "Pavise_LolHeadlessLease");
				}
				try
				{
					flag = mutex.WaitOne(5000);
				}
				catch (AbandonedMutexException)
				{
					flag = true;
				}
				if (!flag)
				{
					return false;
				}
				return action != null && action();
			}
			catch
			{
				return false;
			}
			finally
			{
				if (flag && mutex != null)
				{
					try
					{
						mutex.ReleaseMutex();
					}
					catch
					{
					}
				}
				if (mutex != null)
				{
					try
					{
						mutex.Close();
					}
					catch
					{
					}
				}
			}
		}

		private static string NormalizeRoot(string root)
		{
			if (string.IsNullOrWhiteSpace(root))
			{
				return null;
			}
			try
			{
				string text = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
				return (text.Length > 2) ? text : null;
			}
			catch
			{
				return null;
			}
		}

		private static bool SameRoot(string left, string right)
		{
			string text = NormalizeRoot(left);
			string text2 = NormalizeRoot(right);
			if (text != null && text2 != null)
			{
				return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}
}
