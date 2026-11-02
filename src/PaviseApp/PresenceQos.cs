using System;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class PresenceQos
	{
		internal const string JournalKey = "PrevPresenceQos";

		private static readonly ReversibleReg Switch = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling", "DisableUserPresenceQos", RegistryValueKind.DWord, "PrevPresenceQos");

		private static readonly object lk = new object();

		private static bool active;

		public static bool HasResidue
		{
			get
			{
				return Switch.HasBackup;
			}
		}

		public static bool Activate()
		{
			lock (lk)
			{
				if (active)
				{
					return true;
				}
				active = Switch.Apply(1);
				Logger.Log(active ? Lang.T("log.presenceqos.1") : Lang.T("log.presenceqos.2"));
				return active;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				if (Switch.HasBackup && Switch.Restore())
				{
					Logger.Log(Lang.T("log.presenceqos.3"));
				}
				active = false;
				return !Switch.HasBackup;
			}
		}

		public static void HealFromCrash()
		{
			if (Switch.HasBackup)
			{
				Restore();
			}
		}

		public static bool CurrentlyDisabled()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Power\\PowerThrottling"))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue("DisableUserPresenceQos");
					return value != null && Convert.ToInt32(value) == 1;
				}
			}
			catch
			{
				return false;
			}
		}
	}
}
