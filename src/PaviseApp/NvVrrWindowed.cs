using System;

namespace PaviseApp
{
	internal static class NvVrrWindowed
	{
		internal const string SnapKey = "NvVrrModeSnap";

		private const uint SettingVrrMode = 294973784u;

		private const uint VrrDisabled = 0u;

		private const uint VrrFullscreenOnly = 1u;

		private const uint VrrFullscreenAndWindowed = 2u;

		private static readonly object lk = new object();

		private static bool DriverAvailable
		{
			get
			{
				return NvApi.Available;
			}
		}

		public static bool HasResidue
		{
			get
			{
				return Settings.LoadStr("NvVrrModeSnap", "").Length > 0;
			}
		}

		internal static bool ShouldExpand(uint mode)
		{
			return mode == 1;
		}

		private static bool ReadMode(out uint mode)
		{
			mode = 0u;
			IntPtr session;
			if (!NvApi.TryOpenSession(out session))
			{
				return false;
			}
			try
			{
				IntPtr profile;
				if (!NvApi.TryGetBaseProfile(session, out profile))
				{
					return false;
				}
				uint value;
				int dword = NvApi.TryGetDword(session, profile, 294973784u, out value);
				if (dword < 0)
				{
					return false;
				}
				mode = ((dword == 1) ? value : 0u);
				return true;
			}
			finally
			{
				NvApi.CloseSession(session);
			}
		}

		private static bool WriteMode(uint mode)
		{
			IntPtr session;
			if (!NvApi.TryOpenSession(out session))
			{
				return false;
			}
			try
			{
				IntPtr profile;
				if (!NvApi.TryGetBaseProfile(session, out profile))
				{
					return false;
				}
				if (!NvApi.SetDword(session, profile, 294973784u, mode))
				{
					return false;
				}
				return NvApi.SaveSession(session);
			}
			finally
			{
				NvApi.CloseSession(session);
			}
		}

		public static bool Applicable()
		{
			uint mode;
			if (DriverAvailable && ReadMode(out mode))
			{
				return ShouldExpand(mode);
			}
			return false;
		}

		public static bool Activate()
		{
			lock (lk)
			{
				if (HasResidue)
				{
					uint mode;
					if (!ReadMode(out mode))
					{
						return false;
					}
					if (mode == 2)
					{
						return true;
					}
					if (!Restore())
					{
						return false;
					}
				}
				uint mode2;
				if (!DriverAvailable || !ReadMode(out mode2))
				{
					Logger.Warn(Lang.T("log.nvvrr.1"));
					return false;
				}
				if (!ShouldExpand(mode2))
				{
					return true;
				}
				Settings.SaveStr("NvVrrModeSnap", mode2.ToString());
				if (Settings.LoadStr("NvVrrModeSnap", "") != mode2.ToString())
				{
					return false;
				}
				if (!WriteMode(2u))
				{
					Logger.Log(Lang.T("log.nvvrr.2"));
					return false;
				}
				uint mode3;
				bool matchesReadMode = ReadMode(out mode3) && mode3 == 2;
				if (matchesReadMode)
				{
					Logger.Log(Lang.T("log.nvvrr.3"));
				}
				return matchesReadMode;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string nvVrrModeSnapValue = Settings.LoadStr("NvVrrModeSnap", "");
				if (nvVrrModeSnapValue.Length == 0)
				{
					return true;
				}
				uint result;
				if (!uint.TryParse(nvVrrModeSnapValue, out result) || result != 1)
				{
					return false;
				}
				uint mode;
				if (!ReadMode(out mode))
				{
					return false;
				}
				if (mode != 2)
				{
					Logger.Log(Lang.T("log.nvvrr.5"));
					return Settings.SaveStr("NvVrrModeSnap", "");
				}
				if (!WriteMode(result))
				{
					Logger.Log(Lang.T("log.nvvrr.4"));
					return false;
				}
				if (!ReadMode(out mode) || mode != result || !Settings.SaveStr("NvVrrModeSnap", ""))
				{
					return false;
				}
				Logger.Log(Lang.T("log.nvvrr.6"));
				return true;
			}
		}

		public static void HealFromCrash()
		{
			if (HasResidue && Restore())
			{
				Logger.Log(Lang.T("log.nvvrr.7"));
			}
		}
	}
}
