using Microsoft.Win32;

namespace PaviseApp
{
	internal static class NetTweak
	{
		public const int SystemDefault = 10;

		// REG_DWORD is read as a signed Int32. 0xFFFFFFFF disables throttling.
		public const int ThrottlingDisabled = -1;

		private static readonly ReversibleReg Throttle = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile", "NetworkThrottlingIndex", RegistryValueKind.DWord, "PrevNetThrottle");

		private static readonly object lk = new object();

		public static bool RepairedByPavise
		{
			get
			{
				// The journal is authoritative even if saving the UI marker failed.
				return Throttle.HasBackup;
			}
		}

		public static int? Current()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile"))
				{
					if (registryKey == null)
					{
						return null;
					}
					object value = registryKey.GetValue("NetworkThrottlingIndex");
					return (value is int) ? new int?((int)value) : null;
				}
			}
			catch
			{
				return null;
			}
		}

		public static bool NeedsRepair()
		{
			int? currentResult = Current();
			if (!currentResult.HasValue)
			{
				return false;
			}
			return !IsValid(currentResult.Value);
		}

		private static bool IsValid(int value)
		{
			return value == ThrottlingDisabled || (value >= 1 && value <= 70);
		}

		public static string Describe()
		{
			int? currentResult = Current();
			if (!currentResult.HasValue)
			{
				return Lang.T("netthrottle.absent");
			}
			if (currentResult.Value == ThrottlingDisabled)
			{
				return Lang.T("netthrottle.disabled");
			}
			if (!IsValid(currentResult.Value))
			{
				return Lang.F("netthrottle.broken", "0x" + currentResult.Value.ToString("X"));
			}
			return Lang.F("netthrottle.ok", currentResult.Value);
		}

		public static bool Repair()
		{
			lock (lk)
			{
				if (!NeedsRepair())
				{
					Logger.Log(Lang.T("log.nettweak.1"));
					return true;
				}
				if (!Throttle.Apply(SystemDefault))
				{
					Logger.Log(Lang.T("log.nettweak.2"));
					return false;
				}
				Settings.Save("NetThrottleRepaired", true);
				Logger.Log(Lang.T("log.nettweak.3") + SystemDefault + Lang.T("log.nettweak.4"));
				return true;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				bool matchesRestore = !Throttle.HasBackup || Throttle.Restore();
				if (matchesRestore)
				{
					Settings.Save("NetThrottleRepaired", false);
					Logger.Log(Lang.T("log.nettweak.5"));
				}
				return matchesRestore;
			}
		}

		public static void HealFromCrash()
		{
		}
	}
}
