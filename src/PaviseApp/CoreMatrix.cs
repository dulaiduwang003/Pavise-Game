using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class CoreMatrix : Control
	{
		private sealed class Cell
		{
			public int Cpu;

			public Rectangle Rect;

			public bool Sibling;
		}

		private sealed class Group
		{
			public int Index;

			public ulong Mask;

			public Rectangle Rect;

			public readonly List<Cell> Cells = new List<Cell>();
		}

		private sealed class Band
		{
			public string Title;

			public string Meta;

			public int TitleY;

			public readonly List<Group> Groups = new List<Group>();
		}

		internal enum HeadTag
		{
			None,
			Exclusive,
			Primary,
			Cache,
			Smt
		}

		private const int CellW = 30;

		private const int CellH = 30;

		private const int GroupPad = 3;

		private const int GroupHead = 15;

		private const int GroupGap = 7;

		private const int RowGap = 7;

		private const int BandHead = 26;

		private const int BandGap = 16;

		private readonly List<Band> bands = new List<Band>();

		private ulong selected;

		private ulong allMask;

		private ulong cacheHeavy;

		private int laidOutFor;

		private readonly Motion[] cellOn = new Motion[64];

		public string PrimaryTag;

		public bool MarkExclusive = true;

		private bool editable;

		private int focusCpu;

		private int CellWpx
		{
			get
			{
				return Theme.S(30);
			}
		}

		private int CellHpx
		{
			get
			{
				return Theme.S(30);
			}
		}

		public ulong DisabledMask { get; set; }

		public ulong SchedulingGameMask { get; set; }

		public ulong SchedulingIsolationMask { get; set; }

		public bool SchedulingOverlay { get; set; }

		public Color SelectionColor { get; set; }

		internal static Color GameColor
		{
			get
			{
				return Theme.Accent;
			}
		}

		internal static Color IsolationColor
		{
			get
			{
				return Theme.Accent2;
			}
		}

		public ulong Selected
		{
			get
			{
				return selected;
			}
			set
			{
				ulong num = value & allMask;
				if (num != selected)
				{
					selected = num;
					SyncCells();
					Invalidate();
				}
			}
		}

		internal Rectangle FocusedCellBounds
		{
			get
			{
				foreach (Band band in bands)
				{
					foreach (Group group in band.Groups)
					{
						foreach (Cell cell in group.Cells)
						{
							if (cell.Cpu == focusCpu)
							{
								return cell.Rect;
							}
						}
					}
				}
				return Rectangle.Empty;
			}
		}

		public bool Editable
		{
			get
			{
				return editable;
			}
			set
			{
				editable = value;
				base.TabStop = value;
				Cursor = (value ? Cursors.Hand : Cursors.Default);
			}
		}

		public event Action<ulong> SelectionChanged;

		internal event Action<Rectangle> FocusedCellChanged;

		public CoreMatrix()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Bg;
			allMask = CpuTopology.AllMask;
			selected = allMask;
			for (int i = 0; i < 64; i++)
			{
				cellOn[i].Speed = 0.34f;
			}
			SnapCells();
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

		private void OnFrame(object s, EventArgs e)
		{
			bool flag = false;
			for (int i = 0; i < 64; i++)
			{
				if (cellOn[i].Step())
				{
					flag = true;
				}
			}
			if (flag)
			{
				Invalidate();
			}
		}

		private void SnapCells()
		{
			for (int i = 0; i < 64; i++)
			{
				cellOn[i].Set(((selected & (ulong)(1L << i)) != 0) ? 1f : 0f);
			}
		}

		private void SyncCells()
		{
			if (!base.IsHandleCreated)
			{
				SnapCells();
				return;
			}
			for (int i = 0; i < 64; i++)
			{
				cellOn[i].To(((selected & (ulong)(1L << i)) != 0) ? 1f : 0f);
			}
			UiClock.Wake();
		}

		internal void ShowSelectionImmediately(ulong mask)
		{
			selected = mask & allMask;
			SnapCells();
			Invalidate();
		}

		internal void ToggleSelection(ulong mask)
		{
			if (!Editable || !base.Enabled)
			{
				return;
			}
			mask &= allMask & (~DisabledMask | Selected);
			if (mask != 0)
			{
				Selected = (((Selected & mask) == mask) ? (Selected & ~mask) : (Selected | (mask & ~DisabledMask)));
				if (this.SelectionChanged != null)
				{
					this.SelectionChanged(Selected);
				}
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (!Editable || e.Button != MouseButtons.Left)
			{
				return;
			}
			Focus();
			foreach (Band band in bands)
			{
				foreach (Group group in band.Groups)
				{
					foreach (Cell cell in group.Cells)
					{
						if (cell.Rect.Contains(e.Location))
						{
							focusCpu = cell.Cpu;
							ToggleSelection((ulong)(1L << cell.Cpu));
							return;
						}
					}
					if (group.Rect.Contains(e.Location))
					{
						ToggleSelection(group.Mask);
						return;
					}
				}
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			Keys keys = keyData & Keys.KeyCode;
			if (!Editable || (keys != Keys.Left && keys != Keys.Right && keys != Keys.Up && keys != Keys.Down && keys != Keys.Space))
			{
				return base.IsInputKey(keyData);
			}
			return true;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!Editable || e.Modifiers != 0)
			{
				return;
			}
			int num = focusCpu;
			if (e.KeyCode == Keys.Space)
			{
				ToggleSelection((ulong)(1L << focusCpu));
			}
			else
			{
				if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Up && e.KeyCode != Keys.Right && e.KeyCode != Keys.Down)
				{
					return;
				}
				int num2 = ((e.KeyCode != Keys.Left && e.KeyCode != Keys.Up) ? 1 : (-1));
				for (int i = 0; i < 64; i++)
				{
					focusCpu = (focusCpu + num2 + 64) % 64;
					if ((allMask & ~DisabledMask & (ulong)(1L << focusCpu)) != 0)
					{
						break;
					}
				}
			}
			bool handled = (e.SuppressKeyPress = true);
			e.Handled = handled;
			if (focusCpu != num && this.FocusedCellChanged != null)
			{
				this.FocusedCellChanged(FocusedCellBounds);
			}
			Invalidate();
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			Invalidate();
		}

		protected override void OnLostFocus(EventArgs e)
		{
			base.OnLostFocus(e);
			Invalidate();
		}

		private void DrawInputFocus(Graphics g)
		{
			if (!Editable || !Focused)
			{
				return;
			}
			foreach (Band band in bands)
			{
				foreach (Group group in band.Groups)
				{
					foreach (Cell cell in group.Cells)
					{
						if (cell.Cpu == focusCpu)
						{
							ControlPaint.DrawFocusRectangle(g, cell.Rect, Theme.Fg, BackColor);
						}
					}
				}
			}
		}

		public int LayoutFor(int width)
		{
			return LayoutBands(width);
		}

		private int LayoutBands(int width)
		{
			allMask = CpuTopology.AllMask;
			bands.Clear();
			laidOutFor = width;
			ulong[] array = CpuTopology.PhysicalCoreMasks();
			if (array.Length == 0)
			{
				return Theme.S(40);
			}
			cacheHeavy = CpuTopology.CacheHeavyMask();
			ulong[] array2 = CpuTopology.DieMasks();
			int num = 0;
			if (array2.Length >= 2)
			{
				for (int i = 0; i < array2.Length; i++)
				{
					List<ulong> list = new List<ulong>();
					ulong[] array3 = array;
					foreach (ulong num2 in array3)
					{
						ulong num3 = num2 & allMask;
						if (num3 != 0 && (num3 & array2[i]) != 0)
						{
							list.Add(num3);
						}
					}
					if (list.Count != 0)
					{
						if (num > 0)
						{
							num += Theme.S(16);
						}
						bool flag = cacheHeavy != 0 && (array2[i] & cacheHeavy) != 0;
						num = AddBand(list, Lang.F(flag ? "core.band.ccd.cache" : "core.band.ccd", i), width, num, true);
					}
				}
				return num;
			}
			ulong num4 = (CpuTopology.Hybrid ? CpuTopology.PerfMask : 0);
			List<ulong> list2 = new List<ulong>();
			List<ulong> list3 = new List<ulong>();
			ulong[] array4 = array;
			foreach (ulong num5 in array4)
			{
				ulong num6 = num5 & allMask;
				if (num6 != 0)
				{
					if ((num4 != 0) ? ((num6 & num4) != 0) : (CpuTopology.CountSetBits(num6) > 1))
					{
						list2.Add(num6);
					}
					else
					{
						list3.Add(num6);
					}
				}
			}
			bool flag2 = list2.Count > 0 && list3.Count > 0;
			if (list2.Count > 0)
			{
				num = AddBand(list2, flag2 ? Lang.T("core.band.perf") : Lang.T("core.band.all"), width, num);
			}
			if (list3.Count > 0)
			{
				if (list2.Count > 0)
				{
					num += Theme.S(16);
				}
				num = AddBand(list3, flag2 ? Lang.T("core.band.eff") : Lang.T("core.band.all"), width, num);
			}
			return num;
		}

		private int AddBand(List<ulong> groups, string title, int width, int y)
		{
			bool smt = false;
			foreach (ulong group in groups)
			{
				if (CpuTopology.CountSetBits(group) > 1)
				{
					smt = true;
					break;
				}
			}
			return AddBand(groups, title, width, y, smt);
		}

		private int AddBand(List<ulong> groups, string title, int width, int y, bool smt)
		{
			int num = 0;
			foreach (ulong group3 in groups)
			{
				num += CpuTopology.CountSetBits(group3);
			}
			Band band = new Band();
			band.Title = title;
			band.Meta = Lang.F(smt ? "core.band.meta.smt" : "core.band.meta.solo", groups.Count, num);
			band.TitleY = y;
			Band band2 = band;
			y += Theme.S(26);
			int num2 = 0;
			int num3 = Theme.S(15) + CellHpx + Theme.S(3) * 2;
			int num4 = num3;
			foreach (ulong group4 in groups)
			{
				int num5 = CpuTopology.CountSetBits(group4);
				int num6 = num5 * CellWpx + Theme.S(3) * 2;
				if (num2 > 0 && num2 + num6 > width)
				{
					num2 = 0;
					y += num4 + Theme.S(7);
				}
				Group group = new Group();
				group.Index = LowestIndex(group4);
				group.Mask = group4;
				group.Rect = new Rectangle(num2, y, num6, num3);
				Group group2 = group;
				int num7 = num2 + Theme.S(3);
				bool flag = true;
				for (int i = 0; i < 64; i++)
				{
					if ((group4 & (ulong)(1L << i)) != 0)
					{
						group2.Cells.Add(new Cell
						{
							Cpu = i,
							Rect = new Rectangle(num7, y + Theme.S(15) + Theme.S(3), CellWpx, CellHpx),
							Sibling = !flag
						});
						num7 += CellWpx;
						flag = false;
					}
				}
				band2.Groups.Add(group2);
				num2 += num6 + Theme.S(7);
			}
			bands.Add(band2);
			return y + num4;
		}

		private static int LowestIndex(ulong mask)
		{
			for (int i = 0; i < 64; i++)
			{
				if ((mask & (ulong)(1L << i)) != 0)
				{
					return i;
				}
			}
			return 0;
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);
			if (base.Width > 0 && base.Width != laidOutFor)
			{
				LayoutFor(base.Width);
				Invalidate();
			}
		}

		internal static HeadTag HeadTagFor(bool exclusive, bool anyOn, bool cache, bool multiThread)
		{
			if (exclusive)
			{
				return HeadTag.Exclusive;
			}
			if (anyOn)
			{
				return HeadTag.Primary;
			}
			if (cache)
			{
				return HeadTag.Cache;
			}
			if (!multiThread)
			{
				return HeadTag.None;
			}
			return HeadTag.Smt;
		}

		internal static bool HeadTagFits(int headWidth, int indexWidth, int tagWidth, int gap)
		{
			if (tagWidth > 0)
			{
				return indexWidth + gap + tagWidth <= headWidth;
			}
			return false;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.PaintOnCard(graphics, this, base.ClientRectangle);
			}
			else
			{
				using (SolidBrush brush = new SolidBrush(BackColor))
				{
					graphics.FillRectangle(brush, base.ClientRectangle);
				}
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			foreach (Band band in bands)
			{
				Rectangle bounds = new Rectangle(0, band.TitleY, base.Width, Theme.S(26));
				TextRenderer.DrawText(graphics, band.Title, Theme.UI(8.4f, true), bounds, Theme.Fg, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				TextRenderer.DrawText(graphics, band.Meta, Theme.MonoFor(band.Meta, 7.4f), bounds, Theme.Faint, TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				int num = band.TitleY + Theme.S(26) - Theme.S(4);
				using (Pen pen = new Pen(Theme.Stroke))
				{
					graphics.DrawLine(pen, 0, num, base.Width - 1, num);
				}
				foreach (Group group in band.Groups)
				{
					DrawGroup(graphics, group);
				}
			}
			DrawInputFocus(graphics);
		}

		private void DrawGroup(Graphics g, Group grp)
		{
			int num = 0;
			foreach (Cell cell in grp.Cells)
			{
				if ((selected & (ulong)(1L << cell.Cpu)) != 0)
				{
					num++;
				}
			}
			bool flag = num > 0;
			bool flag2 = MarkExclusive && grp.Cells.Count > 1 && num == 1;
			float num2 = 0f;
			foreach (Cell cell2 in grp.Cells)
			{
				if (cellOn[cell2.Cpu].Value > num2)
				{
					num2 = cellOn[cell2.Cpu].Value;
				}
			}
			Rectangle rect = grp.Rect;
			rect.Width--;
			rect.Height--;
			using (GraphicsPath path = Theme.TechPath(rect, Theme.S(5)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Bg, Theme.Card, num2)))
				{
					g.FillPath(brush, path);
				}
				Color color = (flag2 ? Col.Alpha(Theme.Accent, 200) : (flag ? Theme.StrokeHi : Theme.Stroke));
				float num3 = (flag2 ? Math.Max(1f, (float)Theme.S(2) * 0.75f) : 1f);
				using (Pen pen = new Pen(color, num3))
				{
					g.DrawPath(pen, path);
				}
			}
			Rectangle bounds = new Rectangle(rect.Left + Theme.S(3), rect.Top + Theme.S(1), rect.Width - Theme.S(3) * 2, Theme.S(15));
			bool flag3 = cacheHeavy != 0 && (grp.Mask & cacheHeavy) != 0;
			TextRenderer.DrawText(g, "C" + grp.Index.ToString("00"), Theme.Mono(6.9f), bounds, (!flag3) ? (flag ? Theme.Dim : Theme.Faint) : (flag ? Theme.Accent2 : Col.Alpha(Theme.Accent2, 170)), TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			bool multiThread = grp.Cells.Count > 1;
			string text;
			switch (HeadTagFor(flag2, flag, flag3, multiThread))
			{
			case HeadTag.Exclusive:
				text = Lang.T("core.tag.exclusive");
				break;
			case HeadTag.Primary:
				text = PrimaryTag;
				break;
			case HeadTag.Cache:
				text = Lang.T("core.tag.cache");
				break;
			case HeadTag.Smt:
				text = "SMT";
				break;
			default:
				text = null;
				break;
			}
			if (text != null)
			{
				Font font = Theme.MonoFor(text, 6.4f);
				int indexWidth = TextRenderer.MeasureText(g, "C" + grp.Index.ToString("00"), Theme.Mono(6.9f), bounds.Size, TextFormatFlags.NoPadding).Width;
				int tagWidth = TextRenderer.MeasureText(g, text, font, bounds.Size, TextFormatFlags.NoPadding).Width;
				if (!HeadTagFits(bounds.Width, indexWidth, tagWidth, Theme.S(3)))
				{
					text = null;
				}
			}
			if (text != null)
			{
				TextRenderer.DrawText(g, text, Theme.MonoFor(text, 6.4f), bounds, flag2 ? Theme.Accent : (flag ? Col.Alpha(Theme.Accent, 220) : (flag3 ? Col.Alpha(Theme.Accent2, 170) : Col.Alpha(Theme.Faint, 150))), TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			foreach (Cell cell3 in grp.Cells)
			{
				DrawCell(g, cell3);
			}
		}

		private void DrawCell(Graphics g, Cell c)
		{
			float value = cellOn[c.Cpu].Value;
			Rectangle rect = c.Rect;
			rect.Inflate(-Theme.S(2), -Theme.S(2));
			Color color = ((SchedulingOverlay && !SelectionColor.IsEmpty) ? SelectionColor : Theme.Accent);
			Color color2 = Col.Lerp(Theme.Bg, color, value);
			bool flag = (DisabledMask & (ulong)(1L << c.Cpu)) != 0 || !base.Enabled;
			if (flag)
			{
				color2 = Theme.Card;
			}
			using (GraphicsPath path = Theme.TechPath(rect, Theme.S(4)))
			{
				using (SolidBrush brush = new SolidBrush(color2))
				{
					g.FillPath(brush, path);
				}
				Color color3 = Col.Lerp(Theme.Stroke, Col.Alpha(color, 235), value);
				using (Pen pen = new Pen(color3, 1f))
				{
					g.DrawPath(pen, path);
				}
			}
			TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(7.6f), rect, flag ? Theme.Faint : Col.Lerp(Theme.Dim, Theme.OnAccent, value), TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (!SchedulingOverlay)
			{
				return;
			}
			ulong num = (ulong)(1L << c.Cpu);
			int num2 = rect.Left + Theme.S(3);
			int num3 = rect.Bottom - Theme.S(4);
			ulong[] array = new ulong[2] { SchedulingGameMask, SchedulingIsolationMask };
			Color[] array2 = new Color[2] { GameColor, IsolationColor };
			for (int i = 0; i < array.Length; i++)
			{
				if ((array[i] & num) != 0)
				{
					using (SolidBrush brush2 = new SolidBrush(array2[i]))
					{
						g.FillRectangle(brush2, num2, num3, Theme.S(5), Theme.S(2));
					}
				}
				num2 += Theme.S(7);
			}
		}
	}
}
