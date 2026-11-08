using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class TierPicker : FxControl
	{
		private sealed class TierAccessibleObject : FxAccessibleObject
		{
			private readonly TierPicker owner;

			public override string Value
			{
				get
				{
					return owner.SelectedLabel;
				}
			}

			internal TierAccessibleObject(TierPicker picker)
				: base(picker)
			{
				owner = picker;
			}
		}

		private int idx = 2;

		private int hoverIdx = -1;

		private Motion[] glow = new Motion[12];

		public Action<int> IndexChanged;

		public string[] Labels;

		public bool StrongSelection;

		internal bool PlainSegments;

		private int Count
		{
			get
			{
				if (Labels == null || Labels.Length < 2)
				{
					return 3;
				}
				return Labels.Length;
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
				if (value >= 0 && value < Count && value != idx)
				{
					idx = value;
					Invalidate();
				}
			}
		}

		public string SelectedLabel
		{
			get
			{
				if (Labels == null || idx >= Labels.Length)
				{
					return "";
				}
				return Labels[idx];
			}
		}

		public TierPicker()
		{
			BackColor = Theme.Card;
			base.TabStop = true;
			base.AccessibleRole = AccessibleRole.PageTabList;
			for (int i = 0; i < glow.Length; i++)
			{
				glow[i].Speed = 0.26f;
			}
		}

		protected override void OnFrame(object s, EventArgs e)
		{
			bool stepBusySucceeded = StepBusy();
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

		private Rectangle SegmentRect(int index)
		{
			int count = Count;
			int scaledDimension = Theme.S(4);
			int countRatio = (base.Width - scaledDimension * (count - 1)) / count;
			int scaledIndex = index * (countRatio + scaledDimension);
			if (index == count - 1)
			{
				countRatio = base.Width - scaledIndex;
			}
			return new Rectangle(scaledIndex, 0, countRatio, base.Height);
		}

		private int HitIndex(Point p)
		{
			for (int i = 0; i < Count; i++)
			{
				if (SegmentRect(i).Contains(p))
				{
					return i;
				}
			}
			return -1;
		}

		private void Activate(int hit)
		{
			if (base.CanInteract && hit >= 0 && hit < Count && hit != idx)
			{
				idx = hit;
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
			if (e.Handled || e.Modifiers != 0)
			{
				return;
			}
			int navigateHorizontalResult = DsKeys.NavigateHorizontal(e.KeyCode, idx, Count);
			if (navigateHorizontalResult >= 0)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				Activate(navigateHorizontalResult);
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new TierAccessibleObject(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			DsPaint.FillBackground(graphics, this, BackColor);
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			int count = Count;
			int scaledDimension = Theme.S(6);
			for (int i = 0; i < count; i++)
			{
				Rectangle rectangle = SegmentRect(i);
				rectangle.Width--;
				rectangle.Height--;
				float t = GlowAt(i);
				if (PlainSegments)
				{
					DrawPlainFrame(graphics, rectangle, Col.Lerp(Theme.Card, Theme.CardHover, t), Col.Lerp(Theme.Stroke, Theme.StrokeHi, t));
				}
				else
				{
					DsPaint.TechFrame(graphics, rectangle, scaledDimension, Col.Lerp(Theme.Card, Theme.CardHover, t), Col.Lerp(Theme.Stroke, Theme.StrokeHi, t));
				}
			}
			Rectangle rectangle2 = Rectangle.Empty;
			if (idx < count)
			{
				rectangle2 = SegmentRect(idx);
				rectangle2.Width--;
				rectangle2.Height--;
				if (StrongSelection)
				{
					using (GraphicsPath path = (PlainSegments ? Theme.Rounded(rectangle2, Theme.S(5)) : Theme.TechPath(rectangle2, scaledDimension)))
					{
						using (LinearGradientBrush brush = new LinearGradientBrush(DsPaint.GradientRect(rectangle2), Col.Lerp(Theme.Accent, Color.White, 0.08f), Theme.Accent2, LinearGradientMode.ForwardDiagonal))
						{
							using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 235)))
							{
								graphics.FillPath(brush, path);
								graphics.DrawPath(pen, path);
							}
						}
					}
					using (Pen pen2 = new Pen(Col.Alpha(Color.White, 100), DsSpace.Line(1)))
					{
						graphics.DrawLine(pen2, rectangle2.Left + Theme.S(2), rectangle2.Top + Theme.S(1), rectangle2.Right - scaledDimension, rectangle2.Top + Theme.S(1));
					}
				}
				else if (PlainSegments)
				{
					DrawPlainFrame(graphics, rectangle2, Col.Lerp(Theme.Card, Theme.Accent, 0.18f), Col.Alpha(Theme.Accent, 215));
				}
				else
				{
					DsPaint.TechFrame(graphics, rectangle2, scaledDimension, Col.Lerp(Theme.Card, Theme.Accent, 0.18f), Col.Alpha(Theme.Accent, 215));
				}
			}
			for (int j = 0; j < count; j++)
			{
				Rectangle bounds = SegmentRect(j);
				bounds.Width--;
				bounds.Height--;
				bool matchesJ = j == idx;
				string message = ((Labels != null && j < Labels.Length) ? Labels[j] : "");
				TextRenderer.DrawText(graphics, message, DsType.Font(DsText.Label, matchesJ), bounds, (!base.Enabled) ? Theme.Faint : (matchesJ ? (StrongSelection ? Theme.OnAccent : Theme.Fg) : Theme.Dim), TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			}
			PaintBusy(graphics, base.ClientRectangle, null);
			if (!base.Enabled)
			{
				DsPaint.Veil(graphics, base.ClientRectangle, Col.Alpha(Theme.Card, DsAlpha.VeilTier));
			}
			if (base.WantsFocusCue && !rectangle2.IsEmpty)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(rectangle2, 3), Theme.Fg, Theme.Card);
			}
		}

		private static void DrawPlainFrame(Graphics g, Rectangle bounds, Color fill, Color edge)
		{
			using (GraphicsPath path = Theme.Rounded(bounds, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(fill))
				{
					using (Pen pen = new Pen(edge))
					{
						g.FillPath(brush, path);
						g.DrawPath(pen, path);
					}
				}
			}
		}
	}
}
