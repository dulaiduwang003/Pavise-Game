using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class StatusDot : FxControl
	{
		private Color color = Theme.Dim;

		private bool pulse;

		private float phase;

		private bool followAccent;

		public Color Color
		{
			get
			{
				return color;
			}
			set
			{
				if (color != value)
				{
					color = value;
					Invalidate();
				}
			}
		}

		public bool FollowAccent
		{
			get
			{
				return followAccent;
			}
			set
			{
				if (followAccent != value)
				{
					followAccent = value;
					Invalidate();
				}
			}
		}

		private Color PaintColor
		{
			get
			{
				if (!followAccent)
				{
					return color;
				}
				return Theme.Accent;
			}
		}

		public bool Pulse
		{
			get
			{
				return pulse;
			}
			set
			{
				if (pulse != value)
				{
					pulse = value;
					if (value)
					{
						UiClock.WakeSlow();
					}
					Invalidate();
				}
			}
		}

		public StatusDot()
		{
			Cursor = Cursors.Default;
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			UiClock.SlowFrame += OnSlowFrame;
			if (pulse && base.Visible)
			{
				UiClock.WakeSlow();
			}
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			UiClock.SlowFrame -= OnSlowFrame;
			base.OnHandleDestroyed(e);
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			if (pulse && base.Visible)
			{
				UiClock.WakeSlow();
			}
		}

		private void OnSlowFrame(object sender, EventArgs e)
		{
			if (pulse && base.Visible)
			{
				UiClock.WakeSlow();
				phase += 0.72f;
				if (phase > 6.2832f)
				{
					phase -= 6.2832f;
				}
				Invalidate();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			int widthRatio = base.Width / 2;
			int heightRatio = base.Height / 2;
			int sResult = Dpi.S(5);
			Color paintColor = PaintColor;
			if (pulse)
			{
				float ratio = (float)((Math.Sin(phase) + 1.0) / 2.0);
				int sResultOffset = sResult + (int)((float)Dpi.S(6) * ratio);
				using (SolidBrush brush = new SolidBrush(Col.Alpha(paintColor, (int)(80f * (1f - ratio)))))
				{
					graphics.FillEllipse(brush, widthRatio - sResultOffset, heightRatio - sResultOffset, sResultOffset * 2, sResultOffset * 2);
				}
			}
			using (SolidBrush brush2 = new SolidBrush(paintColor))
			{
				graphics.FillEllipse(brush2, widthRatio - sResult, heightRatio - sResult, sResult * 2, sResult * 2);
			}
		}
	}
}
