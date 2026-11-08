using Microsoft.Win32;

namespace PaviseApp
{
	internal static class GameDvr
	{
		private static readonly ReversibleReg Dvr = new ReversibleReg(Registry.CurrentUser, "System\\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, "PrevGameDvr");

		private static readonly ReversibleReg Cap = new ReversibleReg(Registry.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, "PrevGameDvrCap");

		private static readonly object lk = new object();

		private static bool active;

		private static ReversibleReg[] Values
		{
			get
			{
				return new ReversibleReg[2] { Dvr, Cap };
			}
		}

		internal static bool HasResidue
		{
			get
			{
				if (!Dvr.HasBackup)
				{
					return Cap.HasBackup;
				}
				return true;
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
				if (active)
				{
					return true;
				}
				bool applySucceeded = values[0].Apply(0);
				bool applySucceeded2 = values[1].Apply(0);
				active = applySucceeded && applySucceeded2;
				Logger.Log(active ? Lang.T("log.gamedvr.1") : (applySucceeded ? Lang.T("log.gamedvr.2") : Lang.T("log.gamedvr.3")));
				return active;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				bool hasResidue = HasResidue;
				ReversibleReg[] values = Values;
				if (values == null)
				{
					if (!hasResidue)
					{
						active = false;
					}
					return !hasResidue;
				}
				bool matchesRestore = !values[0].HasBackup || values[0].Restore();
				bool matchesRestore2 = !values[1].HasBackup || values[1].Restore();
				if (hasResidue && matchesRestore && matchesRestore2)
				{
					Logger.Log(Lang.T("log.gamedvr.4"));
				}
				active = false;
				return !HasResidue;
			}
		}

		public static void HealFromCrash()
		{
			if (Dvr.HasBackup || Cap.HasBackup)
			{
				Restore();
			}
		}
	}
}
