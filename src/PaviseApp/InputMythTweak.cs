using Microsoft.Win32;

namespace PaviseApp
{
	internal static class InputMythTweak
	{
		// Legacy recovery only: retain original values saved before queue correction was retired.
		private static readonly ReversibleReg MouseQueue = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\mouclass\\Parameters", "MouseDataQueueSize", RegistryValueKind.DWord, "PrevMouseQueue");

		private static readonly ReversibleReg KbdQueue = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Services\\kbdclass\\Parameters", "KeyboardDataQueueSize", RegistryValueKind.DWord, "PrevKbdQueue");

		private static readonly object lk = new object();

		public static bool Restore()
		{
			lock (lk)
			{
				bool restoreSucceeded = MouseQueue.Restore();
				restoreSucceeded &= KbdQueue.Restore();
				if (restoreSucceeded)
				{
					Settings.Save("InputMythRepaired", false);
					Logger.Log(Lang.T("log.inputmythtweak.11"));
				}
				else
				{
					Logger.Log(Lang.T("log.inputmythtweak.12"));
				}
				return restoreSucceeded;
			}
		}

		public static bool HasResidue()
		{
			return Settings.Load("InputMythRepaired", false) || MouseQueue.HasBackup || KbdQueue.HasBackup;
		}
	}
}
