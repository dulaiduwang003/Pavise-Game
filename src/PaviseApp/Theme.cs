using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class Theme
	{
		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
		private class LogFont
		{
			public int lfHeight;

			public int lfWidth;

			public int lfEscapement;

			public int lfOrientation;

			public int lfWeight;

			public byte lfItalic;

			public byte lfUnderline;

			public byte lfStrikeOut;

			public byte lfCharSet;

			public byte lfOutPrecision;

			public byte lfClipPrecision;

			public byte lfQuality;

			public byte lfPitchAndFamily;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
			public string lfFaceName = "";
		}

		private const byte ClearTypeQuality = 5;

		private static bool light;

		private static Color accent = Color.FromArgb(52, 208, 148);

		private static Color accent2 = Color.FromArgb(20, 136, 94);

		private static Color fromAccent = accent;

		private static Color fromAccent2 = accent2;

		private static Color toAccent = accent;

		private static Color toAccent2 = accent2;

		private static float themeT = 1f;

		private static PerformancePreset currentMode = PerformancePreset.Standard;

		private static readonly Color[] modeOverride = new Color[7]
		{
			Color.Empty,
			Color.Empty,
			Color.Empty,
			Color.Empty,
			Color.Empty,
			Color.Empty,
			Color.Empty
		};

		private static readonly Dictionary<int, Font> fontCache = new Dictionary<int, Font>();

		private static readonly object fontLk = new object();

		private static readonly Dictionary<int, Font> monoCache = new Dictionary<int, Font>();

		public static bool LightMode
		{
			get
			{
				return light;
			}
		}

		public static Color Bg
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(9, 10, 12);
				}
				return Color.FromArgb(238, 242, 247);
			}
		}

		public static Color Nav
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(6, 7, 9);
				}
				return Color.FromArgb(225, 231, 239);
			}
		}

		public static Color Card
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(17, 19, 23);
				}
				return Color.FromArgb(251, 252, 254);
			}
		}

		public static Color CardHover
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(23, 27, 33);
				}
				return Color.FromArgb(245, 248, 252);
			}
		}

		public static Color Inset
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(7, 8, 10);
				}
				return Color.FromArgb(228, 234, 242);
			}
		}

		public static Color Stroke
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(39, 44, 52);
				}
				return Color.FromArgb(194, 203, 216);
			}
		}

		public static Color StrokeHi
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(72, 81, 94);
				}
				return Color.FromArgb(137, 150, 169);
			}
		}

		public static Color Fg
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(244, 246, 249);
				}
				return Color.FromArgb(17, 24, 36);
			}
		}

		public static Color Dim
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(163, 170, 181);
				}
				return Color.FromArgb(76, 89, 108);
			}
		}

		public static Color Faint
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(91, 100, 113);
				}
				return Color.FromArgb(124, 139, 159);
			}
		}

		public static Color Green
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(69, 224, 154);
				}
				return Color.FromArgb(16, 150, 92);
			}
		}

		public static Color Danger
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(255, 72, 88);
				}
				return Color.FromArgb(208, 30, 50);
			}
		}

		public static Color Warning
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(255, 190, 75);
				}
				return Color.FromArgb(155, 94, 0);
			}
		}

		public static Color TrackOff
		{
			get
			{
				if (!light)
				{
					return Color.FromArgb(43, 48, 57);
				}
				return Color.FromArgb(200, 206, 215);
			}
		}

		public static Color Accent
		{
			get
			{
				return accent;
			}
		}

		public static Color Accent2
		{
			get
			{
				return accent2;
			}
		}

		public static Color Sel
		{
			get
			{
				return Col.Lerp(Card, accent, 0.2f);
			}
		}

		public static Color DimTint
		{
			get
			{
				return Col.Lerp(Dim, accent, 0.3f);
			}
		}

		public static Color OnAccent
		{
			get
			{
				double measuredValue = 0.299 * (double)(int)toAccent.R + 0.587 * (double)(int)toAccent.G + 0.114 * (double)(int)toAccent.B;
				if (!(measuredValue > 150.0))
				{
					return Color.White;
				}
				return Color.FromArgb(23, 19, 10);
			}
		}

		public static PerformancePreset CurrentMode
		{
			get
			{
				return currentMode;
			}
		}

		public static bool ThemeAnimating
		{
			get
			{
				return themeT < 1f;
			}
		}

		internal static Color TransitionFromAccent
		{
			get
			{
				return fromAccent;
			}
		}

		internal static Color TransitionFromAccent2
		{
			get
			{
				return fromAccent2;
			}
		}

		internal static Color TransitionTargetAccent
		{
			get
			{
				return toAccent;
			}
		}

		internal static Color TransitionTargetAccent2
		{
			get
			{
				return toAccent2;
			}
		}

		internal static float ThemeTransitionProgress
		{
			get
			{
				return 1f - (float)Math.Pow(1f - themeT, 3.0);
			}
		}

		public static int S(int v)
		{
			return Dpi.S(v);
		}

		public static bool HasModeColorOverride(PerformancePreset mode)
		{
			if (mode >= PerformancePreset.Standard && (int)mode < modeOverride.Length)
			{
				return !modeOverride[(int)mode].IsEmpty;
			}
			return false;
		}

		public static Color ModeColor(PerformancePreset mode)
		{
			if (mode >= PerformancePreset.Standard && (int)mode < modeOverride.Length && !modeOverride[(int)mode].IsEmpty)
			{
				return modeOverride[(int)mode];
			}
			return ModeColorBuiltin(mode);
		}

		public static Color ModeColorBuiltin(PerformancePreset mode)
		{
			switch (mode)
			{
			case PerformancePreset.Extreme:
				if (!light)
				{
					return Color.FromArgb(184, 112, 255);
				}
				return Color.FromArgb(121, 55, 197);
			case PerformancePreset.Competitive:
				if (!light)
				{
					return Color.FromArgb(255, 61, 82);
				}
				return Color.FromArgb(222, 36, 58);
			case PerformancePreset.Handheld:
				if (!light)
				{
					return Color.FromArgb(255, 148, 46);
				}
				return Color.FromArgb(176, 92, 12);
			default:
				if (!light)
				{
					return Color.FromArgb(52, 208, 148);
				}
				return Color.FromArgb(10, 136, 92);
			}
		}

		public static Color ModeColor2(PerformancePreset mode)
		{
			if (mode >= PerformancePreset.Standard && (int)mode < modeOverride.Length && !modeOverride[(int)mode].IsEmpty)
			{
				return Col.Lerp(modeOverride[(int)mode], Color.Black, 0.3f);
			}
			switch (mode)
			{
			case PerformancePreset.Extreme:
				if (!light)
				{
					return Color.FromArgb(115, 54, 204);
				}
				return Color.FromArgb(82, 30, 148);
			case PerformancePreset.Competitive:
				if (!light)
				{
					return Color.FromArgb(178, 22, 48);
				}
				return Color.FromArgb(152, 14, 36);
			case PerformancePreset.Handheld:
				if (!light)
				{
					return Color.FromArgb(178, 92, 16);
				}
				return Color.FromArgb(122, 56, 4);
			default:
				if (!light)
				{
					return Color.FromArgb(20, 136, 94);
				}
				return Color.FromArgb(6, 94, 62);
			}
		}

		public static void SetModeColorOverride(PerformancePreset mode, Color color)
		{
			if (mode >= PerformancePreset.Standard && (int)mode < modeOverride.Length)
			{
				modeOverride[(int)mode] = color;
				if (mode == currentMode)
				{
					ReapplyAccent();
				}
			}
		}

		public static void ClearModeColorOverride(PerformancePreset mode)
		{
			if (mode >= PerformancePreset.Standard && (int)mode < modeOverride.Length)
			{
				modeOverride[(int)mode] = Color.Empty;
				if (mode == currentMode)
				{
					ReapplyAccent();
				}
			}
		}

		private static void ReapplyAccent()
		{
			Color color = ModeColor(currentMode);
			Color color2 = ModeColor2(currentMode);
			if (!(toAccent == color) || !(toAccent2 == color2))
			{
				fromAccent = accent;
				fromAccent2 = accent2;
				toAccent = color;
				toAccent2 = color2;
				if (!UiClock.HasFrameListeners)
				{
					accent = color;
					accent2 = color2;
					fromAccent = color;
					fromAccent2 = color2;
					themeT = 1f;
				}
				else
				{
					themeT = 0f;
					UiClock.Wake(36);
				}
			}
		}

		public static void SetLight(bool value)
		{
			if (light != value)
			{
				light = value;
				accent = ModeColor(currentMode);
				accent2 = ModeColor2(currentMode);
				fromAccent = (toAccent = accent);
				fromAccent2 = (toAccent2 = accent2);
				themeT = 1f;
			}
		}

		public static void SetMode(PerformancePreset mode, bool animate)
		{
			Color color = ModeColor(mode);
			Color color2 = ModeColor2(mode);
			if (currentMode != mode || !(toAccent == color) || !(toAccent2 == color2))
			{
				currentMode = mode;
				fromAccent = accent;
				fromAccent2 = accent2;
				toAccent = color;
				toAccent2 = color2;
				themeT = (animate ? 0f : 1f);
				if (!animate)
				{
					accent = color;
					accent2 = color2;
				}
				else
				{
					UiClock.Wake(36);
				}
			}
		}

		public static bool StepTheme()
		{
			if (themeT >= 1f)
			{
				return false;
			}
			themeT = Math.Min(1f, themeT + 0.055f);
			float t = 1f - (float)Math.Pow(1f - themeT, 3.0);
			accent = Col.Lerp(fromAccent, toAccent, t);
			accent2 = Col.Lerp(fromAccent2, toAccent2, t);
			return true;
		}

		public static Font UI(float size, bool bold)
		{
			if (size < 9f)
			{
				size = Math.Min(9f, size + 0.7f);
			}
			int key = ((int)Math.Round(size * 100f) << 1) | (bold ? 1 : 0);
			lock (fontLk)
			{
				Font value;
				if (!fontCache.TryGetValue(key, out value))
				{
					value = SmoothFont("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
					fontCache[key] = value;
				}
				return value;
			}
		}

		public static void DropFontCache()
		{
			lock (fontLk)
			{
				fontCache.Clear();
				monoCache.Clear();
			}
		}

		internal static bool HasCjk(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return false;
			}
			foreach (char c in s)
			{
				if ((c >= '⺀' && c <= '\u9fff') || (c >= '豈' && c <= '\ufaff'))
				{
					return true;
				}
			}
			return false;
		}

		public static Font MonoFor(string text, float size)
		{
			if (!HasCjk(text))
			{
				return Mono(size);
			}
			return UI(size + 0.2f, false);
		}

		public static Font Mono(float size)
		{
			int key = (int)Math.Round(size * 100f);
			lock (fontLk)
			{
				Font value;
				if (!monoCache.TryGetValue(key, out value))
				{
					value = SmoothFont("Consolas", size, FontStyle.Regular);
					monoCache[key] = value;
				}
				return value;
			}
		}

		private static Font SmoothFont(string family, float size, FontStyle style)
		{
			try
			{
				int roundLimit = (int)Math.Max(1.0, Math.Round(size * Dpi.Scale * 96f / 72f));
				LogFont logFont = new LogFont();
				logFont.lfHeight = -roundLimit;
				logFont.lfWeight = (((style & FontStyle.Bold) != 0) ? 700 : 400);
				logFont.lfItalic = (byte)(((style & FontStyle.Italic) != 0) ? 1u : 0u);
				logFont.lfCharSet = 1;
				logFont.lfQuality = 5;
				logFont.lfFaceName = family;
				return Font.FromLogFont(logFont);
			}
			catch
			{
				return new Font(family, Dpi.CrispPoint(size), style, GraphicsUnit.Point);
			}
		}

		public static GraphicsPath Rounded(Rectangle r, int rad)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			if (rad <= 0)
			{
				graphicsPath.AddRectangle(r);
				return graphicsPath;
			}
			int scaledRad = rad * 2;
			if (scaledRad > r.Width)
			{
				scaledRad = r.Width;
			}
			if (scaledRad > r.Height)
			{
				scaledRad = r.Height;
			}
			if (scaledRad <= 0)
			{
				graphicsPath.AddRectangle(r);
				return graphicsPath;
			}
			graphicsPath.AddArc(r.X, r.Y, scaledRad, scaledRad, 180f, 90f);
			graphicsPath.AddArc(r.Right - scaledRad, r.Y, scaledRad, scaledRad, 270f, 90f);
			graphicsPath.AddArc(r.Right - scaledRad, r.Bottom - scaledRad, scaledRad, scaledRad, 0f, 90f);
			graphicsPath.AddArc(r.X, r.Bottom - scaledRad, scaledRad, scaledRad, 90f, 90f);
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		public static GraphicsPath TechPath(Rectangle r, int cut)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			cut = Math.Max(1, Math.Min(cut, Math.Min(r.Width, r.Height) / 3));
			graphicsPath.AddPolygon(new Point[6]
			{
				new Point(r.Left, r.Top),
				new Point(r.Right - cut, r.Top),
				new Point(r.Right, r.Top + cut),
				new Point(r.Right, r.Bottom),
				new Point(r.Left + cut, r.Bottom),
				new Point(r.Left, r.Bottom - cut)
			});
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
		{
			using (GraphicsPath path = Rounded(r, rad))
			{
				using (SolidBrush brush = new SolidBrush(c))
				{
					g.FillPath(brush, path);
				}
			}
		}

		public static void StyleList(ListBox lb)
		{
			StyleList(lb, true);
		}

		public static void StyleList(ListBox lb, bool defaultDraw)
		{
			lb.BackColor = Card;
			lb.ForeColor = Fg;
			lb.BorderStyle = BorderStyle.None;
			lb.DrawMode = DrawMode.OwnerDrawFixed;
			lb.ItemHeight = S(32);
			lb.IntegralHeight = false;
			lb.Font = UI(9.5f, false);
			lb.Tag = -1;
			if (defaultDraw)
			{
				lb.DrawItem += DrawListItem;
			}
			lb.MouseMove += delegate(object s, MouseEventArgs e)
			{
				ListBox listBox2 = (ListBox)s;
				int tagValue = listBox2.IndexFromPoint(e.Location);
				int tag = ((listBox2.Tag is int) ? ((int)listBox2.Tag) : (-1));
				if (tag != tagValue)
				{
					listBox2.Tag = tagValue;
					if (tag >= 0 && tag < listBox2.Items.Count)
					{
						listBox2.Invalidate(listBox2.GetItemRectangle(tag));
					}
					if (tagValue >= 0 && tagValue < listBox2.Items.Count)
					{
						listBox2.Invalidate(listBox2.GetItemRectangle(tagValue));
					}
				}
			};
			lb.MouseLeave += delegate(object s, EventArgs e)
			{
				ListBox listBox = (ListBox)s;
				int tag = ((listBox.Tag is int) ? ((int)listBox.Tag) : (-1));
				if (tag >= 0)
				{
					listBox.Tag = -1;
					if (tag < listBox.Items.Count)
					{
						listBox.Invalidate(listBox.GetItemRectangle(tag));
					}
				}
			};
			Native.Dark(lb);
		}

		public static int HoverIndex(ListBox lb)
		{
			if (lb == null || !(lb.Tag is int))
			{
				return -1;
			}
			return (int)lb.Tag;
		}

		private static void DrawListItem(object s, DrawItemEventArgs e)
		{
			ListBox listBox = (ListBox)s;
			using (SolidBrush brush = new SolidBrush(listBox.BackColor))
			{
				e.Graphics.FillRectangle(brush, e.Bounds);
			}
			if (e.Index >= 0)
			{
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				bool valuesMatch = (e.State & DrawItemState.Selected) != 0;
				int tag = ((listBox.Tag is int) ? ((int)listBox.Tag) : (-1));
				Rectangle r = Rectangle.Inflate(e.Bounds, -S(4), -S(2));
				if (valuesMatch)
				{
					FillRound(e.Graphics, r, S(8), Sel);
				}
				else if (e.Index == tag)
				{
					FillRound(e.Graphics, r, S(8), CardHover);
				}
				TextRenderer.DrawText(e.Graphics, listBox.Items[e.Index].ToString(), listBox.Font, new Rectangle(e.Bounds.X + S(14), e.Bounds.Y, e.Bounds.Width - S(20), e.Bounds.Height), valuesMatch ? Color.White : Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			}
		}

		public static TextBox MakeTextBox(int x, int y, int w)
		{
			TextBox textBox = new TextBox();
			textBox.SetBounds(x, y, w, S(28));
			textBox.BackColor = Card;
			textBox.ForeColor = Fg;
			textBox.BorderStyle = BorderStyle.FixedSingle;
			textBox.Font = UI(9.5f, false);
			return textBox;
		}
	}
}
