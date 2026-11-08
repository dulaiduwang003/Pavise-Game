using System.Collections.Generic;

namespace PaviseApp
{
	internal static class SvcPause
	{
		private const string Flag = "PrevSvcPaused";

		internal static readonly string[] Names = new string[2] { "SysMain", "WSearch" };

		private static readonly ServicePauser pauser = new ServicePauser(Names, "PrevSvcPaused");

        internal static string[] CaptureConfirmedStops() { return pauser.CaptureConfirmedStops(); }

		public static bool Activate()
		{
			List<string> justStopped;
			List<string> confirmedStopped;
			bool ledgerLost;
			bool result = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
			if (ledgerLost)
			{
				Logger.Log(Lang.T("log.svcpause.1"));
				return false;
			}
			if (confirmedStopped.Count > 0)
			{
				Logger.Log(Lang.T("log.svcpause.2") + string.Join(" ", confirmedStopped.ToArray()));
			}
			else if (justStopped.Count > 0)
			{
				Logger.Log(Lang.T("log.svcpause.3") + string.Join(" ", justStopped.ToArray()));
			}
			return result;
		}

		public static bool Restore()
		{
			bool hadLedger = pauser.HadLedger;
			List<string> remain;
			bool restoreSucceeded = pauser.Restore(out remain);
			if (hadLedger)
			{
				if (restoreSucceeded)
				{
					Logger.Log(Lang.T("log.svcpause.4"));
				}
				else
				{
					Logger.Log(Lang.T("log.svcpause.5") + ((remain.Count == 0) ? Lang.T("t.versionmigrations.2") : string.Join(" ", remain.ToArray())) + Lang.T("log.svcpause.6"));
				}
			}
			return restoreSucceeded;
		}

		public static bool HasResidue()
		{
			return pauser.HasResidue;
		}

		public static void HealFromCrash()
		{
			if (pauser.HasResidue)
			{
				Restore();
			}
		}
	}
}
