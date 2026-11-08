using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameLibraryGallery : Panel
	{
		private readonly List<GameLibraryItem> items = new List<GameLibraryItem>();

		private readonly List<GameLibraryTile> cards = new List<GameLibraryTile>();

		private readonly List<int> visibleIndices = new List<int>();

		private readonly ToolTip tips = new ToolTip();

		private int selectedIndex = -1;

		private int columns = 1;

		private int preferredCardHeight = 170;

		private int cardGap = 12;

		private bool arranging;

		private bool updatingItems;

		private bool applyingFilter;

		private bool refreshingConfiguration;

		private string filterText = "";

		private Predicate<GameLibraryItem> filter;

		private Func<GameLibraryItem, Control> configurationFactory;

		private Action<GameLibraryItem, Control> configurationPresenter;

		private GameLibraryTile expandedCard;

		public Func<string, Bitmap> IconProvider;

		public Func<GameProfile, Bitmap> CoverProvider;

		public Action<Control, GameLibraryItem> ConfigurationRefresher { get; set; }

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

		public int VisibleItemCount
		{
			get
			{
				return visibleIndices.Count;
			}
		}

		public int ColumnCount
		{
			get
			{
				return columns;
			}
		}

		public GameLibraryItem ExpandedItem
		{
			get
			{
				if (expandedCard != null)
				{
					return expandedCard.Item;
				}
				return null;
			}
		}

		public Action<GameLibraryItem, Control> ConfigurationPresenter
		{
			get
			{
				return configurationPresenter;
			}
			set
			{
				if (configurationPresenter == value)
				{
					return;
				}
				CollapseConfiguration();
				configurationPresenter = value;
				foreach (GameLibraryTile card in cards)
				{
					UpdateConfigurationAction(card);
				}
				PerformLayout();
			}
		}

		public Func<GameLibraryItem, Control> ConfigurationFactory
		{
			get
			{
				return configurationFactory;
			}
			set
			{
				if (configurationFactory == value)
				{
					return;
				}
				CollapseConfiguration();
				configurationFactory = value;
				foreach (GameLibraryTile card in cards)
				{
					UpdateConfigurationAction(card);
				}
				PerformLayout();
			}
		}

		public int PreferredCardHeight
		{
			get
			{
				return preferredCardHeight;
			}
			set
			{
				int minLimit = Math.Max(128, Math.Min(170, value));
				if (preferredCardHeight != minLimit)
				{
					preferredCardHeight = minLimit;
					PerformLayout();
				}
			}
		}

		public int CardGap
		{
			get
			{
				return cardGap;
			}
			set
			{
				int minLimit = Math.Max(6, Math.Min(12, value));
				if (cardGap != minLimit)
				{
					cardGap = minLimit;
					PerformLayout();
				}
			}
		}

		public string FilterText
		{
			get
			{
				return filterText;
			}
			set
			{
				string a = (value ?? "").Trim();
				if (!string.Equals(a, filterText, StringComparison.Ordinal))
				{
					filterText = a;
					ApplyFilter(false);
				}
			}
		}

		public Predicate<GameLibraryItem> Filter
		{
			get
			{
				return filter;
			}
			set
			{
				filter = value;
				ApplyFilter(false);
			}
		}

		public event EventHandler SelectedIndexChanged;

		public event EventHandler<GameLibraryEventArgs> MoreRequested;

		public GameLibraryGallery()
		{
			DoubleBuffered = true;
			AutoScroll = true;
			base.TabStop = true;
			SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
			BackColor = Theme.Bg;
			base.Padding = new Padding(Theme.S(2));
			base.AccessibleRole = AccessibleRole.List;
			base.AccessibleName = Lang.T("nav.library");
			Native.Dark(this);
			tips.InitialDelay = 650;
			tips.ReshowDelay = 120;
			tips.AutoPopDelay = 14000;
			tips.ShowAlways = true;
		}

		private void UpdateConfigurationAction(GameLibraryTile card)
		{
			card.ConfigurationModal = configurationPresenter != null;
			card.ConfigurationAvailable = configurationPresenter != null || configurationFactory != null;
		}

		public void SetItems(List<GameLibraryItem> fresh)
		{
			if (fresh == null)
			{
				fresh = new List<GameLibraryItem>();
			}
			GameLibraryItem selectedItem = SelectedItem;
			int selectedIndexSnapshot = selectedIndex;
			string id = ((selectedItem == null || selectedItem.Profile == null) ? null : selectedItem.Profile.Id);
			int setItemsValue = -base.AutoScrollPosition.Y;
			bool matchesCount = fresh.Count != items.Count;
			if (!matchesCount)
			{
				for (int i = 0; i < fresh.Count; i++)
				{
					if (!SameIdentity(fresh[i], items[i]))
					{
						matchesCount = true;
						break;
					}
				}
			}
			SuspendLayout();
			updatingItems = true;
			try
			{
				if (matchesCount)
				{
					expandedCard = null;
					foreach (GameLibraryTile card in cards)
					{
						card.Dispose();
					}
					cards.Clear();
					items.Clear();
					foreach (GameLibraryItem item in fresh)
					{
						items.Add(item);
						GameLibraryTile gameLibraryTile = CreateCard(item, cards.Count);
						cards.Add(gameLibraryTile);
						base.Controls.Add(gameLibraryTile);
					}
				}
				else
				{
					for (int j = 0; j < fresh.Count; j++)
					{
						items[j] = fresh[j];
						cards[j].SetItem(fresh[j]);
					}
				}
				int setItemsValue2 = -1;
				if (selectedItem != null)
				{
					for (int k = 0; k < items.Count; k++)
					{
						if ((!string.IsNullOrEmpty(id) && items[k] != null && items[k].Profile != null && string.Equals(items[k].Profile.Id, id, StringComparison.OrdinalIgnoreCase)) || (id == null && object.ReferenceEquals(selectedItem, items[k])))
						{
							setItemsValue2 = k;
							break;
						}
					}
				}
				selectedIndex = setItemsValue2;
				ApplyFilter(true);
				RefreshRows();
			}
			finally
			{
				updatingItems = false;
				ResumeLayout(true);
			}
			base.AutoScrollPosition = new Point(0, setItemsValue);
			if ((selectedIndexSnapshot != selectedIndex || !SameIdentity(selectedItem, SelectedItem)) && this.SelectedIndexChanged != null)
			{
				this.SelectedIndexChanged(this, EventArgs.Empty);
			}
		}

		private static bool SameIdentity(GameLibraryItem a, GameLibraryItem b)
		{
			if (object.ReferenceEquals(a, b))
			{
				return true;
			}
			if (a == null || b == null || a.Profile == null || b.Profile == null)
			{
				return false;
			}
			if (!string.IsNullOrEmpty(a.Profile.Id))
			{
				return string.Equals(a.Profile.Id, b.Profile.Id, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private GameLibraryTile CreateCard(GameLibraryItem item, int index)
		{
			GameLibraryTile card = new GameLibraryTile(item, tips)
			{
				TabIndex = index
			};
			card.IconProvider = IconProvider;
			card.CoverProvider = CoverProvider;
			UpdateConfigurationAction(card);
			card.SelectionRequested += delegate
			{
				if (!applyingFilter && !updatingItems)
				{
					SelectIndex(cards.IndexOf(card), false);
				}
			};
			card.MoreRequested += delegate
			{
				SelectIndex(cards.IndexOf(card), false);
				if (this.MoreRequested != null)
				{
					this.MoreRequested(this, new GameLibraryEventArgs(card.Item, card.MoreButton));
				}
			};
			card.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				HandleCardKey(card, e);
			};
			card.ConfigurationRequested += delegate
			{
				ToggleConfiguration(card);
			};
			card.ConfigurationHeightChanged += delegate
			{
				if (!arranging && !updatingItems && !refreshingConfiguration)
				{
					PerformLayout();
				}
			};
			return card;
		}

		public void RefreshRows()
		{
			for (int i = 0; i < cards.Count; i++)
			{
				cards[i].IconProvider = IconProvider;
				cards[i].CoverProvider = CoverProvider;
				UpdateConfigurationAction(cards[i]);
				cards[i].Selected = i == selectedIndex;
				cards[i].RefreshModel();
			}
			RefreshConfiguration();
			Invalidate();
		}

		public void RefreshConfiguration()
		{
			if (refreshingConfiguration || expandedCard == null || expandedCard.IsDisposed || expandedCard.ConfigurationContent == null)
			{
				return;
			}
			refreshingConfiguration = true;
			try
			{
				if (ConfigurationRefresher != null)
				{
					ConfigurationRefresher(expandedCard.ConfigurationContent, expandedCard.Item);
				}
			}
			finally
			{
				refreshingConfiguration = false;
			}
			PerformLayout();
		}

		private void ToggleConfiguration(GameLibraryTile card)
		{
			if ((configurationPresenter == null && configurationFactory == null) || card == null || card.IsDisposed)
			{
				return;
			}
			int matchIndex = cards.IndexOf(card);
			if (matchIndex < 0 || !visibleIndices.Contains(matchIndex))
			{
				return;
			}
			SelectIndex(matchIndex, false);
			if (configurationPresenter != null)
			{
				configurationPresenter(card.Item, card.ConfigurationButton);
				return;
			}
			int top = card.Top;
			if (expandedCard == card)
			{
				CollapseConfiguration();
				return;
			}
			CollapseConfiguration();
			Control control = configurationFactory(card.Item);
			if (control != null)
			{
				expandedCard = card;
				card.SetConfigurationContent(control);
				RefreshConfiguration();
				PerformLayout();
				int remainingTop = card.Top - base.AutoScrollPosition.Y;
				base.AutoScrollPosition = new Point(0, Math.Max(0, remainingTop - top));
			}
		}

		private void CollapseConfiguration()
		{
			if (expandedCard != null)
			{
				GameLibraryTile gameLibraryTile = expandedCard;
				expandedCard = null;
				if (!gameLibraryTile.IsDisposed)
				{
					gameLibraryTile.SetConfigurationContent(null);
				}
				if (!arranging && !updatingItems && !applyingFilter)
				{
					PerformLayout();
				}
			}
		}

		internal bool CollapseExpandedConfiguration()
		{
			if (expandedCard == null)
			{
				return false;
			}
			GameLibraryTile gameLibraryTile = expandedCard;
			CollapseConfiguration();
			if (!gameLibraryTile.IsDisposed && gameLibraryTile.ConfigurationButton.CanFocus)
			{
				gameLibraryTile.ConfigurationButton.Focus();
			}
			return true;
		}

		private bool Matches(GameLibraryItem item)
		{
			if (item == null || item.Profile == null || (filter != null && !filter(item)))
			{
				return false;
			}
			if (filterText.Length == 0)
			{
				return true;
			}
			GameProfile profile = item.Profile;
			if (!Contains(profile.Name, filterText) && !Contains(profile.ExecutablePath, filterText))
			{
				return Contains(profile.Root, filterText);
			}
			return true;
		}

		private static bool Contains(string source, string value)
		{
			if (!string.IsNullOrEmpty(source))
			{
				return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return false;
		}

		private void ApplyFilter(bool preserveScroll)
		{
			int applyFilterValue = (preserveScroll ? (-base.AutoScrollPosition.Y) : 0);
			bool applyFilterReady = false;
			SuspendLayout();
			applyingFilter = true;
			try
			{
				visibleIndices.Clear();
				for (int i = 0; i < items.Count; i++)
				{
					bool visibleValue = Matches(items[i]);
					cards[i].Visible = visibleValue;
					if (visibleValue)
					{
						visibleIndices.Add(i);
						continue;
					}
					if (selectedIndex == i)
					{
						applyFilterReady = true;
					}
					if (expandedCard == cards[i])
					{
						CollapseConfiguration();
					}
				}
				if (applyFilterReady)
				{
					SelectIndex(-1, false);
				}
			}
			finally
			{
				applyingFilter = false;
				ResumeLayout(true);
			}
			base.AutoScrollPosition = new Point(0, applyFilterValue);
			Invalidate();
		}

		private void SelectIndex(int value, bool focus)
		{
			if (value < -1 || value >= items.Count || (value >= 0 && !visibleIndices.Contains(value)))
			{
				value = -1;
			}
			bool matchesValue = value != selectedIndex;
			selectedIndex = value;
			for (int i = 0; i < cards.Count; i++)
			{
				cards[i].Selected = i == value;
			}
			if (focus && value >= 0)
			{
				ScrollControlIntoView(cards[value]);
				cards[value].Focus();
			}
			if (matchesValue && !updatingItems && this.SelectedIndexChanged != null)
			{
				this.SelectedIndexChanged(this, EventArgs.Empty);
			}
		}

		private void HandleCardKey(GameLibraryTile card, KeyEventArgs e)
		{
			int selectedIndexSnapshot = ((card == null) ? selectedIndex : cards.IndexOf(card));
			int matchIndex = visibleIndices.IndexOf(selectedIndexSnapshot);
			int processedCount = matchIndex;
			if (e.KeyCode == Keys.F2 && e.Modifiers == Keys.None)
			{
				if (selectedIndexSnapshot >= 0)
				{
					((PillButton)cards[selectedIndexSnapshot].ConfigurationButton).PerformClick();
				}
				e.Handled = true;
				e.SuppressKeyPress = true;
				return;
			}
			if ((e.KeyCode == Keys.Apps && e.Modifiers == Keys.None) || (e.KeyCode == Keys.F10 && e.Modifiers == Keys.Shift))
			{
				if (selectedIndexSnapshot >= 0)
				{
					cards[selectedIndexSnapshot].OpenMore();
				}
				e.Handled = true;
				e.SuppressKeyPress = true;
				return;
			}
			int lowerBound = Math.Max(1, base.ClientSize.Height / Math.Max(1, Theme.S(preferredCardHeight + cardGap)));
			switch (e.KeyCode)
			{
			case Keys.Left:
				processedCount--;
				break;
			case Keys.Right:
				processedCount++;
				break;
			case Keys.Up:
				processedCount -= columns;
				break;
			case Keys.Down:
				processedCount += columns;
				break;
			case Keys.Home:
				processedCount = 0;
				break;
			case Keys.End:
				processedCount = visibleIndices.Count - 1;
				break;
			case Keys.Prior:
				processedCount -= columns * lowerBound;
				break;
			case Keys.Next:
				processedCount += columns * lowerBound;
				break;
			case Keys.Return:
			case Keys.Space:
				if (selectedIndexSnapshot < 0 && visibleIndices.Count > 0)
				{
					selectedIndexSnapshot = visibleIndices[0];
				}
				SelectIndex(selectedIndexSnapshot, true);
				e.Handled = true;
				e.SuppressKeyPress = true;
				return;
			default:
				if (card != null)
				{
					base.OnKeyDown(e);
				}
				return;
			}
			e.Handled = true;
			e.SuppressKeyPress = true;
			if (visibleIndices.Count != 0)
			{
				if (matchIndex < 0 && e.KeyCode != Keys.End)
				{
					processedCount = 0;
				}
				processedCount = Math.Max(0, Math.Min(visibleIndices.Count - 1, processedCount));
				SelectIndex(visibleIndices[processedCount], true);
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			if (IsNavigationKey(key))
			{
				return true;
			}
			return base.IsInputKey(keyData);
		}

		internal static bool IsNavigationKey(Keys key)
		{
			if (key != Keys.Left && key != Keys.Right && key != Keys.Up && key != Keys.Down && key != Keys.Home && key != Keys.End && key != Keys.Prior)
			{
				return key == Keys.Next;
			}
			return true;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			HandleCardKey(null, e);
			base.OnKeyDown(e);
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
				int scaledDimension = Theme.S(cardGap);
				int scaledDimension2 = Theme.S(238);
				int baseHeight = Theme.S(preferredCardHeight);
				for (int i = 0; i < 3; i++)
				{
					base.OnLayout(levent);
					int lowerBound = Math.Max(1, base.ClientSize.Width - base.Padding.Horizontal);
					columns = Math.Max(1, Math.Min(4, (lowerBound + scaledDimension) / Math.Max(1, scaledDimension2 + scaledDimension)));
					int width = Math.Max(1, (lowerBound - scaledDimension * (columns - 1)) / columns);
					int columnsRatio = (visibleIndices.Count + columns - 1) / columns;
					int[] integerValues = new int[columnsRatio];
					int[] integerValues2 = new int[visibleIndices.Count];
					for (int j = 0; j < visibleIndices.Count; j++)
					{
						int onLayoutValue = (integerValues2[j] = cards[visibleIndices[j]].HeightForWidth(width, baseHeight));
						int jRatio = j / columns;
						if (onLayoutValue > integerValues[jRatio])
						{
							integerValues[jRatio] = onLayoutValue;
						}
					}
					int verticalOffset = ((columnsRatio != 0) ? (base.Padding.Vertical + (columnsRatio - 1) * scaledDimension) : 0);
					int[] integerValues3 = integerValues;
					foreach (int numericValueEntry in integerValues3)
					{
						verticalOffset += numericValueEntry;
					}
					if (base.AutoScrollMinSize.Height != verticalOffset)
					{
						base.AutoScrollMinSize = new Size(0, verticalOffset);
					}
					int topOffset = base.Padding.Top + base.AutoScrollPosition.Y;
					int top = topOffset;
					for (int l = 0; l < visibleIndices.Count; l++)
					{
						int left = base.Padding.Left + l % columns * (width + scaledDimension);
						if (l > 0 && l % columns == 0)
						{
							top += integerValues[l / columns - 1] + scaledDimension;
						}
						cards[visibleIndices[l]].SetBounds(left, top, width, integerValues2[l]);
					}
					base.OnLayout(levent);
					if (lowerBound == base.ClientSize.Width - base.Padding.Horizontal && !base.HorizontalScroll.Visible)
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
			using (SolidBrush brush = new SolidBrush(Theme.Bg))
			{
				e.Graphics.FillRectangle(brush, e.ClipRectangle);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			if (visibleIndices.Count <= 0)
			{
				string textResult = ((filterText.Length > 0 || filter != null) ? PerformancePolicy.Text("没有匹配的游戏", "No matching games") : PerformancePolicy.Text("添加游戏，开始守护", "Add a game to get started"));
				Rectangle b = new Rectangle(Math.Max(0, (base.ClientSize.Width - Theme.S(38)) / 2), Math.Max(Theme.S(24), base.ClientSize.Height / 2 - Theme.S(50)), Theme.S(38), Theme.S(38));
				Glyphs.Draw(e.Graphics, "gamepad", b, Theme.Faint);
				TextRenderer.DrawText(e.Graphics, textResult, Theme.UI(10f, false), new Rectangle(Theme.S(12), b.Bottom + Theme.S(15), Math.Max(1, base.ClientSize.Width - Theme.S(24)), Theme.S(28)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				expandedCard = null;
				tips.Dispose();
			}
			base.Dispose(disposing);
		}

		internal static string SourceOf(GameProfile profile)
		{
			if (profile == null)
			{
				return "";
			}
			string source = (profile.ExecutablePath ?? "") + " " + (profile.Root ?? "");
			if (Contains(source, "wegame"))
			{
				return "WeGame";
			}
			if (Contains(source, "steamapps") || Contains(source, "steam://"))
			{
				return "Steam";
			}
			if (Contains(source, "epic games") || Contains(source, "epicgames"))
			{
				return "Epic";
			}
			if (Contains(source, "gog games") || Contains(source, "gog galaxy"))
			{
				return "GOG";
			}
			if (Contains(source, "xboxgames") || Contains(source, "windowsapps"))
			{
				return "Xbox";
			}
			return "Local";
		}

		internal static string PlatformName(GameProfile profile)
		{
			string sourceOfResult = SourceOf(profile);
			if (!(sourceOfResult == "Local"))
			{
				return sourceOfResult;
			}
			return PerformancePolicy.Text("本地游戏", "Local game");
		}
	}
}
