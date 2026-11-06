using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameLibraryList : Panel
	{
		private readonly List<GameLibraryItem> items = new List<GameLibraryItem>();

		private readonly List<GameLibraryRow> rows = new List<GameLibraryRow>();

		private readonly ToolTip tips = new ToolTip();

		private int selectedIndex = -1;

		private bool arranging;

		public Func<string, Bitmap> IconProvider { get; set; }

		public IList<GameLibraryItem> Items
		{
			get
			{
				return items.AsReadOnly();
			}
		}

		public GameLibraryItem SelectedItem
		{
			get
			{
				if (selectedIndex < 0 || selectedIndex >= items.Count)
				{
					return null;
				}
				return items[selectedIndex];
			}
		}

		public int SelectedIndex
		{
			get
			{
				return selectedIndex;
			}
			set
			{
				SelectIndex(value, false);
			}
		}

		public event EventHandler SelectedIndexChanged;

		public event EventHandler<GameLibraryEventArgs> FamilyToggleRequested;

		public event EventHandler<GameLibraryEventArgs> CoreSchedulingRequested;

		public event EventHandler<GameLibraryEventArgs> CoreToggleRequested;

		public event EventHandler<GameLibraryEventArgs> RebarToggleRequested;

		public event EventHandler<GameLibraryEventArgs> FullscreenOptimizationsToggleRequested;

		public event EventHandler<GameLibraryEventArgs> MoreRequested;

		public GameLibraryList()
		{
			DoubleBuffered = true;
			AutoScroll = true;
			base.TabStop = false;
			BackColor = Theme.Card;
			base.AccessibleRole = AccessibleRole.List;
			base.AccessibleName = Lang.T("nav.library");
			Native.Dark(this);
			tips.InitialDelay = 500;
			tips.ReshowDelay = 120;
			tips.AutoPopDelay = 18000;
			tips.ShowAlways = true;
		}

		public void SetItems(List<GameLibraryItem> fresh)
		{
			string text = ((SelectedItem == null) ? null : SelectedItem.Profile.Id);
			int num = -base.AutoScrollPosition.Y;
			bool flag = fresh.Count != items.Count;
			if (!flag)
			{
				for (int i = 0; i < fresh.Count; i++)
				{
					if (!string.Equals(fresh[i].Profile.Id, items[i].Profile.Id, StringComparison.OrdinalIgnoreCase))
					{
						flag = true;
						break;
					}
				}
			}
			SuspendLayout();
			try
			{
				if (flag)
				{
					foreach (GameLibraryRow row2 in rows)
					{
						row2.Dispose();
					}
					rows.Clear();
					items.Clear();
					selectedIndex = -1;
					foreach (GameLibraryItem item in fresh)
					{
						GameLibraryRow row = new GameLibraryRow(item, tips);
						row.IconProvider = IconProvider;
						row.TabIndex = rows.Count;
						row.SelectionRequested += delegate
						{
							SelectIndex(rows.IndexOf(row), false);
						};
						row.FamilyRequested += delegate
						{
							SelectIndex(rows.IndexOf(row), false);
							if (this.FamilyToggleRequested != null)
							{
								this.FamilyToggleRequested(this, new GameLibraryEventArgs(row.Item));
							}
						};
						row.CoreRequested += delegate
						{
							SelectIndex(rows.IndexOf(row), false);
							if (this.CoreSchedulingRequested != null)
							{
								this.CoreSchedulingRequested(this, new GameLibraryEventArgs(row.Item));
							}
						};
						row.CoreToggleRequested += delegate
						{
							SelectIndex(rows.IndexOf(row), false);
							if (this.CoreToggleRequested != null)
							{
								this.CoreToggleRequested(this, new GameLibraryEventArgs(row.Item));
							}
						};
						row.CoreSwitch.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						row.RebarRequested += delegate
						{
							SelectIndex(rows.IndexOf(row), false);
							if (this.RebarToggleRequested != null)
							{
								this.RebarToggleRequested(this, new GameLibraryEventArgs(row.Item));
							}
						};
						row.RebarSwitch.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						row.FullscreenOptimizationsRequested += delegate
						{
							SelectIndex(rows.IndexOf(row), false);
							if (this.FullscreenOptimizationsToggleRequested != null)
							{
								this.FullscreenOptimizationsToggleRequested(this, new GameLibraryEventArgs(row.Item));
							}
						};
						row.FullscreenOptimizationsSwitch.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						row.CoreButton.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						row.MoreRequested += delegate
						{
							if (this.MoreRequested != null)
							{
								this.MoreRequested(this, new GameLibraryEventArgs(row.Item, row.MoreButton));
							}
						};
						row.MoreButton.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						row.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						row.FamilySwitch.KeyDown += delegate(object sender, KeyEventArgs e)
						{
							HandleRowKey(row, e);
						};
						rows.Add(row);
						items.Add(item);
						base.Controls.Add(row);
					}
				}
				else
				{
					for (int j = 0; j < fresh.Count; j++)
					{
						items[j] = fresh[j];
						rows[j].SetItem(fresh[j]);
					}
				}
			}
			finally
			{
				ResumeLayout(true);
			}
			int value = -1;
			if (text != null)
			{
				for (int k = 0; k < items.Count; k++)
				{
					if (string.Equals(items[k].Profile.Id, text, StringComparison.OrdinalIgnoreCase))
					{
						value = k;
						break;
					}
				}
			}
			SelectIndex(value, false);
			if (flag)
			{
				base.AutoScrollPosition = new Point(0, num);
			}
			RefreshRows();
		}

		public void RefreshRows()
		{
			foreach (GameLibraryRow row in rows)
			{
				row.RefreshModel();
			}
			Invalidate();
		}

		private void SelectIndex(int value, bool focus)
		{
			if (value < -1 || value >= items.Count)
			{
				value = -1;
			}
			bool flag = value != selectedIndex;
			selectedIndex = value;
			for (int i = 0; i < rows.Count; i++)
			{
				rows[i].Selected = i == selectedIndex;
			}
			if (focus && value >= 0)
			{
				ScrollControlIntoView(rows[value]);
				rows[value].Focus();
			}
			if (flag && this.SelectedIndexChanged != null)
			{
				this.SelectedIndexChanged(this, EventArgs.Empty);
			}
		}

		private void HandleRowKey(GameLibraryRow row, KeyEventArgs e)
		{
			int num = rows.IndexOf(row);
			switch (e.KeyCode)
			{
			case Keys.Up:
				num--;
				break;
			case Keys.Down:
				num++;
				break;
			case Keys.Home:
				num = 0;
				break;
			case Keys.End:
				num = rows.Count - 1;
				break;
			case Keys.Prior:
				num -= Math.Max(1, base.ClientSize.Height / Math.Max(1, row.Height));
				break;
			case Keys.Next:
				num += Math.Max(1, base.ClientSize.Height / Math.Max(1, row.Height));
				break;
			default:
				OnKeyDown(e);
				return;
			}
			e.Handled = true;
			e.SuppressKeyPress = true;
			SelectIndex(Math.Max(0, Math.Min(rows.Count - 1, num)), true);
		}

		protected override void OnLayout(LayoutEventArgs levent)
		{
			if (arranging)
			{
				return;
			}
			arranging = true;
			try
			{
				for (int i = 0; i < 3; i++)
				{
					base.OnLayout(levent);
					int num = Math.Max(1, base.ClientSize.Width - base.Padding.Horizontal);
					int num2 = GameLibraryRowLayout.HeightForWidth(num);
					int num3 = Theme.S(8);
					int num4 = ((rows.Count != 0) ? (base.Padding.Vertical - num3) : 0);
					foreach (GameLibraryRow row in rows)
					{
						num4 += num2 + row.ExtraHeightForWidth(num) + num3;
					}
					if (base.AutoScrollMinSize.Height != num4)
					{
						base.AutoScrollMinSize = new Size(0, num4);
					}
					int num5 = base.Padding.Top + base.AutoScrollPosition.Y;
					foreach (GameLibraryRow row2 in rows)
					{
						int num6 = num2 + row2.ExtraHeightForWidth(num);
						row2.SetBounds(base.Padding.Left, num5, num, num6);
						num5 += num6 + num3;
					}
					base.OnLayout(levent);
					if (num == base.ClientSize.Width - base.Padding.Horizontal && !base.HorizontalScroll.Visible)
					{
						break;
					}
				}
			}
			finally
			{
				arranging = false;
			}
		}

		protected override void OnScroll(ScrollEventArgs se)
		{
			base.OnScroll(se);
			PerformLayout();
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.PaintOnCard(e.Graphics, this, e.ClipRectangle);
				return;
			}
			using (SolidBrush brush = new SolidBrush(Theme.Card))
			{
				e.Graphics.FillRectangle(brush, e.ClipRectangle);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				tips.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
