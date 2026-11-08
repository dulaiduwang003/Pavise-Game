using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class NavRail : FxControl
	{
		private sealed class NavigationAccessibility : FxAccessibleObject
		{
			private readonly NavRail rail;

			internal NavigationAccessibility(NavRail owner)
				: base(owner)
			{
				rail = owner;
			}

			public override int GetChildCount()
			{
				return rail.order.Length;
			}

			public override AccessibleObject GetChild(int index)
			{
				if (index < 0 || index >= rail.order.Length)
				{
					return null;
				}
				return new NavigationItem(this, rail, index);
			}
		}

		private sealed class NavigationItem : AccessibleObject
		{
			private readonly AccessibleObject parent;

			private readonly NavRail rail;

			private readonly int slot;

			public override string Name
			{
				get
				{
					return rail.labels[rail.order[slot]];
				}
				set
				{
				}
			}

			public override AccessibleRole Role
			{
				get
				{
					return AccessibleRole.PageTab;
				}
			}

			public override AccessibleObject Parent
			{
				get
				{
					return parent;
				}
			}

			public override string DefaultAction
			{
				get
				{
					return PerformancePolicy.Text("打开", "Open");
				}
			}

			public override Rectangle Bounds
			{
				get
				{
					Rectangle r = rail.ItemBounds(slot);
					if (rail.FloatingDock)
					{
						r.Intersect(rail.ClientRectangle);
					}
					else
					{
						int flowBottom = ((slot < rail.order.Length - rail.anchorCount) ? rail.FlowBottom : (rail.Height - rail.BottomPad));
						r.Intersect(new Rectangle(0, rail.TopPad, rail.Width, Math.Max(0, flowBottom - rail.TopPad)));
					}
					if (!rail.IsHandleCreated || r.Width <= 0 || r.Height <= 0)
					{
						return Rectangle.Empty;
					}
					return rail.RectangleToScreen(r);
				}
			}

			public override AccessibleStates State
			{
				get
				{
					AccessibleStates accessibleStates = AccessibleStates.Focusable | AccessibleStates.Selectable;
					if (!rail.CanInteract)
					{
						accessibleStates |= AccessibleStates.Unavailable;
					}
					if (rail.sel == rail.order[slot])
					{
						accessibleStates |= AccessibleStates.Selected;
					}
					if (rail.Focused && rail.order[slot] == ((rail.hoverIdx >= 0) ? rail.hoverIdx : rail.sel))
					{
						accessibleStates |= AccessibleStates.Focused;
					}
					if (!rail.Visible || Bounds.IsEmpty)
					{
						accessibleStates |= AccessibleStates.Offscreen;
					}
					return accessibleStates;
				}
			}

			internal NavigationItem(AccessibleObject parent, NavRail rail, int slot)
			{
				this.parent = parent;
				this.rail = rail;
				this.slot = slot;
			}

			public override void DoDefaultAction()
			{
				rail.KeepItemVisible(rail.order[slot]);
				rail.Focus();
				rail.InvokeItem(rail.order[slot]);
			}
		}

		private const float NormalDockPointSize = 9.5f;

		private const float CompactDockPointSize = 9.5f;

		private readonly string[] labels;

		private readonly string[] glyphs;

		private readonly int[] order;

		private readonly int[] groupSlots;

		private readonly string[] groupTexts;

		private readonly int anchorCount;

		private int sel;

		private int hoverIdx = -1;

		private Motion indicator;

		private Motion indicatorCross;

		private float measuredDockScale = -1f;

		private int preferredDockWidth;

		private int minimumDockWidth;

		private int[] normalDockCells;

		private int[] compactDockCells;

		private Font normalDockFont;

		private Font compactDockFont;

		private float dockTypographyScale = -1f;

		private Image logo;

		private readonly ToolTip itemTip = new ToolTip();

		private int scrollOffset;

		private bool showBranding = true;

		private PerformancePreset mode;

		private bool modeEnabled = true;

		public Action<int> SelectionChanged;

		public Action<int> ItemInvoked;

		public string SectionTitle;

		public bool ShowBranding
		{
			get
			{
				return showBranding;
			}
			set
			{
				if (showBranding != value)
				{
					showBranding = value;
					RefreshLogo();
				}
			}
		}

		public int Selected
		{
			get
			{
				return sel;
			}
		}

		internal int ItemCount
		{
			get
			{
				return labels.Length;
			}
		}

		private bool FloatingDock
		{
			get
			{
				if (base.Width > base.Height)
				{
					return base.Height > 0;
				}
				return false;
			}
		}

		private int DockColumns
		{
			get
			{
				return DockColumnCount(base.Width);
			}
		}

		private int DockRows
		{
			get
			{
				return Math.Max(1, (order.Length + DockColumns - 1) / DockColumns);
			}
		}

		private float DockFontSize
		{
			get
			{
				EnsureDockMeasurements();
				if (base.Width < preferredDockWidth)
				{
					return 9.5f;
				}
				return 9.5f;
			}
		}

		private Rectangle DockBounds
		{
			get
			{
				int maxLimit = Math.Min(Dpi.S(5), Math.Max(0, base.Width / 8));
				int val = Dpi.S(DockRows * 64 + (DockRows - 1) * 4);
				int maxLimit2 = Math.Min(val, Math.Max(1, base.Height - Dpi.S(12)));
				return new Rectangle(maxLimit, Dpi.S(3), Math.Max(1, base.Width - maxLimit * 2 - 1), maxLimit2);
			}
		}

		private bool CompactRail
		{
			get
			{
				if (base.Width > 0)
				{
					return base.Width < Dpi.S(210);
				}
				return false;
			}
		}

		private bool IconOnly
		{
			get
			{
				if (base.Width > 0)
				{
					return base.Width < Dpi.S(160);
				}
				return false;
			}
		}

		private bool TightHeight
		{
			get
			{
				if (base.Height > 0)
				{
					return base.Height < Dpi.S(470);
				}
				return false;
			}
		}

		private bool CompactHeight
		{
			get
			{
				if (base.Height <= 0 || order == null || order.Length == 0)
				{
					return false;
				}
				int sResult = Dpi.S(137 + order.Length * 47 + Math.Max(0, order.Length - 1) * 13 + GroupsAbove(order.Length - 1) * 42 + 48);
				return base.Height < sResult;
			}
		}

		private int TopPad
		{
			get
			{
				return Dpi.S((!TightHeight) ? ((!CompactHeight) ? 137 : (showBranding ? 80 : 104)) : (showBranding ? 8 : 104));
			}
		}

		private int ItemH
		{
			get
			{
				if (!FloatingDock)
				{
					return Dpi.S(CompactHeight ? 44 : 47);
				}
				return Math.Max(1, (DockBounds.Height - Dpi.S(8 + (DockRows - 1) * 4)) / DockRows);
			}
		}

		private int Gap
		{
			get
			{
				return Dpi.S((!TightHeight) ? (CompactHeight ? 4 : 13) : 0);
			}
		}

		private int Pad
		{
			get
			{
				return Dpi.S(CompactRail ? 10 : 14);
			}
		}

		private int Pitch
		{
			get
			{
				return ItemH + Gap;
			}
		}

		private int GroupH
		{
			get
			{
				return Dpi.S((!TightHeight) ? (CompactHeight ? 12 : 42) : 0);
			}
		}

		private int BottomPad
		{
			get
			{
				return Dpi.S(TightHeight ? 8 : (CompactHeight ? 46 : 24));
			}
		}

		private int FlowBottom
		{
			get
			{
				return base.Height - BottomPad - anchorCount * Pitch;
			}
		}

		private int ScrollMaximum
		{
			get
			{
				if (FloatingDock)
				{
					return 0;
				}
				int remainingLength = ((order != null) ? (order.Length - anchorCount) : 0);
				if (remainingLength <= 0)
				{
					return 0;
				}
				return Math.Max(0, TopPad + remainingLength * ItemH + (remainingLength - 1) * Gap + GroupsAbove(remainingLength - 1) * GroupH - FlowBottom);
			}
		}

		internal float DockLabelPointSize
		{
			get
			{
				return DockFontSize;
			}
		}

		internal Font DockLabelFont
		{
			get
			{
				EnsureDockMeasurements();
				if (base.Width < preferredDockWidth)
				{
					return compactDockFont;
				}
				return normalDockFont;
			}
		}

		private bool CanAnimateIndicator
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

		internal int ItemAtSlot(int slot)
		{
			if (slot < 0 || slot >= order.Length)
			{
				return -1;
			}
			return order[slot];
		}

		internal Size MeasureFloatingDock(int availableWidth)
		{
			EnsureDockMeasurements();
			int minLimit = Math.Max(1, Math.Min(availableWidth, preferredDockWidth));
			int dockColumnCountResult = DockColumnCount(minLimit);
			int lowerBound = Math.Max(1, (order.Length + dockColumnCountResult - 1) / dockColumnCountResult);
			return new Size(minLimit, Dpi.S(lowerBound * 64 + (lowerBound - 1) * 4 + 12));
		}

		private void EnsureDockMeasurements()
		{
			if (measuredDockScale != Dpi.Scale || preferredDockWidth <= 0)
			{
				EnsureDockTypography();
				measuredDockScale = Dpi.Scale;
				normalDockCells = new int[order.Length];
				compactDockCells = new int[order.Length];
				int ensureDockMeasurementsValue = 0;
				int ensureDockMeasurementsValue2 = 0;
				for (int i = 0; i < order.Length; i++)
				{
					string message = labels[order[i]] ?? "";
					normalDockCells[i] = Math.Max(Dpi.S(88), TextRenderer.MeasureText(message, normalDockFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Dpi.S(20));
					compactDockCells[i] = Math.Max(Dpi.S(82), TextRenderer.MeasureText(message, compactDockFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Dpi.S(20));
					ensureDockMeasurementsValue += normalDockCells[i];
					ensureDockMeasurementsValue2 += compactDockCells[i];
				}
				int groupsAboveResult = GroupsAbove(Math.Max(0, order.Length - 1));
				int sResult = Dpi.S(22 + Math.Max(0, order.Length - 1) * 4 + groupsAboveResult * 18);
				preferredDockWidth = Math.Max(Dpi.S(240), ensureDockMeasurementsValue + sResult);
				minimumDockWidth = Math.Max(Dpi.S(240), ensureDockMeasurementsValue2 + sResult);
			}
		}

		private void EnsureDockTypography()
		{
			if (dockTypographyScale == Dpi.Scale && normalDockFont != null && compactDockFont != null)
			{
				return;
			}
			DropDockTypography();
			bool ensureDockTypographyReady = false;
			string[] labelsSnapshot = labels;
			foreach (string entryText in labelsSnapshot)
			{
				if (entryText == null)
				{
					continue;
				}
				string message = entryText;
				foreach (char c in message)
				{
					if (c >= '㐀' && c <= '\u9fff')
					{
						ensureDockTypographyReady = true;
						break;
					}
				}
				if (ensureDockTypographyReady)
				{
					break;
				}
			}
			string[] textEntries = (ensureDockTypographyReady ? new string[3] { "DengXian", "Microsoft YaHei UI", "Segoe UI" } : new string[3] { "Bahnschrift SemiBold", "Segoe UI Semibold", "Segoe UI" });
			string[] textEntries2 = textEntries;
			foreach (string name in textEntries2)
			{
				try
				{
					using (FontFamily fontFamily = new FontFamily(name))
					{
						FontStyle style = ((ensureDockTypographyReady && fontFamily.IsStyleAvailable(FontStyle.Bold)) ? FontStyle.Bold : FontStyle.Regular);
						float ratio = Math.Max(0.01f, Dpi.Scale) * 96f / 72f;
						normalDockFont = new Font(fontFamily, 9.5f * ratio, style, GraphicsUnit.Pixel);
						compactDockFont = new Font(fontFamily, 9.5f * ratio, style, GraphicsUnit.Pixel);
					}
				}
				catch (ArgumentException)
				{
					DropDockTypography();
					continue;
				}
				break;
			}
			if (normalDockFont == null)
			{
				float ratio2 = Math.Max(0.01f, Dpi.Scale) * 96f / 72f;
				normalDockFont = new Font(FontFamily.GenericSansSerif, 9.5f * ratio2, FontStyle.Bold, GraphicsUnit.Pixel);
				compactDockFont = new Font(FontFamily.GenericSansSerif, 9.5f * ratio2, FontStyle.Bold, GraphicsUnit.Pixel);
			}
			dockTypographyScale = Dpi.Scale;
		}

		private void DropDockTypography()
		{
			if (normalDockFont != null)
			{
				normalDockFont.Dispose();
				normalDockFont = null;
			}
			if (compactDockFont != null)
			{
				compactDockFont.Dispose();
				compactDockFont = null;
			}
			dockTypographyScale = -1f;
		}

		private int DockColumnCount(int width)
		{
			EnsureDockMeasurements();
			int lowerBound = Math.Max(1, (order == null) ? 1 : order.Length);
			if (width < minimumDockWidth && lowerBound > 4)
			{
				return (lowerBound + 1) / 2;
			}
			return lowerBound;
		}

		public NavRail(string[] names, string[] icons)
			: this(names, icons, null, null, null, 0)
		{
		}

		public NavRail(string[] names, string[] icons, int[] displayOrder, int[] groupBeforeSlots, string[] groupTitles, int bottomAnchored)
		{
			labels = names;
			glyphs = icons;
			if (displayOrder != null && displayOrder.Length > 0 && displayOrder.Length <= names.Length)
			{
				order = displayOrder;
			}
			else
			{
				order = new int[names.Length];
				for (int i = 0; i < names.Length; i++)
				{
					order[i] = i;
				}
			}
			if (groupTitles != null && groupBeforeSlots != null && groupTitles.Length == groupBeforeSlots.Length)
			{
				groupSlots = groupBeforeSlots;
				groupTexts = groupTitles;
			}
			else
			{
				groupSlots = new int[0];
				groupTexts = new string[0];
			}
			anchorCount = ((bottomAnchored >= 0) ? ((bottomAnchored > order.Length) ? order.Length : bottomAnchored) : 0);
			Cursor = Cursors.Default;
			base.TabStop = true;
			base.AccessibleRole = AccessibleRole.PageTabList;
			base.AccessibleName = PerformancePolicy.Text("导航", "Navigation");
			indicator.Speed = 0.4f;
			indicatorCross.Speed = 0.4f;
			indicator.Set(SlotY(SlotOfItem(sel)));
			logo = IconArt.Render(Dpi.S(46), mode, modeEnabled);
		}

		internal static int GroupsAbove(int slot, int[] groupBeforeSlots)
		{
			if (groupBeforeSlots == null)
			{
				return 0;
			}
			int processedCount = 0;
			for (int i = 0; i < groupBeforeSlots.Length; i++)
			{
				if (slot >= groupBeforeSlots[i])
				{
					processedCount++;
				}
			}
			return processedCount;
		}

		private int GroupsAbove(int slot)
		{
			return GroupsAbove(slot, groupSlots);
		}

		private int SlotOfItem(int item)
		{
			for (int i = 0; i < order.Length; i++)
			{
				if (order[i] == item)
				{
					return i;
				}
			}
			return -1;
		}

		private int SlotY(int slot)
		{
			if (FloatingDock)
			{
				return ItemBounds(Math.Max(0, slot)).X;
			}
			if (slot < 0)
			{
				return TopPad;
			}
			int remainingLength = order.Length - anchorCount;
			if (slot >= remainingLength && base.Height > 0)
			{
				return base.Height - BottomPad - ItemH - (order.Length - 1 - slot) * Pitch;
			}
			return TopPad + slot * Pitch + GroupsAbove(slot) * GroupH - scrollOffset;
		}

		private Rectangle ItemBounds(int slot)
		{
			if (!FloatingDock)
			{
				return new Rectangle(Pad, SlotY(slot), Math.Max(1, base.Width - Pad * 2), ItemH);
			}
			Rectangle dockBounds = DockBounds;
			int orderCount = ((order != null) ? order.Length : 0);
			if (orderCount == 0 || slot < 0 || slot >= orderCount)
			{
				return Rectangle.Empty;
			}
			int dockColumns = DockColumns;
			int slotRatio = slot / dockColumns;
			int slotOffset = slot % dockColumns;
			int scaledSlotRatio = slotRatio * dockColumns;
			int dockColumnsLimit = Math.Min(dockColumns, orderCount - scaledSlotRatio);
			int sResult = Dpi.S(6);
			int sResult2 = Dpi.S(4);
			int sResult3 = Dpi.S(18);
			int groupsAboveResult = GroupsAbove(scaledSlotRatio);
			int remainingGroupsAbove = GroupsAbove(scaledSlotRatio + dockColumnsLimit - 1) - groupsAboveResult;
			int remainingGroupsAbove2 = GroupsAbove(slot) - groupsAboveResult;
			int dockColumnsLimitLimit = Math.Max(dockColumnsLimit, dockBounds.Width - sResult * 2 - sResult2 * (dockColumnsLimit - 1) - remainingGroupsAbove * sResult3);
			int[] normalDockCellsSnapshot = ((base.Width >= preferredDockWidth) ? normalDockCells : compactDockCells);
			int itemBoundsValue = 0;
			int itemBoundsValue2 = 0;
			for (int i = 0; i < dockColumnsLimit; i++)
			{
				int itemBoundsValue3 = normalDockCellsSnapshot[scaledSlotRatio + i];
				itemBoundsValue += itemBoundsValue3;
				if (i < slotOffset)
				{
					itemBoundsValue2 += itemBoundsValue3;
				}
			}
			int itemBoundsValue4 = dockBounds.Left + sResult + (int)Math.Round((double)dockColumnsLimitLimit * (double)itemBoundsValue2 / (double)Math.Max(1, itemBoundsValue)) + sResult2 * slotOffset + remainingGroupsAbove2 * sResult3;
			int itemBoundsValue5 = dockBounds.Left + sResult + (int)Math.Round((double)dockColumnsLimitLimit * (double)(itemBoundsValue2 + normalDockCellsSnapshot[slot]) / (double)Math.Max(1, itemBoundsValue)) + sResult2 * slotOffset + remainingGroupsAbove2 * sResult3;
			return new Rectangle(itemBoundsValue4, dockBounds.Top + Dpi.S(4) + slotRatio * (ItemH + Dpi.S(4)), Math.Max(1, itemBoundsValue5 - itemBoundsValue4), ItemH);
		}

		internal Rectangle DockIconBounds(int slot)
		{
			Rectangle rectangle = ItemBounds(slot);
			int sResult = Dpi.S(20);
			return new Rectangle(rectangle.Left + (rectangle.Width - sResult) / 2, rectangle.Top + Dpi.S(7), sResult, sResult);
		}

		internal Rectangle DockLabelBounds(int slot)
		{
			Rectangle rectangle = ItemBounds(slot);
			return new Rectangle(rectangle.Left + Dpi.S(5), rectangle.Top + Dpi.S(32), Math.Max(1, rectangle.Width - Dpi.S(10)), Math.Max(1, rectangle.Height - Dpi.S(34)));
		}

		private void KeepItemVisible(int item)
		{
			if (FloatingDock)
			{
				scrollOffset = 0;
				return;
			}
			int slotOfItemResult = SlotOfItem(item);
			if (slotOfItemResult >= 0 && slotOfItemResult < order.Length - anchorCount)
			{
				int slotYResult = SlotY(slotOfItemResult);
				if (slotYResult < TopPad)
				{
					scrollOffset += slotYResult - TopPad;
				}
				else if (slotYResult + ItemH > FlowBottom)
				{
					scrollOffset += slotYResult + ItemH - FlowBottom;
				}
			}
			scrollOffset = Math.Max(0, Math.Min(scrollOffset, ScrollMaximum));
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			SnapToSelection();
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			SnapToSelection();
			if (logo != null)
			{
				try
				{
					logo.Dispose();
				}
				catch
				{
				}
				logo = null;
			}
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
			if (order != null)
			{
				KeepItemVisible(sel);
			}
			SnapToSelection();
			UpdateDockRegion();
		}

		private void UpdateDockRegion()
		{
			if (order == null)
			{
				return;
			}
			Region region = base.Region;
			if (FloatingDock)
			{
				Rectangle dockBounds = DockBounds;
				dockBounds.Inflate(Dpi.S(4), Dpi.S(3));
				dockBounds.Height += Dpi.S(5);
				using (GraphicsPath path = DockPath(dockBounds, Dpi.S(16)))
				{
					base.Region = new Region(path);
				}
			}
			else
			{
				base.Region = null;
			}
			if (region != null)
			{
				region.Dispose();
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				itemTip.Dispose();
				DropDockTypography();
			}
			base.Dispose(disposing);
		}

		public void Select(int i)
		{
			if (i >= 0 && i < labels.Length)
			{
				SelectSilently(i);
				if (SelectionChanged != null)
				{
					SelectionChanged(i);
				}
			}
		}

		internal void SelectSilently(int i)
		{
			if (i < 0 || i >= labels.Length)
			{
				return;
			}
			bool matchesSel = sel != i;
			sel = i;
			KeepItemVisible(i);
			int slotOfItemResult = SlotOfItem(i);
			int slotYResult = SlotY(slotOfItemResult);
			int top = ((FloatingDock && slotOfItemResult >= 0) ? ItemBounds(slotOfItemResult).Y : 0);
			if (matchesSel && slotOfItemResult >= 0 && CanAnimateIndicator)
			{
				indicator.To(slotYResult);
				indicatorCross.To(top);
				UiClock.Wake();
			}
			else
			{
				if (!CanAnimateIndicator || indicator.Target != (float)slotYResult)
				{
					indicator.Set(slotYResult);
				}
				if (!CanAnimateIndicator || indicatorCross.Target != (float)top)
				{
					indicatorCross.Set(top);
				}
			}
			Invalidate();
		}

		internal void InvokeItem(int i)
		{
			if (base.CanInteract && SlotOfItem(i) >= 0)
			{
				if (ItemInvoked != null)
				{
					ItemInvoked(i);
				}
				else
				{
					Select(i);
				}
			}
		}

		public void SnapToSelection()
		{
			if (order != null)
			{
				indicator.Set(SlotY(SlotOfItem(sel)));
				int slotOfItemResult = SlotOfItem(sel);
				indicatorCross.Set((FloatingDock && slotOfItemResult >= 0) ? ItemBounds(slotOfItemResult).Y : 0);
				Invalidate();
			}
		}

		public void SetMode(PerformancePreset value, bool enabled)
		{
			if (mode != value || modeEnabled != enabled)
			{
				mode = value;
				modeEnabled = enabled;
				RefreshLogo();
			}
		}

		public void RefreshLogo()
		{
			Image image = logo;
			logo = (showBranding ? IconArt.Render(Dpi.S(46), mode, modeEnabled) : null);
			if (image != null)
			{
				image.Dispose();
			}
			Invalidate();
		}

		protected override bool StepAll()
		{
			bool stepAllSucceeded = base.StepAll();
			int slotOfItemResult = SlotOfItem(sel);
			int slotYResult = SlotY(slotOfItemResult);
			int top = ((FloatingDock && slotOfItemResult >= 0) ? ItemBounds(slotOfItemResult).Y : 0);
			if (!CanAnimateIndicator || indicator.Target != (float)slotYResult)
			{
				bool anyConditionMet = indicator.Value != (float)slotYResult || indicator.Target != (float)slotYResult;
				indicator.Set(slotYResult);
				stepAllSucceeded = stepAllSucceeded || anyConditionMet;
			}
			if (!CanAnimateIndicator || indicatorCross.Target != (float)top)
			{
				stepAllSucceeded |= indicatorCross.Value != (float)top || indicatorCross.Target != (float)top;
				indicatorCross.Set(top);
			}
			if (indicator.Value != indicator.Target)
			{
				stepAllSucceeded |= indicator.Step();
				if (Math.Abs(indicator.Value - indicator.Target) < 0.75f)
				{
					indicator.Set(indicator.Target);
				}
			}
			if (indicatorCross.Value != indicatorCross.Target)
			{
				stepAllSucceeded |= indicatorCross.Step();
				if (Math.Abs(indicatorCross.Value - indicatorCross.Target) < 0.75f)
				{
					indicatorCross.Set(indicatorCross.Target);
				}
			}
			return stepAllSucceeded;
		}

		private int HitTest(int y)
		{
			for (int i = 0; i < order.Length; i++)
			{
				if (i >= order.Length - anchorCount || (y >= TopPad && y < FlowBottom))
				{
					int slotYResult = SlotY(i);
					if (y >= slotYResult && y < slotYResult + ItemH)
					{
						return order[i];
					}
				}
			}
			return -1;
		}

		private int HitTestPoint(Point point)
		{
			if (!FloatingDock)
			{
				return HitTest(point.Y);
			}
			for (int i = 0; i < order.Length; i++)
			{
				if (ItemBounds(i).Contains(point))
				{
					return order[i];
				}
			}
			return -1;
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			int hitTestPointResult = HitTestPoint(e.Location);
			Cursor = ((hitTestPointResult >= 0) ? Cursors.Hand : Cursors.Default);
			if (hitTestPointResult != hoverIdx)
			{
				hoverIdx = hitTestPointResult;
				itemTip.SetToolTip(this, (hitTestPointResult >= 0) ? labels[hitTestPointResult] : null);
				Invalidate();
			}
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			base.OnMouseLeave(e);
			if (hoverIdx != -1)
			{
				hoverIdx = -1;
				Invalidate();
			}
		}

		protected override void OnMouseClick(MouseEventArgs e)
		{
			base.OnMouseClick(e);
			if (e.Button == MouseButtons.Left)
			{
				int hitTestPointResult = HitTestPoint(e.Location);
				if (hitTestPointResult >= 0)
				{
					Focus();
					InvokeItem(hitTestPointResult);
				}
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			Keys keys = keyData & Keys.KeyCode;
			if (keys != Keys.Left && keys != Keys.Right && keys != Keys.Up && keys != Keys.Down && keys != Keys.Home && keys != Keys.End)
			{
				return base.IsInputKey(keyData);
			}
			return true;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled || e.Modifiers != 0 || !base.CanInteract)
			{
				return;
			}
			int slotOfItemResult = SlotOfItem((hoverIdx >= 0) ? hoverIdx : sel);
			if (slotOfItemResult < 0)
			{
				slotOfItemResult = 0;
			}
			int dockColumnsSnapshot = ((!FloatingDock || DockRows <= 1) ? 1 : DockColumns);
			if (e.KeyCode == Keys.Up)
			{
				slotOfItemResult = Math.Max(0, slotOfItemResult - dockColumnsSnapshot);
			}
			else if (e.KeyCode == Keys.Down)
			{
				slotOfItemResult = Math.Min(order.Length - 1, slotOfItemResult + dockColumnsSnapshot);
			}
			else if (e.KeyCode == Keys.Left)
			{
				slotOfItemResult = Math.Max(0, slotOfItemResult - 1);
			}
			else if (e.KeyCode == Keys.Right)
			{
				slotOfItemResult = Math.Min(order.Length - 1, slotOfItemResult + 1);
			}
			else if (e.KeyCode == Keys.Home)
			{
				slotOfItemResult = 0;
			}
			else if (e.KeyCode == Keys.End)
			{
				slotOfItemResult = order.Length - 1;
			}
			else
			{
				if (e.KeyCode != Keys.Return && e.KeyCode != Keys.Space)
				{
					return;
				}
				InvokeItem(order[slotOfItemResult]);
			}
			hoverIdx = order[slotOfItemResult];
			KeepItemVisible(hoverIdx);
			Invalidate();
			e.Handled = true;
			e.SuppressKeyPress = true;
		}

		protected override void OnMouseWheel(MouseEventArgs e)
		{
			base.OnMouseWheel(e);
			if (ScrollMaximum > 0 && e.Delta != 0)
			{
				scrollOffset = Math.Max(0, Math.Min(ScrollMaximum, scrollOffset - Math.Sign(e.Delta) * Pitch));
				hoverIdx = -1;
				itemTip.SetToolTip(this, null);
				SnapToSelection();
				HandledMouseEventArgs handledMouseEventArgs = e as HandledMouseEventArgs;
				if (handledMouseEventArgs != null)
				{
					handledMouseEventArgs.Handled = true;
				}
			}
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new NavigationAccessibility(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			if (FloatingDock)
			{
				PaintDock(graphics);
				return;
			}
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(graphics, this, base.ClientRectangle);
				using (SolidBrush brush = new SolidBrush(Backdrop.NavFill(this, Theme.Nav)))
				{
					graphics.FillRectangle(brush, base.ClientRectangle);
				}
			}
			else if (Theme.LightMode)
			{
				using (LinearGradientBrush brush2 = new LinearGradientBrush(base.ClientRectangle, Theme.Nav, Col.Lerp(Theme.Nav, Theme.Bg, 0.58f), LinearGradientMode.Horizontal))
				{
					graphics.FillRectangle(brush2, base.ClientRectangle);
				}
			}
			else
			{
				using (SolidBrush brush3 = new SolidBrush(Theme.Nav))
				{
					graphics.FillRectangle(brush3, base.ClientRectangle);
				}
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			DrawTechTexture(graphics);
			using (Pen pen = new Pen(Theme.Accent, Math.Max(1f, Dpi.S(1))))
			{
				graphics.DrawLine(pen, 0, 0, base.Width, 0);
			}
			if (showBranding && !TightHeight)
			{
				if (logo != null)
				{
					graphics.DrawImage(logo, IconOnly ? ((base.Width - Dpi.S(46)) / 2) : (CompactRail ? Dpi.S(12) : Dpi.S(20)), Dpi.S(26), Dpi.S(46), Dpi.S(46));
				}
				if (!CompactRail)
				{
					TextRenderer.DrawText(graphics, "PAVISE", Theme.UI(16.5f, true), new Rectangle(Dpi.S(78), Dpi.S(24), base.Width - Dpi.S(84), Dpi.S(30)), Theme.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				}
				if (!CompactRail)
				{
					TextRenderer.DrawText(graphics, "AUTO GUARD " + App.Version, Theme.Mono(7.5f), new Rectangle(Dpi.S(79), Dpi.S(57), base.Width - Dpi.S(84), Dpi.S(18)), Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				}
			}
			if (!string.IsNullOrEmpty(SectionTitle) && !TightHeight && !IconOnly)
			{
				TextRenderer.DrawText(graphics, SectionTitle, Theme.UI(10f, true), new Rectangle(Pad + Dpi.S(12), TopPad - Dpi.S(34), Math.Max(1, base.Width - Pad * 2 - Dpi.S(24)), Dpi.S(24)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			}
			using (Pen pen2 = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen2, base.Width - 1, 0, base.Width - 1, base.Height);
			}
			GraphicsState gstate = graphics.Save();
			graphics.SetClip(new Rectangle(0, TopPad, base.Width, Math.Max(0, base.Height - TopPad - BottomPad)));
			int slotOfItemResult = SlotOfItem(sel);
			int slotYResult = SlotY(slotOfItemResult);
			if (!CanAnimateIndicator || indicator.Target != (float)slotYResult)
			{
				indicator.Set(slotYResult);
			}
			if (slotOfItemResult >= 0)
			{
				int roundedValue = (int)Math.Round(indicator.Value);
				Rectangle r = new Rectangle(Pad, roundedValue, base.Width - Pad * 2, ItemH);
				using (GraphicsPath path = Theme.TechPath(r, Dpi.S(8)))
				{
					using (SolidBrush brush4 = new SolidBrush(Col.Alpha(Theme.Accent, Theme.LightMode ? 38 : 22)))
					{
						graphics.FillPath(brush4, path);
					}
					using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 124 : 76)))
					{
						graphics.DrawPath(pen3, path);
					}
				}
				Rectangle r2 = new Rectangle(Pad, roundedValue + Dpi.S(12), Dpi.S(4), ItemH - Dpi.S(24));
				using (GraphicsPath path2 = Theme.Rounded(r2, Dpi.S(1)))
				{
					using (SolidBrush brush5 = new SolidBrush(Theme.Accent))
					{
						graphics.FillPath(brush5, path2);
					}
				}
			}
			for (int i = 0; i < groupSlots.Length; i++)
			{
				if (GroupH == 0)
				{
					break;
				}
				int onPaintValue = groupSlots[i];
				if (onPaintValue < 0 || onPaintValue >= order.Length)
				{
					continue;
				}
				string message = groupTexts[i] ?? "";
				int remainingSlotY = SlotY(onPaintValue) - GroupH;
				Font font = Theme.MonoFor(message, 6.5f);
				int width = TextRenderer.MeasureText(graphics, message, font).Width;
				int top = remainingSlotY + GroupH / 2;
				using (Pen pen4 = new Pen(Theme.Stroke))
				{
					if (message.Length > 0)
					{
						TextRenderer.DrawText(graphics, message, font, new Rectangle(Pad + Dpi.S(13), remainingSlotY, base.Width - Pad * 2 - Dpi.S(13), GroupH), Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
						graphics.DrawLine(pen4, Pad + Dpi.S(17) + width + Dpi.S(8), top, base.Width - Pad - Dpi.S(2), top);
					}
					else
					{
						graphics.DrawLine(pen4, Pad + Dpi.S(6), top, base.Width - Pad - Dpi.S(6), top);
					}
				}
			}
			for (int j = 0; j < order.Length; j++)
			{
				int onPaintValue2 = order[j];
				int slotYResult2 = SlotY(j);
				if (slotYResult2 + ItemH <= TopPad || slotYResult2 >= base.Height - BottomPad)
				{
					continue;
				}
				bool matchesOnPaintValue2 = onPaintValue2 == sel;
				if (!matchesOnPaintValue2 && onPaintValue2 == hoverIdx)
				{
					Rectangle r3 = new Rectangle(Pad, slotYResult2, base.Width - Pad * 2, ItemH);
					using (SolidBrush brush6 = new SolidBrush(Col.Alpha(Theme.Fg, Theme.LightMode ? 16 : 9)))
					{
						using (GraphicsPath path3 = Theme.TechPath(r3, Dpi.S(8)))
						{
							graphics.FillPath(brush6, path3);
						}
					}
				}
				Color color = ((!matchesOnPaintValue2) ? ((onPaintValue2 == hoverIdx) ? Theme.Fg : Theme.Dim) : (Theme.LightMode ? Theme.Accent : Color.White));
				Rectangle b = new Rectangle(IconOnly ? ((base.Width - Dpi.S(20)) / 2) : (Pad + Dpi.S(CompactRail ? 12 : 20)), slotYResult2 + (ItemH - Dpi.S(20)) / 2, Dpi.S(20), Dpi.S(20));
				Glyphs.Draw(graphics, glyphs[onPaintValue2], b, matchesOnPaintValue2 ? Theme.Accent : color);
				if (!IconOnly)
				{
					int rightOffset = b.Right + Dpi.S(CompactRail ? 10 : 16);
					Rectangle bounds = new Rectangle(rightOffset, slotYResult2, base.Width - Pad - Dpi.S(10) - rightOffset, ItemH);
					Font font2 = Theme.UI(11f, matchesOnPaintValue2);
					TextRenderer.DrawText(graphics, labels[onPaintValue2], font2, bounds, color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
				}
			}
			graphics.Restore(gstate);
			if (ScrollMaximum <= 0)
			{
				return;
			}
			int lowerBound = Math.Max(1, FlowBottom - TopPad);
			int height = Math.Min(lowerBound, Math.Max(Dpi.S(18), lowerBound * lowerBound / (lowerBound + ScrollMaximum)));
			int top2 = TopPad + (lowerBound - height) * scrollOffset / Math.Max(1, ScrollMaximum);
			using (SolidBrush brush7 = new SolidBrush(Theme.Faint))
			{
				graphics.FillRectangle(brush7, base.Width - Dpi.S(4), top2, Math.Max(1, Dpi.S(2)), height);
			}
		}

		private void DrawTechTexture(Graphics g)
		{
			RogSurface.Draw(g, base.ClientRectangle, true);
		}

		private static GraphicsPath DockPath(Rectangle bounds, int cut)
		{
			int minLimit = Math.Max(0, Math.Min(cut, Math.Min(bounds.Width, bounds.Height) / 2));
			GraphicsPath graphicsPath = new GraphicsPath();
			graphicsPath.AddPolygon(new Point[8]
			{
				new Point(bounds.Left + minLimit, bounds.Top),
				new Point(bounds.Right - minLimit, bounds.Top),
				new Point(bounds.Right, bounds.Top + minLimit),
				new Point(bounds.Right, bounds.Bottom - minLimit),
				new Point(bounds.Right - minLimit, bounds.Bottom),
				new Point(bounds.Left + minLimit, bounds.Bottom),
				new Point(bounds.Left, bounds.Bottom - minLimit),
				new Point(bounds.Left, bounds.Top + minLimit)
			});
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		private void PaintDock(Graphics g)
		{
			WorkspaceCanvasSurface.Paint(g, this, base.ClientRectangle);
			g.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle dockBounds = DockBounds;
			if (dockBounds.Width < 2 || dockBounds.Height < 2)
			{
				return;
			}
			Color accent = Theme.Accent;
			Rectangle bounds = dockBounds;
			bounds.Offset(0, Dpi.S(4));
			using (GraphicsPath path = DockPath(bounds, Dpi.S(13)))
			{
				using (SolidBrush brush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
				{
					g.FillPath(brush, path);
				}
			}
			int sResult = Dpi.S(13);
			Point[] points = new Point[4]
			{
				new Point(dockBounds.Left, dockBounds.Bottom - sResult),
				new Point(dockBounds.Left + sResult, dockBounds.Bottom),
				new Point(dockBounds.Right - sResult, dockBounds.Bottom),
				new Point(dockBounds.Right, dockBounds.Bottom - sResult)
			};
			using (Pen pen = new Pen(Col.Alpha(accent, Theme.LightMode ? 5 : 9), Dpi.S(7)))
			{
				g.DrawLines(pen, points);
			}
			using (Pen pen2 = new Pen(Col.Alpha(accent, Theme.LightMode ? 9 : 17), Dpi.S(3)))
			{
				g.DrawLines(pen2, points);
			}
			Rectangle bounds2 = dockBounds;
			bounds2.Inflate(Dpi.S(2), Dpi.S(2));
			using (GraphicsPath path2 = DockPath(bounds2, Dpi.S(15)))
			{
				using (Pen pen3 = new Pen(Col.Alpha(accent, Theme.LightMode ? 24 : 30)))
				{
					g.DrawPath(pen3, path2);
				}
			}
			using (GraphicsPath path3 = DockPath(dockBounds, sResult))
			{
				Color color = (Theme.LightMode ? Color.FromArgb(246, 248, 251) : Color.FromArgb(21, 23, 28));
				Color color2 = (Theme.LightMode ? Color.FromArgb(224, 230, 237) : Color.FromArgb(11, 13, 17));
				using (LinearGradientBrush brush2 = new LinearGradientBrush(dockBounds, color, color2, LinearGradientMode.Vertical))
				{
					g.FillPath(brush2, path3);
				}
				using (Pen pen4 = new Pen(Col.Alpha(accent, Theme.LightMode ? 130 : 180)))
				{
					g.DrawPath(pen4, path3);
				}
			}
			Rectangle bounds3 = dockBounds;
			bounds3.Inflate(-Dpi.S(2), -Dpi.S(2));
			using (GraphicsPath path4 = DockPath(bounds3, Dpi.S(11)))
			{
				using (Pen pen5 = new Pen(Col.Alpha(Theme.StrokeHi, Theme.LightMode ? 75 : 44)))
				{
					g.DrawPath(pen5, path4);
				}
			}
			using (Pen pen6 = new Pen(Col.Alpha(accent, Theme.LightMode ? 78 : 116)))
			{
				g.DrawLines(pen6, points);
			}
			using (Pen pen7 = new Pen(Col.Alpha(accent, 170), Math.Max(1, Dpi.S(1))))
			{
				int sLimit = Math.Min(Dpi.S(30), dockBounds.Width / 6);
				g.DrawLine(pen7, dockBounds.Left + sResult, dockBounds.Top, dockBounds.Left + sResult + sLimit, dockBounds.Top);
				g.DrawLine(pen7, dockBounds.Right - sResult - sLimit, dockBounds.Bottom, dockBounds.Right - sResult, dockBounds.Bottom);
			}
			for (int i = 0; i < groupSlots.Length; i++)
			{
				int paintDockValue = groupSlots[i];
				if (paintDockValue <= 0 || paintDockValue >= order.Length)
				{
					continue;
				}
				Rectangle rectangle = ItemBounds(paintDockValue - 1);
				Rectangle rectangle2 = ItemBounds(paintDockValue);
				if (rectangle.Top == rectangle2.Top)
				{
					int left = (rectangle.Right + rectangle2.Left) / 2;
					using (Pen pen8 = new Pen(Theme.LightMode ? Theme.Stroke : Color.FromArgb(50, 53, 63)))
					{
						g.DrawLine(pen8, left, rectangle2.Top + Dpi.S(10), left, rectangle2.Bottom - Dpi.S(10));
					}
				}
			}
			int slotOfItemResult = SlotOfItem(sel);
			int slotYResult = SlotY(slotOfItemResult);
			if (!CanAnimateIndicator || indicator.Target != (float)slotYResult)
			{
				indicator.Set(slotYResult);
			}
			if (slotOfItemResult >= 0)
			{
				Rectangle rectangle3 = ItemBounds(slotOfItemResult);
				rectangle3.X = (int)Math.Round(indicator.Value);
				rectangle3.Y = (int)Math.Round(indicatorCross.Value);
				using (GraphicsPath path5 = DockPath(rectangle3, Dpi.S(7)))
				{
					using (LinearGradientBrush brush3 = new LinearGradientBrush(rectangle3, Col.Alpha(accent, Theme.LightMode ? 38 : 62), Col.Alpha(accent, Theme.LightMode ? 19 : 28), LinearGradientMode.Vertical))
					{
						g.FillPath(brush3, path5);
					}
					using (Pen pen9 = new Pen(Col.Alpha(accent, Theme.LightMode ? 54 : 78)))
					{
						g.DrawPath(pen9, path5);
					}
				}
				int sLimit2 = Math.Min(Dpi.S(54), rectangle3.Width - Dpi.S(20));
				using (Pen pen10 = new Pen(Col.Alpha(accent, 28), Dpi.S(5)))
				{
					g.DrawLine(pen10, rectangle3.Left + (rectangle3.Width - sLimit2) / 2, rectangle3.Bottom - 1, rectangle3.Left + (rectangle3.Width + sLimit2) / 2, rectangle3.Bottom - 1);
				}
				using (Pen pen11 = new Pen(accent, Math.Max(1, Dpi.S(2))))
				{
					g.DrawLine(pen11, rectangle3.Left + (rectangle3.Width - sLimit2) / 2, rectangle3.Bottom - 1, rectangle3.Left + (rectangle3.Width + sLimit2) / 2, rectangle3.Bottom - 1);
				}
			}
			for (int j = 0; j < order.Length; j++)
			{
				int paintDockValue2 = order[j];
				Rectangle rectangle4 = ItemBounds(j);
				bool matchesPaintDockValue2 = paintDockValue2 == sel;
				bool matchesPaintDockValue22 = paintDockValue2 == hoverIdx;
				if (!matchesPaintDockValue2 && matchesPaintDockValue22)
				{
					using (GraphicsPath path6 = DockPath(rectangle4, Dpi.S(7)))
					{
						using (SolidBrush brush4 = new SolidBrush(Col.Alpha(Theme.Fg, Theme.LightMode ? 13 : 9)))
						{
							g.FillPath(brush4, path6);
						}
					}
				}
				Color color3 = ((matchesPaintDockValue2 || matchesPaintDockValue22) ? Theme.Fg : Theme.Dim);
				Font dockLabelFont = DockLabelFont;
				Glyphs.Draw(g, glyphs[paintDockValue2], DockIconBounds(j), matchesPaintDockValue2 ? accent : color3);
				TextRenderer.DrawText(g, labels[paintDockValue2], dockLabelFont, DockLabelBounds(j), color3, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				if (!base.WantsFocusCue || paintDockValue2 != ((hoverIdx >= 0) ? hoverIdx : sel))
				{
					continue;
				}
				Rectangle bounds4 = rectangle4;
				bounds4.Inflate(-Dpi.S(3), -Dpi.S(3));
				using (GraphicsPath path7 = DockPath(bounds4, Dpi.S(5)))
				{
					Pen pen12 = new Pen(Col.Alpha(accent, 175));
					pen12.DashStyle = DashStyle.Dot;
					using (Pen pen13 = pen12)
					{
						g.DrawPath(pen13, path7);
					}
				}
			}
		}
	}
}
