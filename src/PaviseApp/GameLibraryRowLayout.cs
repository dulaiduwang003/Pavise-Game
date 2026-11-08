using System;
using System.Drawing;

namespace PaviseApp
{
	internal struct GameLibraryRowLayout
	{
		public Rectangle Icon;

		public Rectangle Running;

		public Rectangle Name;

		public Rectangle Path;

		public Rectangle Metadata;

		public Rectangle Policy;

		public Rectangle Scheduling;

		public Rectangle Rebar;

		public Rectangle FullscreenOptimizations;

		public Rectangle More;

		private static float measuredScale;

		private static int measuredLanguage = -1;

		private static int schedulingWidth;

		private static int familyWidth;

		private static int rebarWidth;

		private static int fullscreenWidth;

		private static void MeasurePolicies()
		{
			if (measuredLanguage != Lang.Cur || measuredScale != Dpi.Scale)
			{
				measuredLanguage = Lang.Cur;
				measuredScale = Dpi.Scale;
				schedulingWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("nav.corescheduling"), true);
				familyWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("lib.family.compact"), false);
				rebarWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("lib.rebar"), false);
				fullscreenWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("lib.fso"), false);
			}
		}

		private static Rectangle PlacePolicy(int preferredWidth, int available, ref int x, ref int y)
		{
			int scaledDimension = Theme.S(14);
			int availableLimit = Math.Min(preferredWidth, available);
			if (x > scaledDimension && x + availableLimit > scaledDimension + available)
			{
				x = scaledDimension;
				y += Theme.S(32) + Theme.S(8);
			}
			Rectangle result = new Rectangle(x, y, availableLimit, Theme.S(32));
			x = result.Right + Theme.S(10);
			return result;
		}

		private static GameLibraryRowLayout PolicyLayout(int width)
		{
			MeasurePolicies();
			GameLibraryRowLayout result = default(GameLibraryRowLayout);
			int x = Theme.S(14);
			int y = Theme.S(72);
			int available = Math.Max(1, width - Theme.S(14) * 2);
			result.Scheduling = PlacePolicy(schedulingWidth, available, ref x, ref y);
			result.Policy = PlacePolicy(familyWidth, available, ref x, ref y);
			result.Rebar = PlacePolicy(rebarWidth, available, ref x, ref y);
			result.FullscreenOptimizations = PlacePolicy(fullscreenWidth, available, ref x, ref y);
			return result;
		}

		public static int HeightForWidth(int width)
		{
			return PolicyLayout(width).FullscreenOptimizations.Bottom + Theme.S(8);
		}

		public static GameLibraryRowLayout ForSize(Size size)
		{
			GameLibraryRowLayout result = PolicyLayout(size.Width);
			int scaledDimension = Theme.S(14);
			int scaledDimension2 = Theme.S(70);
			int remainingWidth = size.Width - scaledDimension;
			int width = Math.Max(1, remainingWidth - scaledDimension2);
			result.Icon = new Rectangle(Theme.S(18), Theme.S(16), Theme.S(36), Theme.S(36));
			result.More = new Rectangle(remainingWidth - Theme.S(28), Theme.S(10), Theme.S(28), Theme.S(22));
			result.Running = new Rectangle(result.More.Left - Theme.S(80), Theme.S(12), Theme.S(72), Theme.S(20));
			result.Name = new Rectangle(scaledDimension2, Theme.S(10), Math.Max(1, result.Running.Left - scaledDimension2 - Theme.S(10)), Theme.S(22));
			result.Path = new Rectangle(scaledDimension2, Theme.S(32), width, Theme.S(18));
			result.Metadata = new Rectangle(scaledDimension2, Theme.S(52), width, Theme.S(16));
			return result;
		}
	}
}
