using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class DonateDialog : Form
	{
		private const int DlgW = 420;

		private const int PadX = 26;

		private const int TagTop = 17;

		private const int TagHeight = 16;

		private const int TagWidth = 74;

		private const int TitleTop = 43;

		private const int QrSize = 236;

		private const int QrPad = 12;

		private const int BtnH = 34;

		private const int BottomPad = 22;

		private Bitmap image;

		private string status;

		private float sweep;

		private int bodyTop;

		private int bodyH;

		private int qrTop;

		private int hintTop;

		private static Font TitleFont
		{
			get
			{
				return Theme.UI(12.5f, true);
			}
		}

		private static Font BodyFont
		{
			get
			{
				return Theme.UI(9.2f, false);
			}
		}

		public DonateDialog(Bitmap qr, bool loading)
		{
			image = qr;
			status = ((qr != null) ? null : Lang.T(loading ? "donate.loading" : "donate.failed"));
			Text = Lang.T("donate.entry");
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.MaximizeBox = false;
			base.MinimizeBox = false;
			base.ShowInTaskbar = false;
			BackColor = Theme.Bg;
			ForeColor = Theme.Fg;
			Font = Theme.UI(9.5f, false);
			DoubleBuffered = true;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.KeyPreview = true;
			base.Paint += PaintChrome;
			base.MouseDown += DragMove;
			int scaledDimension = Theme.S(420);
			int remainingScaledDimension = scaledDimension - Theme.S(26) * 2;
			bodyTop = Theme.S(43) + MeasureLine(TitleFont) + Theme.S(12);
			bodyH = MeasureBody(remainingScaledDimension);
			qrTop = bodyTop + bodyH + Theme.S(16);
			hintTop = qrTop + Theme.S(260) + Theme.S(10);
			int sOffset = hintTop + Theme.S(22) + Theme.S(18) + Theme.S(34) + Theme.S(22);
			base.ClientSize = new Size(scaledDimension, sOffset);
			Label close = new Label();
			close.Text = "✕";
			close.ForeColor = Theme.Faint;
			close.BackColor = Color.Transparent;
			close.Font = Theme.UI(10f, false);
			close.TextAlign = ContentAlignment.MiddleCenter;
			close.Cursor = Cursors.Hand;
			close.SetBounds(scaledDimension - Theme.S(42), Theme.S(12), Theme.S(26), Theme.S(26));
			close.MouseEnter += delegate
			{
				close.ForeColor = Theme.Accent;
			};
			close.MouseLeave += delegate
			{
				close.ForeColor = Theme.Faint;
			};
			close.Click += delegate
			{
				Close();
			};
			base.Controls.Add(close);
			PillButton pillButton = new PillButton(Lang.T("dlg.ok"), BtnKind.Primary);
			pillButton.Size = new Size(Theme.S(108), Theme.S(34));
			pillButton.Location = new Point(scaledDimension - Theme.S(26) - pillButton.Width, sOffset - Theme.S(22) - Theme.S(34));
			pillButton.Click += delegate
			{
				Close();
			};
			base.Controls.Add(pillButton);
			base.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Return)
				{
					Close();
				}
			};
			Timer timer = new Timer();
			timer.Interval = 33;
			timer.Tick += delegate
			{
				sweep += 0.012f;
				if (sweep > 1f)
				{
					sweep -= 1f;
				}
				Invalidate(new Rectangle(0, 0, base.Width, Theme.S(4)));
			};
			timer.Start();
			base.Disposed += delegate
			{
				timer.Stop();
				timer.Dispose();
				try
				{
					if (image != null)
					{
						image.Dispose();
					}
				}
				catch
				{
				}
			};
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			Fx.EnterForm(this);
		}

		public void SetImage(Bitmap qr)
		{
			if (qr == null)
			{
				MarkFailed();
				return;
			}
			Bitmap bitmap = image;
			image = qr;
			status = null;
			try
			{
				if (bitmap != null)
				{
					bitmap.Dispose();
				}
			}
			catch
			{
			}
			Invalidate();
		}

		public void MarkFailed()
		{
			if (image == null)
			{
				status = Lang.T("donate.failed");
				Invalidate();
			}
		}

		private int MeasureLine(Font font)
		{
			using (Graphics dc = CreateGraphics())
			{
				return TextRenderer.MeasureText(dc, "字Ay", font).Height;
			}
		}

		private int MeasureBody(int width)
		{
			using (Graphics dc = CreateGraphics())
			{
				return TextRenderer.MeasureText(dc, Lang.T("donate.body"), BodyFont, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
			}
		}

		private void PaintChrome(object sender, PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
			Color accent = Theme.Accent;
			int scaledDimension = Theme.S(26);
			int height = Theme.S(4);
			using (SolidBrush brush = new SolidBrush(accent))
			{
				graphics.FillRectangle(brush, 0, 0, base.Width, height);
			}
			using (LinearGradientBrush brush2 = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, base.Width), height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(150, 255, 255, 255), LinearGradientMode.Horizontal))
			{
				int scaledDimension2 = Theme.S(150);
				Rectangle rect = new Rectangle((int)(sweep * (float)(base.Width + scaledDimension2)) - scaledDimension2, 0, scaledDimension2, height);
				Region clip = graphics.Clip;
				graphics.SetClip(new Rectangle(0, 0, base.Width, height), CombineMode.Replace);
				graphics.FillRectangle(brush2, rect);
				graphics.Clip = clip;
			}
			int scaledDimension3 = Theme.S(9);
			string s = Lang.T("donate.entry");
			Font font = Theme.UI(7.2f, true);
			int sLimit = Math.Max(Theme.S(74), TextRenderer.MeasureText(graphics, s, font, Size.Empty, TextFormatFlags.NoPadding).Width + scaledDimension3 * 3);
			Point[] points = new Point[4]
			{
				new Point(scaledDimension + scaledDimension3, Theme.S(17)),
				new Point(scaledDimension + sLimit + scaledDimension3, Theme.S(17)),
				new Point(scaledDimension + sLimit - scaledDimension3, Theme.S(33)),
				new Point(scaledDimension - scaledDimension3, Theme.S(33))
			};
			using (SolidBrush brush3 = new SolidBrush(Color.FromArgb(38, accent)))
			{
				graphics.FillPolygon(brush3, points);
			}
			using (Pen pen = new Pen(Color.FromArgb(120, accent), Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawPolygon(pen, points);
			}
			using (SolidBrush brush4 = new SolidBrush(accent))
			{
				StringFormat stringFormat = new StringFormat();
				stringFormat.Alignment = StringAlignment.Center;
				stringFormat.LineAlignment = StringAlignment.Center;
				using (StringFormat format = stringFormat)
				{
					graphics.DrawString(s, font, brush4, new RectangleF(scaledDimension, Theme.S(17), sLimit, Theme.S(16)), format);
				}
			}
			int remainingWidth = base.Width - scaledDimension * 2;
			TextRenderer.DrawText(graphics, Lang.T("donate.title"), TitleFont, new Rectangle(scaledDimension, Theme.S(43), remainingWidth, bodyTop - Theme.S(43)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, Lang.T("donate.body"), BodyFont, new Rectangle(scaledDimension, bodyTop, remainingWidth, bodyH), Theme.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			int scaledDimension4 = Theme.S(260);
			Rectangle r = new Rectangle((base.Width - scaledDimension4) / 2, qrTop, scaledDimension4, scaledDimension4);
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(10)))
			{
				using (SolidBrush brush5 = new SolidBrush(Color.White))
				{
					graphics.FillPath(brush5, path);
				}
				using (Pen pen2 = new Pen(Col.Alpha(accent, 110), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawPath(pen2, path);
				}
			}
			Rectangle rectangle = new Rectangle(r.Left + Theme.S(12), r.Top + Theme.S(12), Theme.S(236), Theme.S(236));
			if (image != null)
			{
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.DrawImage(image, rectangle);
			}
			else if (!string.IsNullOrEmpty(status))
			{
				TextRenderer.DrawText(graphics, status, Theme.UI(9f, false), rectangle, Color.FromArgb(120, 120, 120), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
			}
			TextRenderer.DrawText(graphics, Lang.T("donate.hint"), Theme.UI(8.5f, false), new Rectangle(scaledDimension, hintTop, remainingWidth, Theme.S(22)), Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			using (Pen pen3 = new Pen(Theme.StrokeHi))
			{
				graphics.DrawRectangle(pen3, 0, 0, base.Width - 1, base.Height - 1);
			}
		}

		private void DragMove(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Native.ReleaseCapture();
				Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
			}
		}
	}
}
