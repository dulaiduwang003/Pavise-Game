using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class DoTweak
	{
		internal const string BandwidthJournalKey = "PrevDoBgBw";

		internal const string StopFlag = "PrevDoSvcStopped";

		private const string SvcName = "DoSvc";

		private static readonly ReversibleReg BgBw = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization", "DOMaxBackgroundDownloadBandwidth", RegistryValueKind.DWord, "PrevDoBgBw");

		private static readonly ServicePauser pauser = new ServicePauser(new string[1] { "DoSvc" }, "PrevDoSvcStopped");

		private static readonly object lk = new object();

		private static bool active;

		public static bool HasResidue
		{
			get
			{
				lock (lk)
				{
					return HasBandwidthBackup() || pauser.HasResidue;
				}
			}
		}

        internal static string[] CaptureConfirmedStops() { return pauser.CaptureConfirmedStops(); }

		public static bool Activate()
		{
			lock (lk)
			{
				if (active)
				{
					return true;
				}
				bool flag = false;
				if (!IsDomainJoined())
				{
					flag = ApplyBandwidth();
				}
				else
				{
					Logger.Log(Lang.T("log.dotweak.9"));
				}
				List<string> justStopped;
				List<string> confirmedStopped;
				bool ledgerLost;
				bool flag2 = pauser.Activate(out justStopped, out confirmedStopped, out ledgerLost);
				if (ledgerLost)
				{
					Logger.Log(Lang.T("log.dotweak.1"));
				}
				active = flag || (flag2 && justStopped.Count > 0);
				Logger.Log(active ? (Lang.T("log.dotweak.2") + (flag ? Lang.T("log.dotweak.3") : Lang.T("log.dotweak.4")) + ((flag2 && confirmedStopped.Count > 0) ? Lang.T("log.dotweak.5") : "") + " ") : Lang.T("log.dotweak.6"));
				return active;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				bool flag = HasBandwidthBackup();
				bool flag2 = !flag || RestoreBandwidth();
				bool hasResidue = pauser.HasResidue;
				List<string> remain;
				bool flag3 = pauser.Restore(out remain);
				if (!flag3)
				{
					Logger.Log(Lang.T("log.dotweak.7"));
				}
				active = false;
				bool flag4 = flag2 && !HasBandwidthBackup() && flag3 && !pauser.HasResidue;
				if (flag4 && (flag || hasResidue))
				{
					Logger.Log(Lang.T("log.dotweak.8"));
				}
				return flag4;
			}
		}

		public static void HealFromCrash()
		{
			if (HasResidue)
			{
				Restore();
			}
		}

		private static bool IsDomainJoined()
		{
			try
			{
				return Native.IsDomainJoined();
			}
			catch
			{
				return true;
			}
		}

		private static bool HasBandwidthBackup()
		{
			try
			{
				return BgBw.HasBackup;
			}
			catch
			{
				return true;
			}
		}

		private static bool ApplyBandwidth()
		{
			try
			{
				return BgBw.Apply(1);
			}
			catch
			{
				return false;
			}
		}

		private static bool RestoreBandwidth()
		{
			try
			{
				return BgBw.Restore();
			}
			catch
			{
				return false;
			}
		}
	}
}
