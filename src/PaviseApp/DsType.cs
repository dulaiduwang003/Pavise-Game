using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class DsType
	{
		public static float Size(DsText role)
		{
			switch (role)
			{
			case DsText.BodyLarge:
				return 9.5f;
			case DsText.Title:
				return 9.75f;
			case DsText.Heading:
				return 12.5f;
			case DsText.Label:
				return 8.25f;
			case DsText.Description:
				return 8.5f;
			case DsText.Status:
				return 8.2f;
			case DsText.Hint:
				return 7.8f;
			case DsText.Tag:
				return 7.2f;
			case DsText.Caption:
				return 7.9f;
			default:
				return 9f;
			}
		}

		public static float MonoSize(DsMono role)
		{
			switch (role)
			{
			case DsMono.Badge:
				return 7f;
			case DsMono.TabNumber:
				return 7.5f;
			case DsMono.Meta:
				return 7f;
			case DsMono.Channel:
				return 5.8f;
			case DsMono.Surface:
				return 5.4f;
			default:
				return 5.5f;
			}
		}

		public static Font Font(DsText role, bool bold)
		{
			return Theme.UI(Size(role), bold);
		}

		public static Font Font(DsText role)
		{
			return Theme.UI(Size(role), false);
		}

		public static Font Mono(DsMono role)
		{
			return Theme.Mono(MonoSize(role));
		}

		public static Font MonoFor(DsMono role, string text)
		{
			return Theme.MonoFor(text, MonoSize(role));
		}

		public static int LineHeight(DsText role)
		{
			return TextRenderer.MeasureText("Ag", Font(role)).Height;
		}
	}
}
