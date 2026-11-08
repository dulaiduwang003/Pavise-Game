using System.Collections.Generic;

namespace PaviseApp
{
	internal static class UpdatePause
	{
		internal const string Flag = "PrevUpdatePaused";

		private static readonly string[] Names = new string[2] { "wuauserv", "UsoSvc" };

		private static readonly ServicePauser pauser = new ServicePauser(Names, "PrevUpdatePaused");

		public static bool HasResidue
		{
			get
			{
				return pauser.HasResidue;
			}
		}

        internal static string[] CaptureConfirmedStops() { return pauser.CaptureConfirmedStops(); }

		public static bool Activate()
		{
			List<string> justStopped;
			List<string> confirmedStopped;
			bool ledgerLost;
			bool result = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
			if (ledgerLost)
			{
				Logger.Log(Lang.T("log.updatepause.1"));
				return false;
			}
			if (justStopped.Count > 0)
			{
				Logger.Log(Lang.T("log.updatepause.2") + string.Join(" + ", justStopped.ToArray()));
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
					Logger.Log(Lang.T("log.updatepause.3"));
				}
				else
				{
					Logger.Log(Lang.T("log.updatepause.4") + ((remain.Count == 0) ? Lang.T("t.versionmigrations.2") : string.Join(",", remain.ToArray())) + Lang.T("log.svcpause.6"));
				}
			}
			return restoreSucceeded;
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
