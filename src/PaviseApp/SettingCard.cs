using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class SettingCard : RoundPanel
	{
		public const int StatusLineH = 17;

		private string title = "";

		private string desc = "";

		private string val = "";

		private Control host;

		private bool hoverOn;

		private bool pressed;

		private Motion cardHover;

		public Color ValueColor = Theme.Dim;

		public int Channel;

		private string lockText = "";

		private bool lockOn;

		private string meta = "";

		private bool hostTop;

		private bool collapsible;

		private bool expanded = true;

		public int CollapsedHeight;

		public int ExpandedHeight;

		public Action ExpandedChanged;

		private string status = "";

		private Color statusInk = Theme.Faint;

		public string Title
		{
			get
			{
				return title;
			}
			set
			{
				string text = value ?? "";
				if (title != text)
				{
					title = text;
					Invalidate();
				}
			}
		}

		internal string StatusText
		{
			get
			{
				return status;
			}
		}

		internal Color StatusColor
		{
			get
			{
				return statusInk;
			}
		}

		internal string LockText
		{
			get
			{
				return lockText;
			}
		}

		internal bool IsLocked
		{
			get
			{
				return lockOn;
			}
		}

		public string Desc
		{
			get
			{
				return desc;
			}
			set
			{
				string text = value ?? "";
				if (desc != text)
				{
					desc = text;
					Invalidate();
				}
			}
		}

		public bool HostTop
		{
			get
			{
				return hostTop;
			}
			set
			{
				if (hostTop != value)
				{
					hostTop = value;
					LayoutHost();
				}
			}
		}

		public string Meta
		{
			get
			{
				return meta;
			}
			set
			{
				string text = value ?? "";
				if (meta != text)
				{
					meta = text;
					Invalidate();
				}
			}
		}

		public bool Collapsible
		{
			get
			{
				return collapsible;
			}
			set
			{
				if (collapsible != value)
				{
					collapsible = value;
					if (value)
					{
						Cursor = Cursors.Hand;
					}
					Invalidate();
				}
			}
		}

		public bool Expanded
		{
			get
			{
				return expanded;
			}
			set
			{
				if (expanded != value)
				{
					expanded = value;
					if (collapsible && ExpandedHeight > 0 && CollapsedHeight > 0)
					{
						base.Height = (expanded ? ExpandedHeight : CollapsedHeight);
					}
					if (ExpandedChanged != null)
					{
						ExpandedChanged();
					}
					Invalidate();
				}
			}
		}

		public bool HasStatus
		{
			get
			{
				return status.Length > 0;
			}
		}

		internal int DescLinesWanted { get; private set; }

		internal int DescLinesShown { get; private set; }

		private bool ShowDesc
		{
			get
			{
				if (collapsible)
				{
					return expanded;
				}
				return true;
			}
		}

		public string Value
		{
			get
			{
				return val;
			}
			set
			{
				string text = value ?? "";
				if (val != text)
				{
					val = text;
					Invalidate();
				}
			}
		}

		public Control Hosted
		{
			get
			{
				return host;
			}
		}

		public SettingCard()
		{
			Radius = Theme.S(12);
			Fill = Theme.Card;
			Border = Theme.Stroke;
			BackColor = Theme.Bg;
			cardHover.Speed = 0.24f;
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			UiClock.Frame += OnFrame;
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			UiClock.Frame -= OnFrame;
			base.OnHandleDestroyed(e);
		}

		private void OnFrame(object sender, EventArgs e)
		{
			if (cardHover.Step())
			{
				Fill = Col.Lerp(Theme.Card, Theme.CardHover, cardHover.Value * 0.55f);
				Border = Col.Lerp(Theme.Stroke, Theme.StrokeHi, cardHover.Value);
				Invalidate();
			}
		}

		public void Flash()
		{
			cardHover.Set(1f);
			cardHover.To(0f);
			UiClock.Wake();
			Invalidate();
		}

		public void SetLock(string text, bool on)
		{
			string text2 = text ?? "";
			if (!(lockText == text2) || lockOn != on)
			{
				lockText = text2;
				lockOn = on;
				Invalidate();
			}
		}

		internal void SnapExpanded(bool value)
		{
			expanded = value;
			Invalidate();
		}

		public void SetStatus(string text, Color ink)
		{
			string text2 = text ?? "";
			bool flag = text2 != status;
			bool flag2 = flag || ink != statusInk;
			bool flag3 = flag && text2.Length > 0 && status.Length > 0 && !string.IsNullOrEmpty(title);
			status = text2;
			statusInk = ink;
			if (flag3)
			{
				Flash();
			}
			if (flag2)
			{
				Invalidate();
			}
		}

		public void SetValue(string v, Color c)
		{
			string text = v ?? "";
			bool flag = text != val || c != ValueColor;
			ValueColor = c;
			val = text;
			if (flag)
			{
				Invalidate();
			}
		}

		public void Host(Control c)
		{
			host = c;
			base.Controls.Add(c);
			c.MouseLeave += OnHostMouseLeave;
			c.VisibleChanged += delegate
			{
				Invalidate();
			};
			LayoutHost();
			if (c is Toggle)
			{
				Cursor = Cursors.Hand;
			}
		}

		private void OnHostMouseLeave(object sender, EventArgs e)
		{
			ClearHoverIfOutside();
		}

		public void TrackChildHover(Control c)
		{
			c.MouseLeave += OnHostMouseLeave;
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			LayoutHost();
		}

		private void LayoutHost()
		{
			if (host != null)
			{
				int num = (HostTop ? Theme.S(10) : ((base.Height - host.Height) / 2));
				host.Location = new Point(base.Width - Theme.S(18) - host.Width, num);
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button == MouseButtons.Left)
			{
				pressed = true;
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			bool flag = pressed;
			if (e.Button == MouseButtons.Left)
			{
				pressed = false;
			}
			if (!flag || e.Button != MouseButtons.Left || !base.ClientRectangle.Contains(e.Location))
			{
				return;
			}
			if (collapsible)
			{
				Expanded = !expanded;
				return;
			}
			Toggle toggle = host as Toggle;
			if (toggle != null && toggle.CanInteract)
			{
				toggle.Checked = !toggle.Checked;
			}
		}

		protected override void OnMouseEnter(EventArgs e)
		{
			base.OnMouseEnter(e);
			if (!hoverOn)
			{
				hoverOn = true;
				cardHover.To(1f);
				UiClock.Wake();
			}
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			ClearHoverIfOutside();
		}

		private void ClearHoverIfOutside()
		{
			if (hoverOn && !base.IsDisposed)
			{
				Point pt;
				try
				{
					pt = PointToClient(Cursor.Position);
				}
				catch
				{
					return;
				}
				if (!base.ClientRectangle.Contains(pt))
				{
					hoverOn = false;
					cardHover.To(0f);
					UiClock.Wake();
				}
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			Graphics graphics = e.Graphics;
			DrawTacticalSurface(graphics);
			if (cardHover.Value > 0.01f)
			{
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, (int)(190f * cardHover.Value)), DsSpace.Line(2)))
				{
					graphics.DrawLine(pen, 0, Theme.S(14), 0, base.Height - Theme.S(14));
				}
			}
			int num = Theme.S(42);
			int num2 = num + ((host != null && host.Visible) ? (host.Width + Theme.S(14)) : 0);
			int num3 = (collapsible ? Theme.S(20) : 0);
			int num4 = base.Width - num - num2 - num3;
			Font font = DsType.Font(DsText.Title, true);
			int num5 = ((lockText.Length != 0) ? (DsPaint.BadgeWidth(graphics, lockText, DsType.MonoFor(DsMono.Badge, lockText)) + Theme.S(9)) : 0);
			if (collapsible)
			{
				DrawChevron(graphics, num + num4 + Theme.S(4));
			}
			bool flag = desc.Length > 0 && ShowDesc;
			int num6 = 0;
			Font font2 = DsType.Font(DsText.Body);
			if (val.Length > 0)
			{
				num6 = TextRenderer.MeasureText(graphics, val, font2).Width + Theme.S(16);
			}
			if (!flag && !HasStatus)
			{
				if (num6 > 0)
				{
					TextRenderer.DrawText(bounds: new Rectangle(base.Width / 3, 0, base.Width - base.Width / 3 - num2 - num3, base.Height), dc: graphics, text: val, font: font2, foreColor: ValueColor, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
				}
				Rectangle rectangle = new Rectangle(num, 0, num4 - num5 - num6, base.Height);
				TextRenderer.DrawText(graphics, title, font, rectangle, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
				DrawLockBadge(graphics, num, rectangle, base.Height / 2);
				return;
			}
			int num7 = Theme.S((HasStatus && !flag) ? 9 : 11);
			int num8 = Theme.S(22);
			if (num6 > 0)
			{
				TextRenderer.DrawText(bounds: new Rectangle(num, num7, num4, num8), dc: graphics, text: val, font: font2, foreColor: ValueColor, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			}
			Rectangle rectangle2 = new Rectangle(num, num7, num4 - num5 - num6, num8);
			TextRenderer.DrawText(graphics, title, font, rectangle2, Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			DrawLockBadge(graphics, num, rectangle2, num7 + Theme.S(11));
			int num9 = rectangle2.Bottom + Theme.S(1);
			if (HasStatus)
			{
				int num10 = Theme.S(6);
				Rectangle rect = new Rectangle(num + Theme.S(1), num9 + Theme.S(5), num10, num10);
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using (SolidBrush brush = new SolidBrush(statusInk))
				{
					graphics.FillEllipse(brush, rect);
				}
				TextRenderer.DrawText(graphics, status, DsType.Font(DsText.Status), new Rectangle(rect.Right + Theme.S(7), num9, num4 - Theme.S(14), Theme.S(17)), statusInk, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
				num9 += Theme.S(17);
			}
			int num11 = 0;
			if (meta.Length > 0)
			{
				num11 = Theme.S(20);
				TextRenderer.DrawText(bounds: new Rectangle(num, base.Height - Theme.S(26), Math.Max(0, base.Width - num - Theme.S(18)), Theme.S(16)), dc: graphics, text: meta, font: DsType.Mono(DsMono.Meta), foreColor: Theme.Faint, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			if (flag)
			{
				Font font3 = DsType.Font(DsText.Description);
				Rectangle bounds4 = new Rectangle(num, num9 + Theme.S(2), num4, base.Height - num9 - Theme.S(9) - num11);
				TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak;
				int num12 = DsType.LineHeight(DsText.Description);
				if (num12 > 0 && bounds4.Height >= num12)
				{
					bounds4.Height = bounds4.Height / num12 * num12;
				}
				int num13 = TextRenderer.MeasureText(graphics, desc, font3, new Size(bounds4.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
				DescLinesWanted = ((num12 > 0) ? ((num13 + num12 - 1) / num12) : 0);
				DescLinesShown = ((num12 > 0) ? (bounds4.Height / num12) : 0);
				TextRenderer.DrawText(graphics, desc, font3, bounds4, Theme.DimTint, flags);
			}
		}

		private void DrawTacticalSurface(Graphics g)
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			int num = Theme.S(30);
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, 82)))
			{
				g.DrawLine(pen, num, Theme.S(11), num, base.Height - Theme.S(11));
			}
			using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, 118 + (int)(cardHover.Value * 90f))))
			{
				g.DrawLine(pen2, num, Theme.S(17), num, Math.Min(base.Height - Theme.S(12), Theme.S(37)));
			}
			string text = ((Channel > 0) ? Channel.ToString("00") : "--");
			TextRenderer.DrawText(g, text, DsType.Mono(DsMono.Channel), new Rectangle(Theme.S(7), Theme.S(9), Theme.S(18), Theme.S(15)), Col.Lerp(Theme.Faint, Theme.Accent, cardHover.Value * 0.75f), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int num2 = base.Width - Theme.S(170);
			Point[] points = new Point[5]
			{
				new Point(num2, 1),
				new Point(base.Width - Theme.S(10), 1),
				new Point(base.Width - 1, Theme.S(11)),
				new Point(base.Width - 1, base.Height - 1),
				new Point(num2 + Theme.S(48), base.Height - 1)
			};
			using (LinearGradientBrush brush = new LinearGradientBrush(DsPaint.GradientRect(new Rectangle(Math.Max(0, num2), 0, base.Width - num2, base.Height)), Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, (int)((float)(Theme.LightMode ? 8 : 5) + cardHover.Value * 9f)), LinearGradientMode.Horizontal))
			{
				g.FillPolygon(brush, points);
			}
			using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, 74 + (int)(cardHover.Value * 82f))))
			{
				g.DrawLine(pen3, Theme.S(1), Theme.S(1), Theme.S(58), Theme.S(1));
			}
		}

		private void DrawChevron(Graphics g, int x)
		{
			int num = (expanded ? Theme.S(22) : (base.Height / 2));
			DsPaint.Chevron(g, x, num, Theme.S(4), expanded, Col.Lerp(Theme.Faint, Theme.Accent, cardHover.Value));
		}

		private void DrawLockBadge(Graphics g, int padL, Rectangle titleRect, int centerY)
		{
			if (lockText.Length != 0)
			{
				Font font = DsType.MonoFor(DsMono.Badge, lockText);
				int num = TextRenderer.MeasureText(g, title, DsType.Font(DsText.Title, true)).Width;
				if (num > titleRect.Width)
				{
					num = titleRect.Width;
				}
				int num2 = DsPaint.BadgeWidth(g, lockText, font);
				int num3 = Theme.S(17);
				Rectangle pill = new Rectangle(padL + num + Theme.S(9), centerY - num3 / 2, num2, num3);
				if (pill.Right <= base.Width - Theme.S(8))
				{
					DsPaint.Badge(g, pill, lockText, font, lockOn);
				}
			}
		}
	}
}
