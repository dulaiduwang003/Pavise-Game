using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class DwmBoost
	{
		private static readonly object lk = new object();

		private static bool active;

		[DllImport("dwmapi.dll")]
		private static extern int DwmEnableMMCSS(bool enable);

		private static int Request(bool enabled)
		{
			return DwmEnableMMCSS(enabled);
		}

		public static bool Activate()
		{
			lock (lk)
			{
				if (active)
				{
					return true;
				}
				if (Request(true) != 0)
				{
					return false;
				}
				active = true;
				Logger.Log(Lang.T("log.dwmboost.1"));
				return true;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				if (!active)
				{
					return true;
				}
				if (Request(false) != 0)
				{
					return false;
				}
				active = false;
				Logger.Log(Lang.T("log.dwmboost.2"));
				return true;
			}
		}
	}
}
