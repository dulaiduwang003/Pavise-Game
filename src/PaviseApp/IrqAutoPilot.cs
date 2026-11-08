using System.Collections.Generic;

namespace PaviseApp
{
	internal static class IrqAutoPilot
	{
		internal const string EnabledKey = "IrqAutoOn";

		internal const string PlanKey = "IrqAutoPlanV1";

		internal const string FuseKey = "IrqAutoFuseV1";

		private static readonly object lk = new object();

		private static readonly IrqAffinityEngine engine = new IrqAffinityEngine("IrqAutoAppliedV1", "IrqAuto_", Lang.T("irqauto.logprefix"));

		public static bool HasResidue
		{
			get
			{
				if (!engine.HasResidue)
				{
					return Settings.LoadStr("IrqAutoPlanV1", "").Length != 0;
				}
				return true;
			}
		}

		public static List<string> TouchedDevices()
		{
			return engine.TouchedDevices();
		}

		public static bool RevertAll()
		{
			lock (lk)
			{
				bool disableSucceeded = engine.Disable(null);
				if (disableSucceeded && !Settings.SaveStr("IrqAutoPlanV1", ""))
				{
					disableSucceeded = false;
				}
				return disableSucceeded;
			}
		}

		public static void HealFromCrash()
		{
			try
			{
				Settings.Save("IrqAutoOn", false);
				if (HasResidue && RevertAll())
				{
					Settings.SaveStr("IrqAutoFuseV1", "");
				}
			}
			catch
			{
			}
		}

		public static void ClearForReset()
		{
			lock (lk)
			{
				Settings.SaveStr("IrqAutoPlanV1", "");
				Settings.SaveStr("IrqAutoFuseV1", "");
				Settings.Save("IrqAutoOn", false);
			}
		}
	}
}
