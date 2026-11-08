using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PerformanceFeatureGrid : DBPanel
	{
		private sealed class Placement
		{
			internal Control Control;

			internal Rectangle Bounds;

			internal Placement(Control control, int x, int y, int width, int height)
			{
				Control = control;
				Bounds = new Rectangle(x, y, width, height);
			}
		}

		private bool ready;

		private bool arranging;

		private bool autoSizeContent;

		private Size arrangedViewport;

		private readonly List<Rectangle> consoleDividers = new List<Rectangle>();

		private readonly HashSet<PerformanceFeatureCard> observedNested = new HashSet<PerformanceFeatureCard>();

		internal Action ContentSizeChanged;

		internal bool CompactConsole
		{
			get
			{
				return true;
			}
		}

		internal Rectangle ConsoleBounds { get; private set; }

		internal int Columns { get; private set; }

		internal int ContentHeight { get; private set; }

		internal int EffectiveScrollHeight
		{
			get
			{
				return ScrollHeightFor(ContentHeight);
			}
		}

		internal bool AutoSizeContent
		{
			get
			{
				return autoSizeContent;
			}
			set
			{
				if (autoSizeContent != value)
				{
					autoSizeContent = value;
					AutoScroll = !value;
					Reflow();
				}
			}
		}

		internal PerformanceFeatureGrid()
		{
			base.BorderStyle = BorderStyle.None;
			base.Padding = Padding.Empty;
			AutoScroll = true;
			BackColor = Theme.Bg;
			Native.Dark(this);
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			if (autoSizeContent) DsPaint.CardBackground(graphics, this, Theme.Card);
			else WorkspaceCanvasSurface.Paint(graphics, this, e.ClipRectangle);
			if (!autoSizeContent && !ConsoleBounds.IsEmpty)
			{
				Rectangle consoleBounds = ConsoleBounds;
				consoleBounds.Offset(base.AutoScrollPosition);
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				DsPaint.TechFrame(graphics, consoleBounds, Theme.S(12), Backdrop.CardFill(this, Theme.Card), Theme.Stroke);
				int scaledDimension = Theme.S(12);
				int scaledDimension2 = Theme.S(24);
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, 205), Math.Max(1, Theme.S(1))))
				{
					graphics.DrawLines(pen, new Point[4]
					{
						new Point(consoleBounds.Left, consoleBounds.Top + scaledDimension2),
						new Point(consoleBounds.Left, consoleBounds.Top + scaledDimension),
						new Point(consoleBounds.Left + scaledDimension, consoleBounds.Top),
						new Point(consoleBounds.Left + scaledDimension2, consoleBounds.Top)
					});
					graphics.DrawLines(pen, new Point[4]
					{
						new Point(consoleBounds.Right - scaledDimension2, consoleBounds.Bottom),
						new Point(consoleBounds.Right - scaledDimension, consoleBounds.Bottom),
						new Point(consoleBounds.Right, consoleBounds.Bottom - scaledDimension),
						new Point(consoleBounds.Right, consoleBounds.Bottom - scaledDimension2)
					});
				}
			}
			using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Card, Theme.Stroke, 0.62f)))
			{
				foreach (Rectangle consoleDivider in consoleDividers)
				{
					Rectangle rect = consoleDivider;
					rect.Offset(base.AutoScrollPosition);
					graphics.FillRectangle(brush, rect);
				}
			}
		}

		protected override void OnControlAdded(ControlEventArgs e)
		{
			base.OnControlAdded(e);
			PerformanceFeatureCard performanceFeatureCard = e.Control as PerformanceFeatureCard;
			if (performanceFeatureCard != null)
			{
				performanceFeatureCard.PresentationChanged = (Action)Delegate.Combine(performanceFeatureCard.PresentationChanged, new Action(Reflow));
				performanceFeatureCard.PresentationStateChanged = (Action)Delegate.Combine(performanceFeatureCard.PresentationStateChanged, new Action(UpdateGroups));
			}
			else
			{
				e.Control.SizeChanged += delegate
				{
					Reflow();
				};
			}
			if (ready)
			{
				Reflow();
			}
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Reflow();
		}

		internal void Complete()
		{
			ready = true;
			Reflow();
		}

		internal static int ColumnCount(int width)
		{
			if (width < Theme.S(760))
			{
				return 1;
			}
			return 2;
		}

		internal void Reveal(Control target)
		{
			if (target != null && Contains(target))
			{
				Reflow();
				ScrollControlIntoView(target);
			}
		}

		private static IEnumerable<PerformanceFeatureCard> Features(Control owner)
		{
			PerformanceFeatureCard card = owner as PerformanceFeatureCard;
			if (card != null)
			{
				yield return card;
				yield break;
			}
			foreach (Control child in owner.Controls)
			{
				foreach (PerformanceFeatureCard item in Features(child))
				{
					yield return item;
				}
			}
		}

		private void UpdateGroups()
		{
			bool updateGroupsReady = false;
			foreach (Control control3 in base.Controls)
			{
				if (control3 is PerformanceSectionHeading)
				{
					updateGroupsReady = true;
					break;
				}
			}
			if (!updateGroupsReady)
			{
				return;
			}
			foreach (Control control4 in base.Controls)
			{
				foreach (PerformanceFeatureCard item in Features(control4))
				{
					if (item.Parent != this && observedNested.Add(item))
					{
						item.PresentationChanged = (Action)Delegate.Combine(item.PresentationChanged, new Action(Reflow));
						item.PresentationStateChanged = (Action)Delegate.Combine(item.PresentationStateChanged, new Action(UpdateGroups));
					}
				}
			}
			PerformanceSectionHeading performanceSectionHeading = null;
			int processedCount = 0;
			int processedCount2 = 0;
			int processedCount3 = 0;
			foreach (Control control5 in base.Controls)
			{
				PerformanceSectionHeading performanceSectionHeading2 = control5 as PerformanceSectionHeading;
				if (performanceSectionHeading2 != null)
				{
					UpdateGroup(performanceSectionHeading, processedCount, processedCount2, processedCount3);
					performanceSectionHeading = performanceSectionHeading2;
					processedCount = (processedCount2 = (processedCount3 = 0));
				}
				else
				{
					if (performanceSectionHeading == null)
					{
						continue;
					}
					foreach (PerformanceFeatureCard item2 in Features(control5))
					{
						processedCount++;
						Toggle toggle = item2.Hosted as Toggle;
						if (toggle != null && toggle.Checked)
						{
							processedCount2++;
						}
						if (item2.HasStatus && item2.StatusColor == Theme.Warning)
						{
							processedCount3++;
						}
					}
				}
			}
			UpdateGroup(performanceSectionHeading, processedCount, processedCount2, processedCount3);
		}

		private static void UpdateGroup(PerformanceSectionHeading heading, int count, int selected, int attention)
		{
			if (heading != null)
			{
				bool anyConditionMet = heading.FeatureCount != count || heading.SelectedCount != selected || heading.AttentionCount != attention;
				heading.FeatureCount = count;
				heading.SelectedCount = selected;
				heading.AttentionCount = attention;
				string accessibleDescriptionValue = heading.ScopeText + ". " + count + PerformancePolicy.Text(" 项；已选偏好 ", " items; selected preferences ") + selected;
				if (heading.AccessibleName != heading.Text)
				{
					heading.AccessibleName = heading.Text;
				}
				if (heading.AccessibleDescription != accessibleDescriptionValue)
				{
					heading.AccessibleDescription = accessibleDescriptionValue;
				}
				if (anyConditionMet)
				{
					heading.Invalidate();
				}
			}
		}

		private List<Placement> Plan(int width, bool prepareCompositeWidths, out int height, out bool widthsReady)
		{
			int scaledDimension = ((!autoSizeContent) ? Theme.S(8) : 0);
			int planValue = 1;
			int columnsRatio = (width - (Columns - 1) * planValue) / Columns;
			int planValue2 = scaledDimension;
			List<Control> list = new List<Control>();
			foreach (Control control2 in base.Controls)
			{
				list.Add(control2);
			}
			List<Placement> list2 = new List<Placement>();
			widthsReady = true;
			int processedCount = 0;
			while (processedCount < list.Count)
			{
				Control control = list[processedCount];
				PerformanceFeatureCard performanceFeatureCard = control as PerformanceFeatureCard;
				if (performanceFeatureCard != null)
				{
					int i = 1;
					if (performanceFeatureCard.MinimumPresentationWidth <= columnsRatio)
					{
						for (; i < Columns && processedCount + i < list.Count; i++)
						{
							PerformanceFeatureCard performanceFeatureCard2 = list[processedCount + i] as PerformanceFeatureCard;
							if (performanceFeatureCard2 == null || performanceFeatureCard2.MinimumPresentationWidth > columnsRatio)
							{
								break;
							}
						}
					}
					if (i > 1)
					{
						int iRatio = (width - (i - 1) * planValue) / i;
						int planValue3 = 0;
						for (int j = 0; j < i; j++)
						{
							int remainingWidth = ((j == i - 1) ? (width - j * (iRatio + planValue)) : iRatio);
							planValue3 = Math.Max(planValue3, ((PerformanceFeatureCard)list[processedCount + j]).PresentationHeight(remainingWidth));
						}
						for (int k = 0; k < i; k++)
						{
							int remainingWidth2 = ((k == i - 1) ? (width - k * (iRatio + planValue)) : iRatio);
							list2.Add(new Placement(list[processedCount + k], scaledDimension + k * (iRatio + planValue), planValue2, remainingWidth2, planValue3));
						}
						planValue2 += planValue3 + planValue;
						processedCount += i;
					}
					else
					{
						int presentationHeightResult = performanceFeatureCard.PresentationHeight(width);
						list2.Add(new Placement(performanceFeatureCard, scaledDimension, planValue2, width, presentationHeightResult));
						planValue2 += presentationHeightResult + planValue;
						processedCount++;
					}
					continue;
				}
				PerformanceSectionHeading performanceSectionHeading = control as PerformanceSectionHeading;
				if (performanceSectionHeading == null && control.Width != width)
				{
					widthsReady = false;
					if (prepareCompositeWidths)
					{
						control.Width = width;
					}
				}
				int height2 = ((performanceSectionHeading == null) ? control.Height : performanceSectionHeading.MeasureHeight(width));
				list2.Add(new Placement(control, scaledDimension, planValue2, width, height2));
				planValue2 += height2 + ((performanceSectionHeading == null) ? planValue : Theme.S(4));
				processedCount++;
			}
			height = Math.Max(scaledDimension * 2, planValue2 - ((list2.Count > 0) ? planValue : 0) + scaledDimension);
			return list2;
		}

		private void UpdateConsoleGeometry(List<Placement> plan, int width, int height)
		{
			int scaledDimension = ((!autoSizeContent) ? Theme.S(8) : 0);
			ConsoleBounds = (autoSizeContent ? Rectangle.Empty : new Rectangle(0, 0, width + scaledDimension * 2 - 1, Math.Max(1, height - 1)));
			consoleDividers.Clear();
			for (int i = 0; i < plan.Count; i++)
			{
				Rectangle bounds = plan[i].Bounds;
				if (bounds.Left > scaledDimension)
				{
					consoleDividers.Add(new Rectangle(bounds.Left - 1, bounds.Top, 1, bounds.Height));
				}
				if (i + 1 < plan.Count && plan[i + 1].Bounds.Top > bounds.Top)
				{
					consoleDividers.Add(new Rectangle(scaledDimension, bounds.Bottom, width, 1));
				}
			}
		}

		private bool Matches(List<Placement> plan, int height)
		{
			if (ContentHeight != height || arrangedViewport != base.ClientSize)
			{
				return false;
			}
			Point autoScrollPosition = base.AutoScrollPosition;
			foreach (Placement item in plan)
			{
				Rectangle bounds = item.Control.Bounds;
				bounds.Offset(-autoScrollPosition.X, -autoScrollPosition.Y);
				if (bounds != item.Bounds)
				{
					return false;
				}
			}
			if (autoSizeContent)
			{
				if (base.Height == height)
				{
					return base.AutoScrollMinSize.IsEmpty;
				}
				return false;
			}
			int scrollHeightForResult = ScrollHeightFor(height);
			if (base.AutoScrollMinSize == new Size(0, height) && !base.HorizontalScroll.Visible && autoScrollPosition.X == 0 && DisplayRectangle.Width == base.ClientSize.Width && DisplayRectangle.Height == Math.Max(scrollHeightForResult, base.ClientSize.Height))
			{
				return -autoScrollPosition.Y <= Math.Max(0, scrollHeightForResult - base.ClientSize.Height);
			}
			return false;
		}

		private int ScrollHeightFor(int contentHeight)
		{
			return contentHeight + ((!autoSizeContent) ? Math.Max(0, base.AutoScrollMargin.Height) : 0);
		}

		internal void Reflow()
		{
			if (!ready || arranging || base.IsDisposed || base.Disposing)
			{
				return;
			}
			arranging = true;
			int contentHeight = ContentHeight;
			try
			{
				int scaledDimension = ((!autoSizeContent) ? Theme.S(8) : 0);
				int lowerBound = Math.Max(1, base.Width - scaledDimension * 2);
				int reflowValue = lowerBound;
				Columns = ColumnCount(reflowValue);
				UpdateGroups();
				int contentHeight2;
				bool widthsReady;
				List<Placement> list = Plan(reflowValue, false, out contentHeight2, out widthsReady);
				if (!autoSizeContent && ScrollHeightFor(contentHeight2) > base.Height)
				{
					reflowValue = Math.Max(1, lowerBound - SystemInformation.VerticalScrollBarWidth);
					Columns = ColumnCount(reflowValue);
					list = Plan(reflowValue, false, out contentHeight2, out widthsReady);
				}
				if (widthsReady && Matches(list, contentHeight2))
				{
					foreach (Placement item in list)
					{
						PerformanceFeatureCard performanceFeatureCard = item.Control as PerformanceFeatureCard;
						if (performanceFeatureCard != null)
						{
							performanceFeatureCard.ArrangePresentation();
						}
					}
					return;
				}
				Point point = new Point(Math.Max(0, -base.AutoScrollPosition.X), Math.Max(0, -base.AutoScrollPosition.Y));
				SuspendLayout();
				try
				{
					base.AutoScrollPosition = Point.Empty;
					list = Plan(reflowValue, true, out contentHeight2, out widthsReady);
					if (!autoSizeContent && ScrollHeightFor(contentHeight2) > base.Height && reflowValue == lowerBound)
					{
						reflowValue = Math.Max(1, lowerBound - SystemInformation.VerticalScrollBarWidth);
						Columns = ColumnCount(reflowValue);
						list = Plan(reflowValue, true, out contentHeight2, out widthsReady);
					}
					int processedCount = 0;
					foreach (Placement item2 in list)
					{
						Control control = item2.Control;
						control.TabIndex = processedCount++;
						control.Visible = true;
						if (control.Bounds != item2.Bounds)
						{
							control.Bounds = item2.Bounds;
						}
						PerformanceFeatureCard performanceFeatureCard2 = control as PerformanceFeatureCard;
						if (performanceFeatureCard2 != null)
						{
							performanceFeatureCard2.ArrangePresentation();
						}
					}
					ContentHeight = contentHeight2;
					UpdateConsoleGeometry(list, reflowValue, contentHeight2);
					if (autoSizeContent)
					{
						base.AutoScrollMinSize = Size.Empty;
						base.Height = contentHeight2;
					}
					else if (base.AutoScrollMinSize != new Size(0, contentHeight2))
					{
						base.AutoScrollMinSize = new Size(0, contentHeight2);
					}
				}
				finally
				{
					ResumeLayout(true);
					if (!autoSizeContent)
					{
						AdjustFormScrollbars(true);
						base.AutoScrollPosition = new Point(0, Math.Min(point.Y, Math.Max(0, EffectiveScrollHeight - base.ClientSize.Height)));
					}
				}
				arrangedViewport = base.ClientSize;
				Invalidate();
			}
			finally
			{
				arranging = false;
			}
			if (contentHeight != ContentHeight && ContentSizeChanged != null)
			{
				ContentSizeChanged();
			}
		}
	}
}
