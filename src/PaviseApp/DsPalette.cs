using System.Drawing;

namespace PaviseApp
{
	internal static class DsPalette
	{
		public static Color Alert
		{
			get
			{
				return Color.FromArgb(255, 176, 32);
			}
		}

		public static Color KnobOn
		{
			get
			{
				return Color.White;
			}
		}

		public static Color KnobOff
		{
			get
			{
				return Color.FromArgb(178, 183, 192);
			}
		}

		public static Color DangerTopDark
		{
			get
			{
				return Color.FromArgb(38, 19, 24);
			}
		}

		public static Color DangerBottomDark
		{
			get
			{
				return Color.FromArgb(21, 15, 18);
			}
		}

		public static Color Ink(DsTone tone)
		{
			switch (tone)
			{
			case DsTone.Accent:
				return Theme.Accent;
			case DsTone.Success:
				return Theme.Green;
			case DsTone.Warning:
				return Theme.Warning;
			case DsTone.Danger:
				return Theme.Danger;
			default:
				return Theme.Fg;
			}
		}
	}
}
