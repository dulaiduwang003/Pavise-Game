using System.Windows.Forms;

namespace PaviseApp
{
	internal static class DsInteraction
	{
		public static bool CanActivate(Control control, bool busy)
		{
			if (control != null && !control.IsDisposed && control.Enabled)
			{
				return !busy;
			}
			return false;
		}

		public static bool CanActivate(Control control)
		{
			IBusyInteractive busyInteractive = control as IBusyInteractive;
			return CanActivate(control, busyInteractive != null && busyInteractive.Busy);
		}
	}
}
