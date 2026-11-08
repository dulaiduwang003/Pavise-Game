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
			string id = ((SelectedItem == null) ? null : SelectedItem.Profile.Id);
			int setItemsValue = -base.AutoScrollPosition.Y;
			bool matchesCount = fresh.Count != items.Count;
			if (!matchesCount)
			{
				for (int i = 0; i < fresh.Count; i++)
				{
					if (!string.Equals(fresh[i].Profile.Id, items[i].Profile.Id, StringComparison.OrdinalIgnoreCase))
					{
						matchesCount = true;
						break;
					}
				}
			}
			SuspendLayout();
			try
			{
				if (matchesCount)
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
			if (id != null)
			{
				for (int k = 0; k < items.Count; k++)
				{
					if (string.Equals(items[k].Profile.Id, id, StringComparison.OrdinalIgnoreCase))
					{
						value = k;
						break;
					}
				}
			}
			SelectIndex(value, false);
			if (matchesCount)
			{
				base.AutoScrollPosition = new Point(0, setItemsValue);
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
			bool matchesValue = value != selectedIndex;
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
			if (matchesValue && this.SelectedIndexChanged != null)
			{
				this.SelectedIndexChanged(this, EventArgs.Empty);
			}
		}

		private void HandleRowKey(GameLibraryRow row, KeyEventArgs e)
		{
			int processedCount = rows.IndexOf(row);
			switch (e.KeyCode)
			{
			case Keys.Up:
				processedCount--;
				break;
			case Keys.Down:
				processedCount++;
				break;
			case Keys.Home:
				processedCount = 0;
				break;
			case Keys.End:
				processedCount = rows.Count - 1;
				break;
			case Keys.Prior:
				processedCount -= Math.Max(1, base.ClientSize.Height / Math.Max(1, row.Height));
				break;
			case Keys.Next:
				processedCount += Math.Max(1, base.ClientSize.Height / Math.Max(1, row.Height));
				break;
			default:
				OnKeyDown(e);
				return;
			}
			e.Handled = true;
			e.SuppressKeyPress = true;
			SelectIndex(Math.Max(0, Math.Min(rows.Count - 1, processedCount)), true);
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
					int width = Math.Max(1, base.ClientSize.Width - base.Padding.Horizontal);
					int heightForWidthResult = GameLibraryRowLayout.HeightForWidth(width);
					int scaledDimension = Theme.S(8);
					int remainingVertical = ((rows.Count != 0) ? (base.Padding.Vertical - scaledDimension) : 0);
					foreach (GameLibraryRow row in rows)
					{
						remainingVertical += heightForWidthResult + row.ExtraHeightForWidth(width) + scaledDimension;
					}
					if (base.AutoScrollMinSize.Height != remainingVertical)
					{
						base.AutoScrollMinSize = new Size(0, remainingVertical);
					}
					int top = base.Padding.Top + base.AutoScrollPosition.Y;
					foreach (GameLibraryRow row2 in rows)
					{
						int height = heightForWidthResult + row2.ExtraHeightForWidth(width);
						row2.SetBounds(base.Padding.Left, top, width, height);
						top += height + scaledDimension;
					}
					base.OnLayout(levent);
					if (width == base.ClientSize.Width - base.Padding.Horizontal && !base.HorizontalScroll.Visible)
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
