using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class Toggle : FxControl
	{
		private sealed class ToggleAccessibleObject : FxAccessibleObject
		{
			private readonly Toggle owner;

			public override AccessibleStates State
			{
				get
				{
					AccessibleStates accessibleStates = base.State;
					if (owner.isOn)
					{
						accessibleStates |= AccessibleStates.Checked;
					}
					return accessibleStates;
				}
			}

			public override string DefaultAction
			{
				get
				{
					return DsLabels.Pick("切换", "Toggle");
				}
			}

			internal ToggleAccessibleObject(Toggle toggle)
				: base(toggle)
			{
				owner = toggle;
			}

			public override void DoDefaultAction()
			{
				owner.PerformClick();
			}
		}

		private bool isOn;

		private Motion colorBlend;

		internal bool PerformanceConsole;

		internal bool OverviewGuard;

		public bool Checked
		{
			get
			{
				return isOn;
			}
			set
			{
				if (isOn != value)
				{
					isOn = value;
					colorBlend.To(value ? 1f : 0f);
					UiClock.Wake();
					Invalidate();
					NotifyStateChange();
					if (this.CheckedChanged != null)
					{
						this.CheckedChanged(this, EventArgs.Empty);
					}
				}
			}
		}

		public event EventHandler CheckedChanged;

		public Toggle()
		{
			base.Size = new Size(Theme.S(46), Theme.S(30));
			ForeColor = Theme.Fg;
			Font = DsType.Font(DsText.Title);
			colorBlend.Speed = 0.32f;
			base.TabStop = true;
			base.AccessibleRole = AccessibleRole.CheckButton;
		}

		public void SetSilently(bool v)
		{
			float num = (v ? 1f : 0f);
			if (isOn != v || colorBlend.Value != num || colorBlend.Target != num)
			{
				isOn = v;
				colorBlend.Set(num);
				Invalidate();
				NotifyStateChange();
			}
		}

		public void PerformClick()
		{
			if (base.CanInteract)
			{
				OnClick(EventArgs.Empty);
			}
		}

		private void NotifyStateChange()
		{
			if (base.IsHandleCreated)
			{
				AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
			}
		}

		protected override bool StepAll()
		{
			bool flag = base.StepAll();
			bool result = colorBlend.Step();
			if (!flag)
			{
				return result;
			}
			return true;
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (base.CanInteract)
			{
				Checked = !isOn;
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if (DsKeys.IsActivation(keyData))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (DsKeys.IsPlainActivation(e) && base.CanInteract)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				PerformClick();
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new ToggleAccessibleObject(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (OverviewGuard)
			{
				FillBg(e.Graphics);
				OverviewGuardSwitch.Paint(e.Graphics, this, colorBlend.Value, hover.Value);
				using (GraphicsPath clip = Theme.TechPath(OverviewGuardSwitch.FrameBounds(this), Theme.S(6)))
				{
					PaintBusy(e.Graphics, base.ClientRectangle, clip);
				}
				if (!base.Enabled)
				{
					DsPaint.Veil(e.Graphics, base.ClientRectangle, Col.Alpha(Theme.Card, 105));
				}
				if (base.WantsFocusCue)
				{
					DsFocus.Draw(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Fg, base.EffBg);
				}
				return;
			}
			if (PerformanceConsole)
			{
				PerformanceConsoleToggle.Paint(e.Graphics, this, colorBlend.Value, hover.Value);
				PaintBusy(e.Graphics, base.ClientRectangle, null);
				if (base.WantsFocusCue)
				{
					DsFocus.Draw(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -Theme.S(3), -Theme.S(3)), Theme.Fg, base.EffBg);
				}
				return;
			}
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			int num = Theme.S(22);
			int num2 = Theme.S(3);
			if (num > base.Height)
			{
				num = base.Height;
			}
			int num3 = (string.IsNullOrEmpty(Text) ? Math.Max(Theme.S(20), base.Width - 1) : Theme.S(44));
			int num4 = num - num2 * 2;
			Rectangle rectangle = new Rectangle(0, (base.Height - num) / 2, num3, num);
			float value = colorBlend.Value;
			using (GraphicsPath path = Theme.Rounded(rectangle, num / 2))
			{
				using (SolidBrush brush = new SolidBrush(Theme.TrackOff))
				{
					graphics.FillPath(brush, path);
				}
				if (value > 0.01f)
				{
					using (LinearGradientBrush brush2 = new LinearGradientBrush(DsPaint.GradientRect(rectangle), Col.Alpha(Theme.Accent, (int)(255f * value)), Col.Alpha(Theme.Accent2, (int)(255f * value)), LinearGradientMode.Horizontal))
					{
						graphics.FillPath(brush2, path);
					}
				}
				else
				{
					using (Pen pen = new Pen(Theme.Stroke))
					{
						graphics.DrawPath(pen, path);
					}
				}
				if (hover.Value > 0.01f)
				{
					using (SolidBrush brush3 = new SolidBrush(Col.Alpha(Color.White, (int)(16f * hover.Value))))
					{
						graphics.FillPath(brush3, path);
					}
				}
			}
			if (num4 > 0)
			{
				int num5 = (isOn ? (rectangle.Right - num2 - num4) : (rectangle.X + num2));
				int num6 = rectangle.Y + num2;
				using (SolidBrush brush4 = new SolidBrush(Col.Alpha(Color.Black, 70)))
				{
					graphics.FillEllipse(brush4, num5, num6 + Theme.S(1), num4, num4);
				}
				using (SolidBrush brush5 = new SolidBrush(isOn ? DsPalette.KnobOn : DsPalette.KnobOff))
				{
					graphics.FillEllipse(brush5, num5, num6, num4, num4);
				}
			}
			if (!string.IsNullOrEmpty(Text))
			{
				int num7 = num3 + Theme.S(12);
				TextRenderer.DrawText(graphics, Text, Font, new Rectangle(num7, 0, base.Width - num7, base.Height), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			}
			PaintBusy(graphics, new Rectangle(0, 0, num3, base.Height), null);
			if (!base.Enabled)
			{
				DsPaint.Veil(graphics, base.ClientRectangle, Col.Alpha(Theme.Card, 105));
			}
			if (base.WantsFocusCue)
			{
				int num8 = Theme.S(2);
				DsFocus.Draw(graphics, Rectangle.Intersect(Rectangle.Inflate(rectangle, num8, num8), base.ClientRectangle), Theme.Fg, base.EffBg);
			}
		}
	}
}
