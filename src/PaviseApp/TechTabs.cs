using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class TechTabs : FxControl
	{
		private sealed class TabsAccessibleObject : FxAccessibleObject
		{
			private readonly TechTabs owner;

			public override string Value
			{
				get
				{
					return owner.SelectedLabel;
				}
			}

			internal TabsAccessibleObject(TechTabs tabs)
				: base(tabs)
			{
				owner = tabs;
			}
		}

		private string[] labels = new string[0];

		private string[] hints = new string[0];

		private bool[] hot = new bool[0];

		private int idx;

		private int hoverIdx = -1;

		private Motion selection;

		private Motion[] glow = new Motion[0];

		public Action<int> IndexChanged;

		private bool CanAnimateSelection
		{
			get
			{
				if (base.IsHandleCreated && base.Visible && !UiClock.Frozen)
				{
					return !UiClock.Suspended;
				}
				return false;
			}
		}

		public int Index
		{
			get
			{
				return idx;
			}
			set
			{
				if (value >= 0 && value < labels.Length && value != idx)
				{
					idx = value;
					if (CanAnimateSelection)
					{
						selection.To(idx);
						UiClock.Wake();
					}
					else
					{
						selection.Set(idx);
					}
					Invalidate();
					if (base.IsHandleCreated)
					{
						AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
					}
					if (IndexChanged != null)
					{
						IndexChanged(idx);
					}
				}
			}
		}

		public int Count
		{
			get
			{
				return labels.Length;
			}
		}

		public string SelectedLabel
		{
			get
			{
				if (idx >= labels.Length)
				{
					return "";
				}
				return labels[idx];
			}
		}

		private int TabW
		{
			get
			{
				return Theme.S(122);
			}
		}

		private int TabGap
		{
			get
			{
				return Theme.S(6);
			}
		}

		internal int TabsWidth
		{
			get
			{
				if (labels.Length != 0)
				{
					return labels.Length * TabW + (labels.Length - 1) * TabGap;
				}
				return 0;
			}
		}

		public TechTabs()
		{
			BackColor = Theme.Bg;
			base.TabStop = true;
			base.AccessibleRole = AccessibleRole.PageTabList;
			selection.Speed = 0.4f;
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			SnapToSelection();
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			selection.Set(idx);
			base.OnHandleDestroyed(e);
		}

		protected override void OnVisibleChanged(EventArgs e)
		{
			base.OnVisibleChanged(e);
			SnapToSelection();
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			SnapToSelection();
		}

		internal void SnapToSelection()
		{
			selection.Set(idx);
			Invalidate();
		}

		protected override void OnFrame(object s, EventArgs e)
		{
			bool stepBusySucceeded = StepBusy();
			if (selection.Value != selection.Target)
			{
				if (!CanAnimateSelection)
				{
					selection.Set(idx);
					stepBusySucceeded = true;
				}
				else
				{
					if (selection.Step())
					{
						stepBusySucceeded = true;
					}
					if (Math.Abs(selection.Value - selection.Target) * (float)(TabW + TabGap) < 0.75f)
					{
						selection.Set(selection.Target);
					}
				}
			}
			for (int i = 0; i < glow.Length; i++)
			{
				if (glow[i].Step())
				{
					stepBusySucceeded = true;
				}
			}
			if (stepBusySucceeded)
			{
				Invalidate();
			}
		}

		public void SetTabs(string[] tabLabels, string[] tabHints)
		{
			labels = tabLabels ?? new string[0];
			hints = tabHints ?? new string[0];
			hot = new bool[labels.Length];
			if (idx >= labels.Length)
			{
				idx = 0;
			}
			glow = new Motion[labels.Length];
			for (int i = 0; i < glow.Length; i++)
			{
				glow[i].Speed = 0.26f;
			}
			SnapToSelection();
		}

		public void SetHot(bool[] value)
		{
			bool setHotReady = false;
			for (int i = 0; i < hot.Length; i++)
			{
				bool allConditionsMet = value != null && i < value.Length && value[i];
				if (hot[i] != allConditionsMet)
				{
					hot[i] = allConditionsMet;
					setHotReady = true;
				}
			}
			if (setHotReady)
			{
				Invalidate();
			}
		}

		private Rectangle TabRect(int i)
		{
			return new Rectangle(i * (TabW + TabGap), Theme.S(2), TabW, base.Height - Theme.S(5));
		}

		private int HitIndex(Point p)
		{
			for (int i = 0; i < labels.Length; i++)
			{
				if (TabRect(i).Contains(p))
				{
					return i;
				}
			}
			return -1;
		}

		private void Activate(int hit)
		{
			if (base.CanInteract && hit >= 0)
			{
				Index = hit;
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			int hitIndexResult = HitIndex(e.Location);
			if (hitIndexResult != hoverIdx)
			{
				hoverIdx = hitIndexResult;
				SyncGlow();
				Invalidate();
			}
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			if (hoverIdx != -1)
			{
				hoverIdx = -1;
				SyncGlow();
				Invalidate();
			}
		}

		private void SyncGlow()
		{
			for (int i = 0; i < glow.Length; i++)
			{
				glow[i].To((i == hoverIdx) ? 1f : 0f);
			}
			UiClock.Wake();
		}

		private float GlowAt(int i)
		{
			if (i < 0 || i >= glow.Length)
			{
				return 0f;
			}
			return glow[i].Value;
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			if (e.Button == MouseButtons.Left)
			{
				Activate(HitIndex(e.Location));
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if (DsKeys.IsHorizontalNavigation(keyData & Keys.KeyCode))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!e.Handled && e.Modifiers == Keys.None)
			{
				int navigateHorizontalResult = DsKeys.NavigateHorizontal(e.KeyCode, idx, labels.Length);
				if (navigateHorizontalResult >= 0)
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
					Activate(navigateHorizontalResult);
				}
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new TabsAccessibleObject(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (!CanAnimateSelection)
			{
				selection.Set(idx);
			}
			Graphics graphics = e.Graphics;
			DsPaint.FillBackground(graphics, this, BackColor);
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			int scaledDimension = Theme.S(9);
			for (int i = 0; i < labels.Length; i++)
			{
				Rectangle r = TabRect(i);
				r.Width--;
				r.Height--;
				float glowAtResult = GlowAt(i);
				DsPaint.TechFrame(graphics, r, scaledDimension, Backdrop.CardFill(this, Col.Lerp(Theme.Card, Theme.CardHover, glowAtResult)), Col.Lerp(Theme.Stroke, Theme.StrokeHi, glowAtResult));
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, (int)(70f + 60f * glowAtResult)), DsSpace.Line(1)))
				{
					graphics.DrawLine(pen, r.Right - scaledDimension, r.Top, r.Right, r.Top + scaledDimension);
				}
			}
			Rectangle r2 = Rectangle.Empty;
			if (labels.Length > 0)
			{
				r2 = TabRect(idx);
				r2.X = (int)Math.Round(selection.Value * (float)(TabW + TabGap));
				r2.Width--;
				r2.Height--;
				DsPaint.TechFrame(graphics, r2, scaledDimension, Backdrop.CardFill(this, Col.Lerp(Theme.Card, Theme.Accent, 0.16f)), Col.Alpha(Theme.Accent, 220));
				using (Pen pen2 = new Pen(Theme.Accent, DsSpace.Line(2)))
				{
					graphics.DrawLine(pen2, r2.Left, r2.Top + scaledDimension, r2.Left, r2.Bottom - scaledDimension);
				}
				using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, 200), DsSpace.Line(1)))
				{
					graphics.DrawLine(pen3, r2.Right - scaledDimension, r2.Top, r2.Right, r2.Top + scaledDimension);
				}
			}
			for (int j = 0; j < labels.Length; j++)
			{
				Rectangle rectangle = TabRect(j);
				rectangle.Width--;
				rectangle.Height--;
				bool matchesJ = j == idx;
				string text2 = (j + 1).ToString("00");
				Font font = DsType.Mono(DsMono.TabNumber);
				Size size = TextRenderer.MeasureText(graphics, text2, font, Size.Empty, TextFormatFlags.NoPadding);
				int leftOffset = rectangle.Left + Theme.S(14);
				TextRenderer.DrawText(graphics, text2, font, new Rectangle(leftOffset, rectangle.Top, size.Width, rectangle.Height), matchesJ ? Theme.Accent : Theme.Faint, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				TextRenderer.DrawText(graphics, labels[j], DsType.Font(DsText.Label, matchesJ), new Rectangle(leftOffset + size.Width + Theme.S(7), rectangle.Top, rectangle.Right - leftOffset - size.Width - Theme.S(10), rectangle.Height), matchesJ ? Theme.Fg : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				if (j < hot.Length && hot[j])
				{
					Rectangle rect = new Rectangle(rectangle.Left + Theme.S(6), rectangle.Top + Theme.S(5), Theme.S(5), Theme.S(5));
					using (SolidBrush brush = new SolidBrush(Theme.Accent))
					{
						graphics.FillEllipse(brush, rect);
					}
				}
			}
			if (idx < hints.Length && !string.IsNullOrEmpty(hints[idx]))
			{
				int scaledLength = labels.Length * (TabW + TabGap);
				Rectangle bounds = new Rectangle(scaledLength + Theme.S(8), 0, base.Width - scaledLength - Theme.S(10), base.Height);
				if (bounds.Width > Theme.S(60))
				{
					TextRenderer.DrawText(graphics, hints[idx], DsType.Font(DsText.Hint), bounds, Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
				}
			}
			PaintBusy(graphics, new Rectangle(0, 0, Math.Max(1, TabsWidth), base.Height), null);
			if (base.WantsFocusCue && !r2.IsEmpty)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(TabRect(idx), 4), Theme.Fg, Theme.Card);
			}
		}
	}
}
