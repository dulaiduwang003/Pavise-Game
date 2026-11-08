using Microsoft.Win32;

namespace PaviseApp
{
	internal static class MpoTweak
	{
		private const string DwmKey = "SOFTWARE\\Microsoft\\Windows\\Dwm";

		private const string Val = "OverlayTestMode";

		private const int DisableValue = 5;

		private static readonly ReversibleReg Overlay = new ReversibleReg(Registry.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\Dwm", "OverlayTestMode", RegistryValueKind.DWord, "PrevMpoOverlay");

		public static bool DisabledByPavise
		{
			get
			{
				return Settings.Load("MpoOffByPavise", false);
			}
		}

		public static bool CurrentlyDisabled()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\Dwm"))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue("OverlayTestMode");
					return value is int && (int)value == 5;
				}
			}
			catch
			{
				return false;
			}
		}

		public static bool Restore()
		{
			try
			{
				bool restoreReady = false;
				bool restoreReady2 = true;
				if (Overlay.HasBackup)
				{
					restoreReady2 = Overlay.Restore();
					restoreReady = true;
				}
				else if (DisabledByPavise && CurrentlyDisabled())
				{
					restoreReady2 = RemoveValue();
					restoreReady = true;
				}
				if (!restoreReady2)
				{
					return false;
				}
				Settings.Save("MpoOffByPavise", false);
				if (Settings.Load("MpoOffByPavise", true))
				{
					return false;
				}
				if (restoreReady)
				{
					Logger.Log(Lang.T("log.mpotweak.1"));
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static bool RemoveValue()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\Dwm", true))
				{
					if (registryKey == null)
					{
						return true;
					}
					if (registryKey.GetValue("OverlayTestMode") != null)
					{
						registryKey.DeleteValue("OverlayTestMode", false);
					}
					return registryKey.GetValue("OverlayTestMode") == null;
				}
			}
			catch
			{
				return false;
			}
		}
	}
}
