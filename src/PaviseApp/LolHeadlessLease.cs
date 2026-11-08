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
			string root2 = NormalizeRoot(root);
			if (root2 == null || pid <= 0 || creation <= 0)
			{
				return false;
			}
			value = "V2|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(root2)) + "|" + pid + "|" + creation;
			return true;
		}

		internal static bool TryParse(string value, out LolHeadlessLeaseInfo info)
		{
			info = null;
			if (string.IsNullOrWhiteSpace(value))
			{
				return false;
			}
			string valueTrimmed = value.Trim();
			if (!valueTrimmed.StartsWith("V2|", StringComparison.Ordinal))
			{
				return false;
			}
			string[] valueTrimmedParts = valueTrimmed.Split('|');
			int result;
			long result2;
			if (valueTrimmedParts.Length != 4 || !int.TryParse(valueTrimmedParts[2], out result) || result <= 0 || !long.TryParse(valueTrimmedParts[3], out result2) || result2 <= 0)
			{
				return false;
			}
			string @string;
			try
			{
				@string = Encoding.UTF8.GetString(Convert.FromBase64String(valueTrimmedParts[1]));
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
			uint numericValue = 2166136261u;
			string toUpperInvariantResult = (value ?? "").ToUpperInvariant();
			for (int i = 0; i < toUpperInvariantResult.Length; i++)
			{
				numericValue ^= toUpperInvariantResult[i];
				numericValue *= 16777619;
			}
			return numericValue.ToString("X8");
		}

		private static bool WithLeaseLock(Func<bool> action)
		{
			Mutex mutex = null;
			bool withLeaseLockReady = false;
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
					withLeaseLockReady = mutex.WaitOne(5000);
				}
				catch (AbandonedMutexException)
				{
					withLeaseLockReady = true;
				}
				if (!withLeaseLockReady)
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
				if (withLeaseLockReady && mutex != null)
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
				string getFullPathTrimmed = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
				return (getFullPathTrimmed.Length > 2) ? getFullPathTrimmed : null;
			}
			catch
			{
				return null;
			}
		}

		private static bool SameRoot(string left, string right)
		{
			string root = NormalizeRoot(left);
			string root2 = NormalizeRoot(right);
			if (root != null && root2 != null)
			{
				return string.Equals(root, root2, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}
}
