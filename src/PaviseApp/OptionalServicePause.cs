using System;

namespace PaviseApp
{
	internal static class OptionalServicePause
	{
		private sealed class SettingsLedger : IOptionalServiceLedger
		{
			public bool TryRead(out string value)
			{
				return Settings.TryLoadStr("PrevOptionalServicesPausedV1", out value);
			}

			public bool TryWrite(string value)
			{
				return Settings.SaveStr("PrevOptionalServicesPausedV1", value);
			}
		}

		internal const string LedgerKey = "PrevOptionalServicesPausedV1";

		private static readonly OptionalServicePauseEngine engine = new OptionalServicePauseEngine(new OptionalServiceControl(), new SettingsLedger());

		public static bool Active
		{
			get
			{
				return engine.Active;
			}
		}

		public static string LastFailure
		{
			get
			{
				return engine.LastFailure;
			}
		}

		public static int PendingStopCount
		{
			get
			{
				return engine.PendingStopCount;
			}
		}

		public static bool HasResidue
		{
			get
			{
				return engine.HasResidue;
			}
		}

		internal static bool MatchesSelection(bool includeBase)
		{
			return engine.MatchesSelection(false, includeBase);
		}

        internal static string[] CaptureConfirmedStops() { return engine.CaptureConfirmedStops(); }

		public static bool ObserveStops()
		{
			return engine.ObserveStops();
		}

		public static bool AuditStops(Func<bool> mayContinue)
		{
			lock (SystemConfigurationGate.Sync)
			{
				return engine.AuditStops(mayContinue);
			}
		}

		public static bool Restore()
		{
			lock (SystemConfigurationGate.Sync)
			{
				return engine.Restore();
			}
		}

		public static void HealFromCrash()
		{
			if (HasResidue)
			{
				Restore();
			}
		}
	}
}
