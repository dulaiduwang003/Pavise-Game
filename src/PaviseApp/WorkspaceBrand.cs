using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class WorkspaceBrand : Control
	{
		private Bitmap logo;

		private PerformancePreset mode = Theme.CurrentMode;

		private bool enabled = true;

		private Color logoColor = Color.Empty;

		private Font versionFont;

		private GraphicsPath wordmarkPath;

		private float typographyScale = -1f;

		private bool paintFallback;

		private string paintStage = "logo";

		private readonly StringFormat brandTextFormat = new StringFormat(StringFormat.GenericTypographic)
		{
			Alignment = StringAlignment.Near,
			LineAlignment = StringAlignment.Center,
			FormatFlags = StringFormatFlags.NoWrap,
			Trimming = StringTrimming.None
		};

		internal Rectangle WordmarkBounds { get; private set; }

		internal Rectangle VersionBounds { get; private set; }

		internal GraphicsPath WordmarkPath
		{
			get
			{
				EnsureTypography();
				if (wordmarkPath == null)
				{
					wordmarkPath = PaviseWordmark.Create(WordmarkBounds);
				}
				return wordmarkPath;
			}
		}

		internal RectangleF WordmarkInkBounds
		{
			get
			{
				return WordmarkPath.GetBounds();
			}
		}

		internal Font VersionFont
		{
			get
			{
				EnsureTypography();
				return versionFont;
			}
		}

		internal string VersionText
		{
			get
			{
				if (base.Width < Theme.S(210))
				{
					return App.Version;
				}
				return "AUTO GUARD " + App.Version;
			}
		}

		internal WorkspaceBrand()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.AccessibleName = "PAVISE " + App.Version;
			base.AccessibleDescription = "AUTO GUARD " + App.Version;
			base.AccessibleRole = AccessibleRole.StaticText;
			BackColor = Theme.Bg;
			base.TabStop = false;
		}

		internal void SetState(PerformancePreset value, bool guardEnabled)
		{
			if (mode != value || enabled != guardEnabled)
			{
				mode = value;
				enabled = guardEnabled;
				DropLogo();
				paintFallback = false;
				Invalidate();
			}
		}

		private void DropLogo()
		{
			if (logo != null)
			{
				logo.Dispose();
				logo = null;
			}
		}

		private void EnsureTypography()
		{
			if (typographyScale != Dpi.Scale || versionFont == null)
			{
				DropTypography();
				typographyScale = Dpi.Scale;
				float ratio = Math.Max(0.01f, typographyScale) * 96f / 72f;
				versionFont = new Font("Consolas", 7.5f * ratio, FontStyle.Regular, GraphicsUnit.Pixel);
			}
		}

		private void DropTypography()
		{
			DropWordmark();
			if (versionFont != null)
			{
				versionFont.Dispose();
				versionFont = null;
			}
		}

		private void DropWordmark()
		{
			if (wordmarkPath != null)
			{
				wordmarkPath.Dispose();
				wordmarkPath = null;
			}
		}

		private void ArrangeWordmark()
		{
			int minLimit = Math.Max(1, Math.Min(Theme.S(46), base.Height - Theme.S(6)));
			int minLimitOffset = minLimit + Theme.S(12);
			WordmarkBounds = new Rectangle(minLimitOffset, Theme.S(2), Math.Max(1, base.Width - minLimitOffset), Theme.S(29));
			VersionBounds = new Rectangle(minLimitOffset + Theme.S(1), Theme.S(30), Math.Max(1, base.Width - minLimitOffset - Theme.S(1)), Theme.S(16));
			DropWordmark();
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			ArrangeWordmark();
			paintFallback = false;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				DropLogo();
				DropTypography();
				brandTextFormat.Dispose();
			}
			base.Dispose(disposing);
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(e.Graphics, this, e.ClipRectangle);
				return;
			}
			using (SolidBrush brush = new SolidBrush(Theme.Bg))
			{
				e.Graphics.FillRectangle(brush, e.ClipRectangle);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (IsDisposed || Disposing || Width <= 0 || Height <= 0) return;
			// A recoverable GDI+ failure must not escape into WinForms' persistent error painting.
			if (!paintFallback)
			{
				for (int attempt = 0; attempt < 2; attempt++)
				{
					try
					{
						PaintPreservingState(e.Graphics, false, attempt > 0);
						return;
					}
					catch (Exception error)
					{
						if (!RecoverablePaintError(error)) throw;
						try { Logger.RepeatedError("workspace-brand-paint", "Workspace brand drawing failed; stage=" + paintStage
							+ "; size=" + Width + "x" + Height + "; scale=" + Dpi.Scale + "; mode=" + mode
							+ "; attempt=" + (attempt + 1) + "; rebuilding local drawing resources. " + error); } catch { }
						DropLogo();
						DropTypography();
					}
				}
				paintFallback = true;
			}
			try { PaintPreservingState(e.Graphics, true, true); }
			catch (Exception error) { if (!RecoverablePaintError(error)) throw; }
		}

		private static bool RecoverablePaintError(Exception error)
		{
			return error is ArgumentException || error is ExternalException
				|| error is InvalidOperationException || error is OutOfMemoryException;
		}

		private void PaintPreservingState(Graphics graphics, bool basic, bool repaintBackground)
		{
			GraphicsState saved = graphics.Save();
			try
			{
				if (repaintBackground) OnPaintBackground(new PaintEventArgs(graphics, ClientRectangle));
				if (basic)
				{
					// GDI text uses the control's font, independent of all three GDI+ caches.
					TextRenderer.DrawText(graphics, "PAVISE", Font, WordmarkBounds, Theme.Fg,
						TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
					TextRenderer.DrawText(graphics, VersionText, Font, VersionBounds, Theme.Dim,
						TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
				}
				else PaintBrand(graphics);
			}
			finally { graphics.Restore(saved); }
		}

		private void PaintBrand(Graphics graphics)
		{
			paintStage = "logo";
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			int minLimit = Math.Max(1, Math.Min(Theme.S(46), base.Height - Theme.S(6)));
			Color color = Theme.ModeColor(mode);
			if (logo == null || logo.Width != minLimit || logoColor != color)
			{
				DropLogo();
				logo = IconArt.Render(minLimit, mode, enabled);
				logoColor = color;
			}
			graphics.DrawImageUnscaled(logo, 0, (base.Height - minLimit) / 2);
			paintStage = "typography";
			EnsureTypography();
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
				using (SolidBrush brush = new SolidBrush(Theme.LightMode ? Theme.Fg : Color.White))
				{
					paintStage = "wordmark";
					graphics.FillPath(brush, WordmarkPath);
				}
				using (SolidBrush brush2 = new SolidBrush(Theme.Dim))
				{
					paintStage = "version";
					graphics.DrawString(VersionText, versionFont, brush2, VersionBounds, brandTextFormat);
				}
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}
	}
}
