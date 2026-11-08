using System;

namespace PaviseApp
{
	internal static class IntelEndurance
	{
		internal const string SnapKey = "IntelEnduranceSnap";

		private const int ControlOff = 0;

		private static readonly object lk = new object();

		private static readonly IntelGraphicsApi api = new IntelGraphicsApi();

		private static long supportChecked;

		private static bool supportAvailable;

		public static bool HasResidue
		{
			get
			{
				return Settings.LoadStr("IntelEnduranceSnap", "").Length > 0;
			}
		}

		internal static bool Wanted(int control)
		{
			if (control != 1)
			{
				return control == 2;
			}
			return true;
		}

		public static bool Supported()
		{
			lock (lk)
			{
				if (DateTime.UtcNow.Ticks < supportChecked)
				{
					return supportAvailable;
				}
				int control;
				int mode;
				supportAvailable = Native.HasSystemBattery() && Read(out control, out mode);
				supportChecked = DateTime.UtcNow.AddSeconds(30.0).Ticks;
				return supportAvailable;
			}
		}

		public static bool Activate()
		{
			lock (lk)
			{
				if (HasResidue)
				{
					return Restore() && Activate();
				}
				int control;
				int mode;
				if (!Read(out control, out mode))
				{
					Logger.Warn(Lang.T("log.intelend.1"));
					return false;
				}
				if (control == 0)
				{
					return true;
				}
				if (!Wanted(control))
				{
					return false;
				}
				string modeOffset = control + "|" + mode;
				if (!Settings.SaveStrDurable("IntelEnduranceSnap", modeOffset) || Settings.LoadStr("IntelEnduranceSnap", "") != modeOffset)
				{
					return false;
				}
				int control2;
				int mode2;
				if (!Write(0, mode) || !Read(out control2, out mode2) || control2 != 0)
				{
					Logger.Log(Lang.T("log.intelend.2"));
					return false;
				}
				Logger.Log(Lang.T("log.intelend.3"));
				return true;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string value;
				if (!Settings.TryLoadStr("IntelEnduranceSnap", out value))
				{
					return false;
				}
				if (value.Length == 0)
				{
					return true;
				}
				string[] valueParts = value.Split('|');
				int result;
				int result2;
				if (valueParts.Length != 2 || !int.TryParse(valueParts[0], out result) || !int.TryParse(valueParts[1], out result2) || !Wanted(result))
				{
					return false;
				}
				int control;
				int mode;
				if (!Read(out control, out mode))
				{
					return false;
				}
				if (control != 0 || mode != result2)
				{
					return Settings.SaveStrDurable("IntelEnduranceSnap", "");
				}
				if (!Write(result, result2) || !Read(out control, out mode) || control != result || mode != result2)
				{
					Logger.Log(Lang.T("log.intelend.4"));
					return false;
				}
				if (!Settings.SaveStrDurable("IntelEnduranceSnap", ""))
				{
					return false;
				}
				Logger.Log(Lang.T("log.intelend.5"));
				return true;
			}
		}

		private static bool Read(out int control, out int mode)
		{
			return api.TryReadEndurance(out control, out mode);
		}

		private static bool Write(int control, int mode)
		{
			return api.TryWriteEndurance(control, mode);
		}

		public static void HealFromCrash()
		{
			if (HasResidue && Restore())
			{
				Logger.Log(Lang.T("log.intelend.6"));
			}
		}
	}
}
