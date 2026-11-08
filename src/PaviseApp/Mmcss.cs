using Microsoft.Win32;

namespace PaviseApp
{
	internal static class Mmcss
	{
		private const string Prof = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile";

		private const string Games = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games";

		internal const int Responsiveness = 10;

		internal const string HighCategory = "High";

		private static readonly ReversibleReg Resp = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile", "SystemResponsiveness", RegistryValueKind.DWord, "Mmcss_Resp");

		private static readonly ReversibleReg Pri = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games", "Priority", RegistryValueKind.DWord, "Mmcss_Pri");

		private static readonly ReversibleReg Sched = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games", "Scheduling Category", RegistryValueKind.String, "Mmcss_Sched");

		private static readonly ReversibleReg Sfio = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\\Tasks\\Games", "SFIO Priority", RegistryValueKind.String, "Mmcss_Sfio");

		private static readonly ReversibleReg Lazy = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile", "NoLazyMode", RegistryValueKind.DWord, "Mmcss_NoLazy");

		private static readonly ReversibleReg[] All = new ReversibleReg[5] { Resp, Pri, Sched, Sfio, Lazy };

		private static readonly object lk = new object();

		private static bool active;

		private static ReversibleReg[] Values
		{
			get
			{
				return All;
			}
		}

		public static bool Activate()
		{
			lock (lk)
			{
				ReversibleReg[] values = Values;
				if (values == null)
				{
					return false;
				}
				if (active && HasResidue())
				{
					return true;
				}
				if (!Native.IsElevated())
				{
					return false;
				}
				if (!(values[0].Apply(10) & values[2].Apply("High") & values[4].Apply(1)))
				{
					ReversibleReg[] valuesSnapshot = values;
					foreach (ReversibleReg reversibleReg in valuesSnapshot)
					{
						reversibleReg.Restore();
					}
					active = false;
					Logger.Log(Lang.T("log.mmcss.1"));
					return false;
				}
				active = true;
				Logger.Log(Lang.T("log.mmcss.2"));
				return true;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				bool hasResidue = HasResidue();
				ReversibleReg[] values = Values;
				if (values == null)
				{
					if (!hasResidue)
					{
						active = false;
					}
					return !hasResidue;
				}
				bool restoreSucceeded = true;
				ReversibleReg[] valuesSnapshot = values;
				foreach (ReversibleReg reversibleReg in valuesSnapshot)
				{
					restoreSucceeded &= reversibleReg.Restore();
				}
				active = false;
				if (hasResidue)
				{
					Logger.Log(Lang.T(restoreSucceeded ? "log.mmcss.3" : "log.mmcss.4"));
				}
				return restoreSucceeded;
			}
		}

		public static bool HasResidue()
		{
			ReversibleReg[] all = All;
			foreach (ReversibleReg reversibleReg in all)
			{
				if (reversibleReg.HasBackup)
				{
					return true;
				}
			}
			return false;
		}

		public static void HealFromCrash()
		{
			if (HasResidue())
			{
				Restore();
			}
		}
	}
}
