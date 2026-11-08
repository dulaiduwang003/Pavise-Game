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
				string valueOffset = value ?? "";
				if (!(caption == valueOffset))
				{
					caption = valueOffset;
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
				string valueOffset = value ?? "";
				if (!(hint == valueOffset))
				{
					hint = valueOffset;
					Invalidate();
				}
			}
		}

		private float CoreRadius
		{
			get
			{
				float scaledMin = (float)Math.Min(base.Width, base.Height) * 0.19f;
				if (!(scaledMin < (float)Theme.S(34)))
				{
					return scaledMin;
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
				int sOffset = RingCenterY + (int)CoreRadius + Theme.S(22) + Theme.S(24);
				if (hint.Length > 0)
				{
					sOffset += Theme.S(26);
				}
				return sOffset + Theme.S(30);
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
			float valueSnapshot = ((float.IsNaN(value) || value < 0f) ? 0f : ((value > 1f) ? 1f : value));
			bool matchesAbs = Math.Abs(valueSnapshot - progress) > 0.0005f;
			progress = valueSnapshot;
			fill.To(valueSnapshot);
			if (text != null && text != caption)
			{
				caption = text;
				matchesAbs = true;
			}
			if (matchesAbs)
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
			bool stepSucceeded = shown.Step();
			stepSucceeded |= fill.Step();
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
			else if (stepSucceeded)
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
			int maxLimit = Math.Min(Theme.S(20), Math.Max(0, (base.Width - 1) / 2));
			int lowerBound = Math.Max(1, base.Width - maxLimit * 2);
			int yValue = Math.Max(1, Theme.S(28));
			int lowerBound2 = Math.Max(0, base.Height / 2 - yValue * 2);
			Rectangle bounds = new Rectangle(maxLimit, lowerBound2, lowerBound, yValue);
			TextRenderer.DrawText(g, caption, Font, bounds, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
			bounds.Y += yValue;
			if (state != AuditScanState.Scanning)
			{
				TextRenderer.DrawText(g, hint, Font, bounds, Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
				return;
			}
			TextRenderer.DrawText(g, (int)(progress * 100f) + "%", Font, bounds, Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping);
			int val = Math.Max(1, Theme.S(6));
			int bottomOffset = bounds.Bottom + Theme.S(8);
			if (bottomOffset >= base.Height)
			{
				return;
			}
			Rectangle rect = new Rectangle(maxLimit, bottomOffset, lowerBound, Math.Min(val, base.Height - bottomOffset));
			using (SolidBrush brush2 = new SolidBrush(Theme.Stroke))
			{
				g.FillRectangle(brush2, rect);
			}
			int scaledLowerBound = (int)((float)lowerBound * progress);
			if (scaledLowerBound <= 0)
			{
				return;
			}
			using (SolidBrush brush3 = new SolidBrush(Theme.Accent))
			{
				g.FillRectangle(brush3, new Rectangle(rect.X, rect.Y, scaledLowerBound, rect.Height));
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
			bool matchesState = state == AuditScanState.Scanning;
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
					if (matchesState)
					{
						DrawSweep(g);
					}
				}
				finally
				{
					g.Restore(gstate);
				}
				using (Pen pen = new Pen(Col.Alpha(Theme.Stroke, matchesState ? 255 : 190)))
				{
					g.DrawPath(pen, path);
				}
			}
			float cx = (float)base.Width * 0.5f;
			float ringCenterYSnapshot = RingCenterY;
			float coreRadius = CoreRadius;
			paintStage = "brackets";
			DrawBrackets(g, cx, ringCenterYSnapshot, coreRadius);
			paintStage = "rings";
			if (matchesState)
			{
				DrawArcs(g, cx, ringCenterYSnapshot, coreRadius);
			}
			paintStage = "core-glow";
			DrawCore(g, cx, ringCenterYSnapshot, coreRadius, matchesState);
			paintStage = "caption";
			DrawText(g, ringCenterYSnapshot + coreRadius);
			paintStage = "progress";
			if (matchesState)
			{
				DrawProgressBar(g, ringCenterYSnapshot + coreRadius);
			}
		}

		private void DrawHexField(Graphics g)
		{
			int sLimit = Math.Max(Theme.S(17), 14);
			float scaledSLimit = (float)sLimit * 1.5f;
			float scaledSqrt = (float)(Math.Sqrt(3.0) * (double)sLimit);
			using (Pen p = new Pen(Col.Alpha(Theme.StrokeHi, 16)))
			{
				int processedCount = 0;
				float measuredValue = -sLimit;
				while (measuredValue < (float)(base.Width + sLimit))
				{
					float scaledScaledSqrt = (((processedCount & 1) == 0) ? 0f : (scaledSqrt * 0.5f));
					for (float iterationIndex = 0f - scaledSqrt; iterationIndex < (float)base.Height + scaledSqrt; iterationIndex += scaledSqrt)
					{
						DrawHex(g, p, measuredValue, iterationIndex + scaledScaledSqrt, (float)sLimit * 0.94f, 0f);
					}
					measuredValue += scaledSLimit;
					processedCount++;
				}
			}
		}

		private static void DrawHex(Graphics g, Pen p, float cx, float cy, float r, float rot)
		{
			PointF[] pointFItems = new PointF[6];
			for (int i = 0; i < 6; i++)
			{
				double rotOffset = (double)rot + (double)i * Math.PI / 3.0;
				pointFItems[i] = new PointF(cx + (float)(Math.Cos(rotOffset) * (double)r), cy + (float)(Math.Sin(rotOffset) * (double)r));
			}
			g.DrawPolygon(p, pointFItems);
		}

		private static void FillHex(Graphics g, Brush b, float cx, float cy, float r, float rot)
		{
			PointF[] pointFItems = new PointF[6];
			for (int i = 0; i < 6; i++)
			{
				double rotOffset = (double)rot + (double)i * Math.PI / 3.0;
				pointFItems[i] = new PointF(cx + (float)(Math.Cos(rotOffset) * (double)r), cy + (float)(Math.Sin(rotOffset) * (double)r));
			}
			g.FillPolygon(b, pointFItems);
		}

		private void DrawSlashes(Graphics g)
		{
			int sLimit = Math.Max(Theme.S(26), 20);
			int sLimit2 = Math.Max(Theme.S(7), 6);
			int height = Theme.S(16);
			float scaledSin = ((state == AuditScanState.Scanning) ? ((float)Math.Sin(phase * 2.4f) * (float)Theme.S(3)) : 0f);
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 70), Math.Max(2f, Theme.S(2))))
			{
				for (int i = 0; i < 3; i++)
				{
					float scaledSinOffset = (float)(i * sLimit2) + scaledSin;
					g.DrawLine(pen, (float)height + scaledSinOffset, height + sLimit, (float)height + scaledSinOffset + (float)sLimit, height);
					g.DrawLine(pen, (float)(base.Width - height) - scaledSinOffset, base.Height - height - sLimit, (float)(base.Width - height) - scaledSinOffset - (float)sLimit, base.Height - height);
				}
			}
		}

		private void DrawSweep(Graphics g)
		{
			float measuredValue = phase * 0.42f % 1f;
			int sLimit = Math.Max(Theme.S(88), 56);
			int remainingSLimit = (int)(measuredValue * (float)(base.Height + sLimit)) - sLimit;
			Rectangle rect = new Rectangle(0, remainingSLimit, base.Width, sLimit);
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
			float scaledR = r * 1.62f;
			float scaledR2 = r * 0.42f;
			float measuredValue = ((state == AuditScanState.Scanning) ? (1f + (float)Math.Sin(phase * 3.1f) * 0.035f) : 1f);
			scaledR *= measuredValue;
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, 200), Math.Max(1.5f, Theme.S(2))))
			{
				float left = cx - scaledR;
				float left2 = cx + scaledR;
				float top = cy - scaledR;
				float top2 = cy + scaledR;
				g.DrawLine(pen, left, top, left + scaledR2, top);
				g.DrawLine(pen, left, top, left, top + scaledR2);
				g.DrawLine(pen, left2, top, left2 - scaledR2, top);
				g.DrawLine(pen, left2, top, left2, top + scaledR2);
				g.DrawLine(pen, left, top2, left + scaledR2, top2);
				g.DrawLine(pen, left, top2, left, top2 - scaledR2);
				g.DrawLine(pen, left2, top2, left2 - scaledR2, top2);
				g.DrawLine(pen, left2, top2, left2, top2 - scaledR2);
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
				float countRatio = 360f / (float)count;
				for (int i = 0; i < count; i++)
				{
					double ratio = (double)(rot + (float)i * countRatio) * Math.PI / 180.0;
					double ratio2 = (double)(rot + (float)i * countRatio + countRatio * duty) * Math.PI / 180.0;
					g.DrawLine(pen, cx + (float)(Math.Cos(ratio) * (double)r), cy + (float)(Math.Sin(ratio) * (double)r), cx + (float)(Math.Cos(ratio2) * (double)r), cy + (float)(Math.Sin(ratio2) * (double)r));
				}
			}
		}

		private void DrawCore(Graphics g, float cx, float cy, float r, bool scanning)
		{
			float scaledPhase = (scanning ? (phase * 0.9f) : (phase * 0.25f));
			float measuredValue = (scanning ? (1f + (float)Math.Sin(phase * 2.2f) * 0.045f) : 1f);
			float scaledMeasuredValue = r * 0.72f * measuredValue;
			using (GraphicsPath graphicsPath = new GraphicsPath())
			{
				graphicsPath.AddEllipse(cx - scaledMeasuredValue * 1.9f, cy - scaledMeasuredValue * 1.9f, scaledMeasuredValue * 3.8f, scaledMeasuredValue * 3.8f);
				using (PathGradientBrush pathGradientBrush = new PathGradientBrush(graphicsPath))
				{
					pathGradientBrush.CenterColor = Col.Alpha(Theme.Accent, scanning ? 62 : 26);
					pathGradientBrush.SurroundColors = new Color[1] { Col.Alpha(Theme.Accent, 0) };
					g.FillPath(pathGradientBrush, graphicsPath);
				}
			}
			using (SolidBrush b = new SolidBrush(Col.Alpha(Theme.Accent, scanning ? 34 : 18)))
			{
				FillHex(g, b, cx, cy, scaledMeasuredValue, scaledPhase);
			}
			using (Pen p = new Pen(Col.Alpha(Theme.Accent, scanning ? 235 : 150), Math.Max(2f, (float)Theme.S(3) * 0.85f)))
			{
				DrawHex(g, p, cx, cy, scaledMeasuredValue, scaledPhase);
			}
			using (Pen p2 = new Pen(Col.Alpha(Theme.Accent2, scanning ? 170 : 90), Math.Max(1f, Theme.S(1))))
			{
				DrawHex(g, p2, cx, cy, scaledMeasuredValue * 0.62f, (0f - scaledPhase) * 1.6f);
			}
			if (!scanning)
			{
				return;
			}
			float scaledMax = Math.Max(2.5f, Theme.S(3)) * measuredValue;
			using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent2, 220)))
			{
				g.FillEllipse(brush, cx - scaledMax, cy - scaledMax, scaledMax * 2f, scaledMax * 2f);
			}
		}

		private void DrawProgressBar(Graphics g, float top)
		{
			int scaledWidth = (int)((float)base.Width * 0.52f);
			if (scaledWidth < Theme.S(140))
			{
				scaledWidth = Math.Min(base.Width - Theme.S(40), Theme.S(140));
			}
			if (scaledWidth <= 0)
			{
				return;
			}
			int sLimit = Math.Max(Theme.S(8), 7);
			int ratio = (base.Width - scaledWidth) / 2;
			int top2 = (int)top + Theme.S(22) + Theme.S(24) + ((hint.Length > 0) ? Theme.S(26) : 0);
			if (top2 + sLimit > base.Height)
			{
				return;
			}
			Rectangle r = new Rectangle(ratio, top2, scaledWidth, sLimit);
			using (GraphicsPath path = Skew(r, sLimit / 2))
			{
				using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Stroke, 150)))
				{
					g.FillPath(brush, path);
				}
				GraphicsState gstate = g.Save();
				try
				{
					g.SetClip(path, CombineMode.Intersect);
					int scaledScaledWidth = (int)((float)scaledWidth * fill.Value);
					if (scaledScaledWidth > 0)
					{
						Rectangle rect = new Rectangle(ratio, top2, scaledScaledWidth, sLimit);
						using (LinearGradientBrush brush2 = new LinearGradientBrush(new Rectangle(ratio, top2, Math.Max(1, scaledScaledWidth), sLimit), Theme.Accent, Theme.Accent2, LinearGradientMode.Horizontal))
						{
							g.FillRectangle(brush2, rect);
						}
						using (Pen pen = new Pen(Col.Alpha(Theme.Inset, 200), Math.Max(1f, Theme.S(1))))
						{
							for (int i = ratio + Theme.S(10); i < ratio + scaledScaledWidth; i += Theme.S(10))
							{
								g.DrawLine(pen, i, top2, i - sLimit, top2 + sLimit);
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
			int ringBottomOffset = (int)ringBottom + Theme.S(22);
			if (base.Width <= Theme.S(32))
			{
				return;
			}
			Rectangle rectangle = new Rectangle(Theme.S(16), ringBottomOffset, base.Width - Theme.S(32), Theme.S(24));
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
			Rectangle rectangle2 = new Rectangle(Theme.S(24), ringBottomOffset + Theme.S(24), base.Width - Theme.S(48), Theme.S(24));
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
