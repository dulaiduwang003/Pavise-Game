using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ScanView : Control
	{
		private AuditScanState state;

		private float phase;

		private float progress;

		private Motion shown;

		private Motion fill;

		private string caption = "";

		private string hint = "";

		private bool paintFallback;

		private string paintStage = "background";

		public string Caption
		{
			get
			{
				return caption;
			}
			set
			{
				string text = value ?? "";
				if (!(caption == text))
				{
					caption = text;
					Invalidate();
				}
			}
		}

		public string Hint
		{
			get
			{
				return hint;
			}
			set
			{
				string text = value ?? "";
				if (!(hint == text))
				{
					hint = text;
					Invalidate();
				}
			}
		}

		private float CoreRadius
		{
			get
			{
				float num = (float)Math.Min(base.Width, base.Height) * 0.19f;
				if (!(num < (float)Theme.S(34)))
				{
					return num;
				}
				return Theme.S(34);
			}
		}

		public int RingCenterY
		{
			get
			{
				return (int)((float)base.Height * 0.38f);
			}
		}

		public int ContentBottom
		{
			get
			{
				int num = RingCenterY + (int)CoreRadius + Theme.S(22) + Theme.S(24);
				if (hint.Length > 0)
				{
					num += Theme.S(26);
				}
				return num + Theme.S(30);
			}
		}

		public ScanView()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Bg;
			base.TabStop = false;
			shown.Speed = 0.18f;
			shown.Set(0f);
			fill.Speed = 0.12f;
			fill.Set(0f);
		}

		public void SetIdle(string text, string detail)
		{
			if (paintFallback)
			{
				Theme.DropFontCache();
			}
			paintFallback = false;
			state = AuditScanState.Idle;
			Caption = text;
			Hint = detail;
			progress = 0f;
			fill.Set(0f);
			shown.To(1f);
			Animate(true);
		}

		public void BeginScan(string text)
		{
			if (paintFallback)
			{
				Theme.DropFontCache();
			}
			paintFallback = false;
			state = AuditScanState.Scanning;
			Caption = text;
			Hint = "";
			progress = 0f;
			fill.Set(0f);
			phase = 0f;
			shown.Set(1f);
			Animate(true);
		}

		public void ReportProgress(float value, string text)
		{
			float num = ((float.IsNaN(value) || value < 0f) ? 0f : ((value > 1f) ? 1f : value));
			bool flag = Math.Abs(num - progress) > 0.0005f;
			progress = num;
			fill.To(num);
			if (text != null && text != caption)
			{
				caption = text;
				flag = true;
			}
			if (flag)
			{
				Invalidate();
			}
		}

		public void Stop()
		{
			Animate(false);
		}

		private void Animate(bool on)
		{
			UiClock.Frame -= OnFrame;
			if (on && base.Visible && !paintFallback)
			{
				UiClock.Frame += OnFrame;
				UiClock.Wake();
			}
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			Animate(base.Visible);
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			UiClock.Frame -= OnFrame;
			base.OnHandleDestroyed(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				UiClock.Frame -= OnFrame;
			}
			base.Dispose(disposing);
		}

		private void OnFrame(object sender, EventArgs e)
		{
			if (!base.Visible || paintFallback)
			{
				UiClock.Frame -= OnFrame;
				return;
			}
			bool flag = shown.Step();
			flag |= fill.Step();
			if (state == AuditScanState.Scanning)
			{
				phase += 0.022f;
				if (phase > 1000f)
				{
					phase -= 1000f;
				}
				UiClock.Wake();
				Invalidate();
			}
			else if (flag)
			{
				Invalidate();
			}
			else
			{
				UiClock.Frame -= OnFrame;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (base.Width <= 0 || base.Height <= 0)
			{
				return;
			}
			if (!paintFallback)
			{
				try
				{
					PaintPreservingState(e.Graphics, false);
				}
				catch (ArgumentException error)
				{
					UsePaintFallback(error);
				}
				catch (ExternalException error2)
				{
					UsePaintFallback(error2);
				}
				catch (InvalidOperationException error3)
				{
					UsePaintFallback(error3);
				}
				catch (OutOfMemoryException error4)
				{
					UsePaintFallback(error4);
				}
			}
			if (!paintFallback)
			{
				return;
			}
			try
			{
				PaintPreservingState(e.Graphics, true);
			}
			catch (ArgumentException)
			{
			}
			catch (ExternalException)
			{
			}
			catch (InvalidOperationException)
			{
			}
			catch (OutOfMemoryException)
			{
			}
		}

		private void PaintPreservingState(Graphics g, bool basic)
		{
			GraphicsState gstate = g.Save();
			try
			{
				if (basic)
				{
					PaintBasic(g);
				}
				else
				{
					PaintScan(g);
				}
			}
			finally
			{
				g.Restore(gstate);
			}
		}

		private void UsePaintFallback(Exception error)
		{
			paintFallback = true;
			Animate(false);
			try
			{
				Logger.RepeatedError("audit-scan-paint", string.Concat("Audit scan drawing failed; stage=", paintStage, "; size=", base.Width, "x", base.Height, "; scale=", Dpi.Scale, "; state=", state, "; phase=", phase, "; progress=", progress, "; basic display retained. ", error), -1L);
			}
			catch
			{
			}
		}

		private void PaintBasic(Graphics g)
		{
			using (SolidBrush brush = new SolidBrush(Theme.Bg))
			{
				g.FillRectangle(brush, base.ClientRectangle);
			}
			int num = Math.Min(Theme.S(20), Math.Max(0, (base.Width - 1) / 2));
			int num2 = Math.Max(1, base.Width - num * 2);
			int num3 = Math.Max(1, Theme.S(28));
			int num4 = Math.Max(0, base.Height / 2 - num3 * 2);
			Rectangle bounds = new Rectangle(num, num4, num2, num3);
			TextRenderer.DrawText(g, caption, Font, bounds, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
			bounds.Y += num3;
			if (state != AuditScanState.Scanning)
			{
				TextRenderer.DrawText(g, hint, Font, bounds, Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
				return;
			}
			TextRenderer.DrawText(g, (int)(progress * 100f) + "%", Font, bounds, Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
			int val = Math.Max(1, Theme.S(6));
			int num5 = bounds.Bottom + Theme.S(8);
			if (num5 >= base.Height)
			{
				return;
			}
			Rectangle rect = new Rectangle(num, num5, num2, Math.Min(val, base.Height - num5));
			using (SolidBrush brush2 = new SolidBrush(Theme.Stroke))
			{
				g.FillRectangle(brush2, rect);
			}
			int num6 = (int)((float)num2 * progress);
			if (num6 <= 0)
			{
				return;
			}
			using (SolidBrush brush3 = new SolidBrush(Theme.Accent))
			{
				g.FillRectangle(brush3, new Rectangle(rect.X, rect.Y, num6, rect.Height));
			}
		}

		private void PaintScan(Graphics g)
		{
			paintStage = "background";
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.PaintOnCard(g, this, base.ClientRectangle);
			}
			else
			{
				using (SolidBrush brush = new SolidBrush(Theme.Bg))
				{
					g.FillRectangle(brush, base.ClientRectangle);
				}
			}
			if (base.Width <= 8 || base.Height <= 8)
			{
				return;
			}
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
			g.PixelOffsetMode = PixelOffsetMode.HighQuality;
			bool flag = state == AuditScanState.Scanning;
			paintStage = "panel";
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(14)))
			{
				using (LinearGradientBrush brush2 = new LinearGradientBrush(rectangle, Backdrop.CardFill(this, Theme.Inset), Backdrop.CardFill(this, Theme.Card), LinearGradientMode.ForwardDiagonal))
				{
					g.FillPath(brush2, path);
				}
				GraphicsState gstate = g.Save();
				try
				{
					g.SetClip(path, CombineMode.Intersect);
					paintStage = "scan-field";
					DrawHexField(g);
					DrawSlashes(g);
					if (flag)
					{
						DrawSweep(g);
					}
				}
				finally
				{
					g.Restore(gstate);
				}
				using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, flag ? 255 : 190)))
				{
					g.DrawPath(pen, path);
				}
			}
			float cx = (float)base.Width * 0.5f;
			float num = RingCenterY;
			float coreRadius = CoreRadius;
			paintStage = "brackets";
			DrawBrackets(g, cx, num, coreRadius);
			paintStage = "rings";
			if (flag)
			{
				DrawArcs(g, cx, num, coreRadius);
			}
			paintStage = "core-glow";
			DrawCore(g, cx, num, coreRadius, flag);
			paintStage = "caption";
			DrawText(g, num + coreRadius);
			paintStage = "progress";
			if (flag)
			{
				DrawProgressBar(g, num + coreRadius);
			}
		}

		private void DrawHexField(Graphics g)
		{
			int num = Math.Max(Theme.S(17), 14);
			float num2 = (float)num * 1.5f;
			float num3 = (float)(Math.Sqrt(3.0) * (double)num);
			using (Pen p = new Pen(Col.Alpha(Theme.StrokeHi, 16)))
			{
				int num4 = 0;
				float num5 = -num;
				while (num5 < (float)(base.Width + num))
				{
					float num6 = (((num4 & 1) == 0) ? 0f : (num3 * 0.5f));
					for (float num7 = 0f - num3; num7 < (float)base.Height + num3; num7 += num3)
					{
						DrawHex(g, p, num5, num7 + num6, (float)num * 0.94f, 0f);
					}
					num5 += num2;
					num4++;
				}
			}
		}

		private static void DrawHex(Graphics g, Pen p, float cx, float cy, float r, float rot)
		{
			PointF[] array = new PointF[6];
			for (int i = 0; i < 6; i++)
			{
				double num = (double)rot + (double)i * Math.PI / 3.0;
				array[i] = new PointF(cx + (float)(Math.Cos(num) * (double)r), cy + (float)(Math.Sin(num) * (double)r));
			}
			g.DrawPolygon(p, array);
		}

		private static void FillHex(Graphics g, Brush b, float cx, float cy, float r, float rot)
		{
			PointF[] array = new PointF[6];
			for (int i = 0; i < 6; i++)
			{
				double num = (double)rot + (double)i * Math.PI / 3.0;
				array[i] = new PointF(cx + (float)(Math.Cos(num) * (double)r), cy + (float)(Math.Sin(num) * (double)r));
			}
			g.FillPolygon(b, array);
		}

		private void DrawSlashes(Graphics g)
		{
			int num = Math.Max(Theme.S(26), 20);
			int num2 = Math.Max(Theme.S(7), 6);
			int num3 = Theme.S(16);
			float num4 = ((state == AuditScanState.Scanning) ? ((float)Math.Sin(phase * 2.4f) * (float)Theme.S(3)) : 0f);
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 70), Math.Max(2f, Theme.S(2))))
			{
				for (int i = 0; i < 3; i++)
				{
					float num5 = (float)(i * num2) + num4;
					g.DrawLine(pen, (float)num3 + num5, num3 + num, (float)num3 + num5 + (float)num, num3);
					g.DrawLine(pen, (float)(base.Width - num3) - num5, base.Height - num3 - num, (float)(base.Width - num3) - num5 - (float)num, base.Height - num3);
				}
			}
		}

		private void DrawSweep(Graphics g)
		{
			float num = phase * 0.42f % 1f;
			int num2 = Math.Max(Theme.S(88), 56);
			int num3 = (int)(num * (float)(base.Height + num2)) - num2;
			Rectangle rect = new Rectangle(0, num3, base.Width, num2);
			if (rect.Height <= 0)
			{
				return;
			}
			using (LinearGradientBrush brush = new LinearGradientBrush(rect, Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, 40), LinearGradientMode.Vertical))
			{
				g.FillRectangle(brush, rect);
			}
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 190), Math.Max(1.5f, Theme.S(2))))
			{
				g.DrawLine(pen, 0, rect.Bottom, base.Width, rect.Bottom);
			}
			using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent2, 120), 1f))
			{
				g.DrawLine(pen2, 0, rect.Bottom - 1, base.Width, rect.Bottom - 1);
			}
		}

		private void DrawBrackets(Graphics g, float cx, float cy, float r)
		{
			float num = r * 1.62f;
			float num2 = r * 0.42f;
			float num3 = ((state == AuditScanState.Scanning) ? (1f + (float)Math.Sin(phase * 3.1f) * 0.035f) : 1f);
			num *= num3;
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, 200), Math.Max(1.5f, Theme.S(2))))
			{
				float num4 = cx - num;
				float num5 = cx + num;
				float num6 = cy - num;
				float num7 = cy + num;
				g.DrawLine(pen, num4, num6, num4 + num2, num6);
				g.DrawLine(pen, num4, num6, num4, num6 + num2);
				g.DrawLine(pen, num5, num6, num5 - num2, num6);
				g.DrawLine(pen, num5, num6, num5, num6 + num2);
				g.DrawLine(pen, num4, num7, num4 + num2, num7);
				g.DrawLine(pen, num4, num7, num4, num7 - num2);
				g.DrawLine(pen, num5, num7, num5 - num2, num7);
				g.DrawLine(pen, num5, num7, num5, num7 - num2);
			}
		}

		private void DrawArcs(Graphics g, float cx, float cy, float r)
		{
			DrawTickRing(g, cx, cy, r * 1.34f, phase * 42f, 48, 0.55f, Col.Alpha(Theme.Accent, 150), Math.Max(1.5f, Theme.S(2)));
			DrawTickRing(g, cx, cy, r * 1.14f, (0f - phase) * 78f, 30, 0.34f, Col.Alpha(Theme.Accent2, 120), Math.Max(1f, Theme.S(1)));
			RectangleF rect = new RectangleF(cx - r * 1.34f, cy - r * 1.34f, r * 2.68f, r * 2.68f);
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 230), Math.Max(2.5f, Theme.S(3))))
			{
				pen.StartCap = LineCap.Flat;
				pen.EndCap = LineCap.Flat;
				g.DrawArc(pen, rect, phase * 42f % 360f, 46f);
			}
		}

		private void DrawTickRing(Graphics g, float cx, float cy, float r, float rot, int count, float duty, Color c, float w)
		{
			using (Pen pen = new Pen(c, w))
			{
				float num = 360f / (float)count;
				for (int i = 0; i < count; i++)
				{
					double num2 = (double)(rot + (float)i * num) * Math.PI / 180.0;
					double num3 = (double)(rot + (float)i * num + num * duty) * Math.PI / 180.0;
					g.DrawLine(pen, cx + (float)(Math.Cos(num2) * (double)r), cy + (float)(Math.Sin(num2) * (double)r), cx + (float)(Math.Cos(num3) * (double)r), cy + (float)(Math.Sin(num3) * (double)r));
				}
			}
		}

		private void DrawCore(Graphics g, float cx, float cy, float r, bool scanning)
		{
			float num = (scanning ? (phase * 0.9f) : (phase * 0.25f));
			float num2 = (scanning ? (1f + (float)Math.Sin(phase * 2.2f) * 0.045f) : 1f);
			float num3 = r * 0.72f * num2;
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddEllipse(cx - num3 * 1.9f, cy - num3 * 1.9f, num3 * 3.8f, num3 * 3.8f);
				using (PathGradientBrush pathGradientBrush = new PathGradientBrush(graphicsPath))
				{
					pathGradientBrush.CenterColor = Col.Alpha(Theme.Accent, scanning ? 62 : 26);
					pathGradientBrush.SurroundColors = new Color[1] { Col.Alpha(Theme.Accent, 0) };
					g.FillPath(pathGradientBrush, graphicsPath);
				}
			}
			using (SolidBrush b = new SolidBrush(Col.Alpha(Theme.Accent, scanning ? 34 : 18)))
			{
				FillHex(g, b, cx, cy, num3, num);
			}
			using (Pen p = new Pen(Col.Alpha(Theme.Accent, scanning ? 235 : 150), Math.Max(2f, (float)Theme.S(3) * 0.85f)))
			{
				DrawHex(g, p, cx, cy, num3, num);
			}
			using (Pen p2 = new Pen(Col.Alpha(Theme.Accent2, scanning ? 170 : 90), Math.Max(1f, Theme.S(1))))
			{
				DrawHex(g, p2, cx, cy, num3 * 0.62f, (0f - num) * 1.6f);
			}
			if (!scanning)
			{
				return;
			}
			float num4 = Math.Max(2.5f, Theme.S(3)) * num2;
			using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent2, 220)))
			{
				g.FillEllipse(brush, cx - num4, cy - num4, num4 * 2f, num4 * 2f);
			}
		}

		private void DrawProgressBar(Graphics g, float top)
		{
			int num = (int)((float)base.Width * 0.52f);
			if (num < Theme.S(140))
			{
				num = Math.Min(base.Width - Theme.S(40), Theme.S(140));
			}
			if (num <= 0)
			{
				return;
			}
			int num2 = Math.Max(Theme.S(8), 7);
			int num3 = (base.Width - num) / 2;
			int num4 = (int)top + Theme.S(22) + Theme.S(24) + ((hint.Length > 0) ? Theme.S(26) : 0);
			if (num4 + num2 > base.Height)
			{
				return;
			}
			Rectangle r = new Rectangle(num3, num4, num, num2);
			using (GraphicsPath path = Skew(r, num2 / 2))
			{
				using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Stroke, 150)))
				{
					g.FillPath(brush, path);
				}
				GraphicsState gstate = g.Save();
				try
				{
					g.SetClip(path, CombineMode.Intersect);
					int num5 = (int)((float)num * fill.Value);
					if (num5 > 0)
					{
						Rectangle rect = new Rectangle(num3, num4, num5, num2);
						using (LinearGradientBrush brush2 = new LinearGradientBrush(new Rectangle(num3, num4, Math.Max(1, num5), num2), Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
						{
							g.FillRectangle(brush2, rect);
						}
						using (Pen pen = new Pen(Col.Alpha(Theme.Inset, 200), Math.Max(1f, Theme.S(1))))
						{
							for (int i = num3 + Theme.S(10); i < num3 + num5; i += Theme.S(10))
							{
								g.DrawLine(pen, i, num4, i - num2, num4 + num2);
							}
						}
					}
				}
				finally
				{
					g.Restore(gstate);
				}
				using (Pen pen2 = new Pen(Col.Alpha(Theme.StrokeHi, 210), 1f))
				{
					g.DrawPath(pen2, path);
				}
			}
		}

		private static GraphicsPath Skew(Rectangle r, int cut)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			graphicsPath.AddPolygon(new Point[4]
			{
				new Point(r.Left + cut, r.Top),
				new Point(r.Right, r.Top),
				new Point(r.Right - cut, r.Bottom),
				new Point(r.Left, r.Bottom)
			});
			return graphicsPath;
		}

		private void DrawText(Graphics g, float ringBottom)
		{
			int num = (int)ringBottom + Theme.S(22);
			if (base.Width <= Theme.S(32))
			{
				return;
			}
			Rectangle rectangle = new Rectangle(Theme.S(16), num, base.Width - Theme.S(32), Theme.S(24));
			Font font = Theme.UI(11f, true);
			using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Fg, (int)(255f * Math.Max(0.25f, shown.Value)))))
			{
				StringFormat stringFormat = new StringFormat();
				stringFormat.Alignment = StringAlignment.Center;
				stringFormat.LineAlignment = StringAlignment.Center;
				stringFormat.Trimming = StringTrimming.EllipsisCharacter;
				stringFormat.FormatFlags = StringFormatFlags.NoWrap;
				using (StringFormat format = stringFormat)
				{
					g.DrawString(caption, font, brush, rectangle, format);
				}
			}
			if (hint.Length == 0 || base.Width <= Theme.S(48))
			{
				return;
			}
			Rectangle rectangle2 = new Rectangle(Theme.S(24), num + Theme.S(24), base.Width - Theme.S(48), Theme.S(24));
			Font font2 = Theme.UI(8.6f, false);
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(Theme.Dim, (int)(255f * Math.Max(0.25f, shown.Value)))))
			{
				StringFormat stringFormat2 = new StringFormat();
				stringFormat2.Alignment = StringAlignment.Center;
				stringFormat2.LineAlignment = StringAlignment.Center;
				stringFormat2.Trimming = StringTrimming.EllipsisCharacter;
				using (StringFormat format2 = stringFormat2)
				{
					g.DrawString(hint, font2, brush2, rectangle2, format2);
				}
			}
		}
	}
}
