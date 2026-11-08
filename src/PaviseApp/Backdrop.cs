using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class Backdrop
	{
		private const string DimKey = "BackdropDim";

		private const string StoreName = "backdrop.img";

		private const int CardAlphaDark = 208;

		private const int CardAlphaLight = 100;

		private const int NavAlphaLight = 80;

		private const int LightSoftenDivisor = 2;

		private const int SourceMaxEdge = 2560;

		private static readonly int[] DimDark = new int[3] { 120, 170, 214 };

		private static readonly float[] LightFloorMix = new float[3] { 0.9f, 0.94f, 0.97f };

		private static Bitmap source;

		private static Bitmap fitted;

		private static Size fittedFor;

		private static int fittedDim = -1;

		private static bool fittedLight;

		private static int dim = 1;

		public static bool Active
		{
			get
			{
				return source != null;
			}
		}

		public static string StorePath
		{
			get
			{
				return Path.Combine(Paths.Data, "backdrop.img");
			}
		}

		public static int Dim
		{
			get
			{
				return dim;
			}
			set
			{
				int valueSnapshot = ((value >= 0) ? ((value > 2) ? 2 : value) : 0);
				if (valueSnapshot != dim)
				{
					dim = valueSnapshot;
					Settings.SaveStr("BackdropDim", valueSnapshot.ToString(CultureInfo.InvariantCulture));
					DropFitted();
				}
			}
		}

		public static bool AppliesTo(Control control)
		{
			if (!Active)
			{
				return false;
			}
			for (Control control2 = control; control2 != null; control2 = control2.Parent)
			{
				RoundPanel roundPanel = control2 as RoundPanel;
				if (roundPanel != null && !roundPanel.UseBackdrop)
				{
					return false;
				}
				if (control2 is Form)
				{
					return control2 is PanelForm;
				}
			}
			return false;
		}

		public static Color NavFill(Control control, Color baseFill)
		{
			if (!AppliesTo(control))
			{
				return baseFill;
			}
			return Col.Alpha(baseFill, Theme.LightMode ? 80 : 198);
		}

		public static Color CardFill(Control control, Color baseFill)
		{
			if (!AppliesTo(control))
			{
				return baseFill;
			}
			return Col.Alpha(baseFill, Theme.LightMode ? 100 : 208);
		}

		public static void Init()
		{
			int result;
			if (int.TryParse(Settings.LoadStr("BackdropDim", "1"), out result))
			{
				dim = ((result >= 0) ? ((result > 2) ? 2 : result) : 0);
			}
			LoadFromStore();
		}

		public static bool Choose(IWin32Window owner)
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog())
			{
				openFileDialog.Title = Lang.T("backdrop.pick");
				openFileDialog.Filter = Lang.T("backdrop.filter") + "|*.jpg;*.jpeg;*.png;*.bmp";
				openFileDialog.CheckFileExists = true;
				if (openFileDialog.ShowDialog(owner) != DialogResult.OK)
				{
					return false;
				}
				try
				{
					using (Bitmap bitmap = Read(openFileDialog.FileName))
					{
						if (bitmap == null)
						{
							return false;
						}
						Directory.CreateDirectory(Paths.Data);
						File.Copy(openFileDialog.FileName, StorePath, true);
					}
				}
				catch (Exception error)
				{
					Logger.LogFailure(Lang.T("log.backdrop.1"), error);
					return false;
				}
			}
			LoadFromStore();
			return Active;
		}

		public static void Clear()
		{
			Drop();
			try
			{
				if (File.Exists(StorePath))
				{
					File.Delete(StorePath);
				}
			}
			catch (Exception error)
			{
				Logger.LogFailure(Lang.T("log.backdrop.2"), error);
			}
		}

		public static void Paint(Graphics g, Control c, Rectangle client)
		{
			if (!AppliesTo(c) || client.Width <= 0 || client.Height <= 0)
			{
				return;
			}
			Bitmap bitmap = Fit(c);
			if (bitmap == null)
			{
				return;
			}
			int paintValue = 0;
			int paintValue2 = 0;
			Control control = c;
			while (control != null && !(control is Form))
			{
				paintValue += control.Left;
				paintValue2 += control.Top;
				control = control.Parent;
			}
			Rectangle srcRect = new Rectangle(client.X + paintValue, client.Y + paintValue2, client.Width, client.Height);
			if (srcRect.X < bitmap.Width && srcRect.Y < bitmap.Height)
			{
				if (srcRect.X < 0)
				{
					client.X -= srcRect.X;
					client.Width += srcRect.X;
					srcRect.Width += srcRect.X;
					srcRect.X = 0;
				}
				if (srcRect.Y < 0)
				{
					client.Y -= srcRect.Y;
					client.Height += srcRect.Y;
					srcRect.Height += srcRect.Y;
					srcRect.Y = 0;
				}
				if (srcRect.Right > bitmap.Width)
				{
					client.Width -= srcRect.Right - bitmap.Width;
					srcRect.Width = bitmap.Width - srcRect.X;
				}
				if (srcRect.Bottom > bitmap.Height)
				{
					client.Height -= srcRect.Bottom - bitmap.Height;
					srcRect.Height = bitmap.Height - srcRect.Y;
				}
				if (srcRect.Width > 0 && srcRect.Height > 0)
				{
					InterpolationMode interpolationMode = g.InterpolationMode;
					PixelOffsetMode pixelOffsetMode = g.PixelOffsetMode;
					g.InterpolationMode = InterpolationMode.NearestNeighbor;
					g.PixelOffsetMode = PixelOffsetMode.Half;
					g.DrawImage(bitmap, client, srcRect, GraphicsUnit.Pixel);
					g.InterpolationMode = interpolationMode;
					g.PixelOffsetMode = pixelOffsetMode;
				}
			}
		}

		public static void PaintPreview(Graphics g, Rectangle target)
		{
			if (!Active || g == null || target.Width <= 0 || target.Height <= 0)
			{
				return;
			}
			GraphicsState gstate = g.Save();
			try
			{
				g.SetClip(target, CombineMode.Intersect);
				InterpolationMode interpolationMode = g.InterpolationMode;
				PixelOffsetMode pixelOffsetMode = g.PixelOffsetMode;
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				DrawCover(g, target, Theme.LightMode);
				if (!Theme.LightMode)
				{
					using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Bg, DimDark[dim])))
					{
						g.FillRectangle(brush, target);
					}
				}
				g.InterpolationMode = interpolationMode;
				g.PixelOffsetMode = pixelOffsetMode;
			}
			finally
			{
				g.Restore(gstate);
			}
		}

		private static void LoadFromStore()
		{
			Drop();
			try
			{
				if (File.Exists(StorePath))
				{
					source = Read(StorePath);
				}
			}
			catch (Exception error)
			{
				Logger.LogFailure(Lang.T("log.backdrop.3"), error);
			}
		}

		private static Bitmap Read(string path)
		{
			byte[] buffer = File.ReadAllBytes(path);
			using (MemoryStream stream = new MemoryStream(buffer))
			{
				using (Image image = Image.FromStream(stream, false, true))
				{
					int heightLimit = Math.Max(image.Width, image.Height);
					if (heightLimit <= 2560)
					{
						return new Bitmap(image);
					}
					double heightLimitRatio = 2560.0 / (double)heightLimit;
					int width = Math.Max(1, (int)Math.Round((double)image.Width * heightLimitRatio));
					int height = Math.Max(1, (int)Math.Round((double)image.Height * heightLimitRatio));
					Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
					using (Graphics graphics = Graphics.FromImage(bitmap))
					{
						graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
						graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
						graphics.DrawImage(image, new Rectangle(0, 0, width, height));
					}
					return bitmap;
				}
			}
		}

		private static Bitmap Fit(Control c)
		{
			Form form = c.FindForm();
			Size size = ((form != null) ? form.ClientSize : c.ClientSize);
			if (size.Width <= 0 || size.Height <= 0)
			{
				return null;
			}
			if (fitted != null && fittedFor == size && fittedDim == dim && fittedLight == Theme.LightMode)
			{
				return fitted;
			}
			DropFitted();
			Bitmap bitmap = null;
			try
			{
				bitmap = new Bitmap(size.Width, size.Height);
				using (Graphics graphics = Graphics.FromImage(bitmap))
				{
					graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
					graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
					DrawCover(graphics, new Rectangle(0, 0, size.Width, size.Height), Theme.LightMode);
				}
				if (Theme.LightMode)
				{
					int width = Math.Max(1, size.Width / 2);
					int height = Math.Max(1, size.Height / 2);
					using (Bitmap image = new Bitmap(width, height))
					{
						using (Graphics graphics2 = Graphics.FromImage(image))
						{
							graphics2.InterpolationMode = InterpolationMode.HighQualityBicubic;
							graphics2.PixelOffsetMode = PixelOffsetMode.HighQuality;
							graphics2.DrawImage(bitmap, new Rectangle(0, 0, width, height), new Rectangle(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel);
						}
						using (Graphics graphics3 = Graphics.FromImage(bitmap))
						{
							graphics3.InterpolationMode = InterpolationMode.HighQualityBicubic;
							graphics3.PixelOffsetMode = PixelOffsetMode.HighQuality;
							graphics3.DrawImage(image, new Rectangle(0, 0, size.Width, size.Height), new Rectangle(0, 0, width, height), GraphicsUnit.Pixel);
						}
					}
				}
				if (!Theme.LightMode)
				{
					using (Graphics graphics4 = Graphics.FromImage(bitmap))
					{
						using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Bg, DimDark[dim])))
						{
							graphics4.FillRectangle(brush, 0, 0, size.Width, size.Height);
						}
					}
				}
				fitted = bitmap;
				bitmap = null;
				fittedFor = size;
				fittedDim = dim;
				fittedLight = Theme.LightMode;
			}
			catch (Exception error)
			{
				Logger.LogFailure(Lang.T("log.backdrop.4"), error);
				if (bitmap != null)
				{
					bitmap.Dispose();
				}
				DropFitted();
			}
			return fitted;
		}

		private static void DrawCover(Graphics g, Rectangle target, bool lightTone)
		{
			float lowerBound = Math.Max((float)target.Width / (float)source.Width, (float)target.Height / (float)source.Height);
			int roundedValue = (int)Math.Ceiling((float)source.Width * lowerBound);
			int roundedValue2 = (int)Math.Ceiling((float)source.Height * lowerBound);
			Rectangle rectangle = new Rectangle(target.X + (target.Width - roundedValue) / 2, target.Y + (target.Height - roundedValue2) / 2, roundedValue, roundedValue2);
			if (!lightTone)
			{
				g.DrawImage(source, rectangle);
				return;
			}
			Color color = Col.Lerp(Color.Black, Theme.Bg, LightFloorMix[dim]);
			float rRatio = (float)(int)color.R / 255f;
			float gRatio = (float)(int)color.G / 255f;
			float bRatio = (float)(int)color.B / 255f;
			using (ImageAttributes imageAttributes = new ImageAttributes())
			{
				imageAttributes.SetColorMatrix(new ColorMatrix(new float[5][]
				{
					new float[5]
					{
						1f - rRatio,
						0f,
						0f,
						0f,
						0f
					},
					new float[5]
					{
						0f,
						1f - gRatio,
						0f,
						0f,
						0f
					},
					new float[5]
					{
						0f,
						0f,
						1f - bRatio,
						0f,
						0f
					},
					new float[5] { 0f, 0f, 0f, 1f, 0f },
					new float[5] { rRatio, gRatio, bRatio, 0f, 1f }
				}), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
				g.DrawImage(source, rectangle, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, imageAttributes);
			}
		}

		private static void DropFitted()
		{
			if (fitted != null)
			{
				fitted.Dispose();
				fitted = null;
			}
			fittedFor = Size.Empty;
			fittedDim = -1;
		}

		private static void Drop()
		{
			DropFitted();
			if (source != null)
			{
				source.Dispose();
				source = null;
			}
		}

		public static void PaintOnCard(Graphics g, Control c, Rectangle rect)
		{
			if (!AppliesTo(c))
			{
				return;
			}
			Paint(g, c, rect);
			for (Control parent = c.Parent; parent != null && !(parent is Form); parent = parent.Parent)
			{
				RoundPanel panel = parent as RoundPanel;
				if (panel != null || object.Equals(parent.Tag, "backdrop-card"))
				{
					using (SolidBrush brush = new SolidBrush(CardFill(c, panel != null ? panel.Fill : Theme.Card))) g.FillRectangle(brush, rect);
					return;
				}
			}
		}
	}
}
