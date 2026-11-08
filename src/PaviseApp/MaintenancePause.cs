namespace PaviseApp
{
	internal static class MaintenancePause
	{
		private sealed class SettingsLedger : IOptionalServiceLedger
		{
			public bool TryRead(out string value)
			{
				return Settings.TryLoadStr("PrevMaintDisabled", out value);
			}

			public bool TryWrite(string value)
			{
				return Settings.SaveStr("PrevMaintDisabled", value);
			}
		}

		internal const string JournalKey = "PrevMaintDisabled";

		private static readonly MaintenancePauseEngine engine = new MaintenancePauseEngine(new MaintenanceControl(), new SettingsLedger());

		internal static bool ConfigurationOnly
		{
			get
			{
				return true;
			}
		}

		public static bool HasResidue
		{
			get
			{
				return engine.HasResidue;
			}
		}

		public static bool Active
		{
			get
			{
				return engine.Active;
			}
		}

		internal static MaintenanceRuntimeSnapshot QueryRuntime()
		{
			return engine.QueryRuntime();
		}

		public static bool Activate()
		{
			lock (SystemConfigurationGate.Sync)
			{
				if (!Native.IsElevated())
				{
					return false;
				}
				bool activateSucceeded = engine.Activate();
				Logger.Log(Lang.T(activateSucceeded ? "log.maint.2" : "log.maint.1"));
				return activateSucceeded;
			}
		}

		public static bool Restore()
		{
			lock (SystemConfigurationGate.Sync)
			{
				bool hasResidue = engine.HasResidue;
				bool restoreSucceeded = engine.Restore();
				if (hasResidue)
				{
					Logger.Log(Lang.T(restoreSucceeded ? "log.maint.3" : "log.maint.4"));
				}
				return restoreSucceeded;
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
