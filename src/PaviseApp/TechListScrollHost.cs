using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class TechListScrollHost : Panel
	{
		private sealed class ScrollRail : Control
		{
			private readonly TechListScrollHost owner;

			private bool hot;

			private bool dragging;

			private int dragOffset;

			internal Rectangle TrackRectangle
			{
				get
				{
					int sLimit = Math.Min(Theme.S(4), base.ClientSize.Height / 2);
					return new Rectangle(0, sLimit, base.ClientSize.Width, Math.Max(0, base.ClientSize.Height - sLimit * 2));
				}
			}

			internal Rectangle ThumbRectangle
			{
				get
				{
					Rectangle trackRectangle = TrackRectangle;
					if (!owner.ScrollNeeded || trackRectangle.Width <= 0 || trackRectangle.Height <= 0)
					{
						return Rectangle.Empty;
					}
					int val = (int)Math.Round((double)trackRectangle.Height * (double)owner.VisibleRows / (double)Math.Max(1, owner.list.Items.Count));
					val = Math.Min(trackRectangle.Height, Math.Max(Theme.S(32), val));
					int minLimit = Math.Max(0, Math.Min(owner.MaximumTop, owner.list.TopIndex));
					int topOffset = trackRectangle.Top + (int)Math.Round((double)(trackRectangle.Height - val) * (double)minLimit / (double)Math.Max(1, owner.MaximumTop));
					int sLimit = Math.Min(trackRectangle.Width, Theme.S((hot || dragging) ? 8 : 6));
					return new Rectangle(trackRectangle.Left + (trackRectangle.Width - sLimit) / 2, topOffset, sLimit, val);
				}
			}

			[DllImport("user32.dll")]
			private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

			internal ScrollRail(TechListScrollHost owner)
			{
				this.owner = owner;
				SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
				SetStyle(ControlStyles.Selectable, false);
				base.TabStop = false;
				base.Visible = false;
				Cursor = Cursors.Hand;
				base.AccessibleRole = AccessibleRole.ScrollBar;
				base.AccessibleName = Lang.T("ui.scroll.vertical");
			}

			protected override void OnPaint(PaintEventArgs e)
			{
				base.OnPaint(e);
				Rectangle thumbRectangle = ThumbRectangle;
				if (!thumbRectangle.IsEmpty)
				{
					Graphics graphics = e.Graphics;
					graphics.SmoothingMode = SmoothingMode.AntiAlias;
					Rectangle trackRectangle = TrackRectangle;
					int maxLimit = Math.Min(trackRectangle.Width, Math.Max(1, Theme.S(2)));
					Rectangle bounds = new Rectangle(trackRectangle.Left + (trackRectangle.Width - maxLimit) / 2, trackRectangle.Top, maxLimit, trackRectangle.Height);
					FillPill(graphics, bounds, Col.Lerp(BackColor, Theme.Stroke, 0.55f));
					Color color = (dragging ? Theme.Accent : (hot ? Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.7f) : Theme.StrokeHi));
					FillPill(graphics, thumbRectangle, color);
				}
			}

			private static void FillPill(Graphics g, Rectangle bounds, Color color)
			{
				if (bounds.Width <= 0 || bounds.Height <= 0)
				{
					return;
				}
				int heightLimit = Math.Min(bounds.Width, bounds.Height);
				using (GraphicsPath graphicsPath = new GraphicsPath())
				{
					using (SolidBrush brush = new SolidBrush(color))
					{
						graphicsPath.AddArc(bounds.Left, bounds.Top, heightLimit, heightLimit, 180f, 180f);
						graphicsPath.AddArc(bounds.Left, bounds.Bottom - heightLimit, heightLimit, heightLimit, 0f, 180f);
						graphicsPath.CloseFigure();
						g.FillPath(brush, graphicsPath);
					}
				}
			}

			protected override void OnMouseEnter(EventArgs e)
			{
				base.OnMouseEnter(e);
				hot = true;
				Invalidate();
			}

			protected override void OnMouseLeave(EventArgs e)
			{
				base.OnMouseLeave(e);
				hot = false;
				Invalidate();
			}

			protected override void OnMouseDown(MouseEventArgs e)
			{
				base.OnMouseDown(e);
				if (e.Button != MouseButtons.Left || !owner.ScrollNeeded)
				{
					return;
				}
				Rectangle thumbRectangle = ThumbRectangle;
				if (!thumbRectangle.IsEmpty)
				{
					if (e.Y >= thumbRectangle.Top && e.Y < thumbRectangle.Bottom)
					{
						BeginDrag(e.Y);
						base.Capture = true;
					}
					else
					{
						owner.ScrollTo(owner.list.TopIndex + ((e.Y < thumbRectangle.Top) ? (-owner.VisibleRows) : owner.VisibleRows));
					}
				}
			}

			protected override void OnMouseMove(MouseEventArgs e)
			{
				base.OnMouseMove(e);
				if (dragging && base.Capture)
				{
					DragTo(e.Y);
				}
			}

			private void BeginDrag(int mouseY)
			{
				Rectangle thumbRectangle = ThumbRectangle;
				if (!thumbRectangle.IsEmpty)
				{
					dragging = true;
					dragOffset = mouseY - thumbRectangle.Top;
					Invalidate();
				}
			}

			private void DragTo(int mouseY)
			{
				if (dragging)
				{
					Rectangle trackRectangle = TrackRectangle;
					Rectangle thumbRectangle = ThumbRectangle;
					int remainingHeight = trackRectangle.Height - thumbRectangle.Height;
					if (remainingHeight > 0)
					{
						int minLimit = Math.Max(0, Math.Min(remainingHeight, mouseY - dragOffset - trackRectangle.Top));
						owner.ScrollTo((int)Math.Round((double)minLimit * (double)owner.MaximumTop / (double)remainingHeight));
					}
				}
			}

			protected override void OnMouseUp(MouseEventArgs e)
			{
				base.OnMouseUp(e);
				if (e.Button == MouseButtons.Left)
				{
					CancelDrag();
				}
			}

			protected override void OnMouseCaptureChanged(EventArgs e)
			{
				base.OnMouseCaptureChanged(e);
				if (!base.Capture)
				{
					dragging = false;
					Invalidate();
				}
			}

			protected override void OnVisibleChanged(EventArgs e)
			{
				base.OnVisibleChanged(e);
				if (!base.Visible)
				{
					hot = false;
					CancelDrag();
				}
			}

			protected override void OnEnabledChanged(EventArgs e)
			{
				base.OnEnabledChanged(e);
				if (!base.Enabled)
				{
					CancelDrag();
				}
			}

			protected override void OnHandleDestroyed(EventArgs e)
			{
				CancelDrag();
				base.OnHandleDestroyed(e);
			}

			protected override void OnMouseWheel(MouseEventArgs e)
			{
				if (owner.list.IsHandleCreated && !owner.list.IsDisposed)
				{
					SendMessage(owner.list.Handle, 522, (IntPtr)((ushort)e.Delta << 16), IntPtr.Zero);
				}
				HandledMouseEventArgs handledMouseEventArgs = e as HandledMouseEventArgs;
				if (handledMouseEventArgs != null)
				{
					handledMouseEventArgs.Handled = true;
				}
			}

			internal void CancelDrag()
			{
				dragging = false;
				if (base.Capture)
				{
					base.Capture = false;
				}
				Invalidate();
			}
		}

		private readonly TechListBox list;

		private readonly ScrollRail rail;

		private bool syncing;

		private int visibleRows = 1;

		private int maximumTop;

		private bool scrollNeeded;

		internal int VisibleRows
		{
			get
			{
				return visibleRows;
			}
		}

		internal int MaximumTop
		{
			get
			{
				return maximumTop;
			}
		}

		internal bool ScrollNeeded
		{
			get
			{
				return scrollNeeded;
			}
		}

		internal TechListScrollHost(TechListBox list)
		{
			if (list == null)
			{
				throw new ArgumentNullException("list");
			}
			this.list = list;
			SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			base.TabStop = false;
			BackColor = list.BackColor;
			list.Dock = DockStyle.None;
			list.ExternalScrollBar = true;
			rail = new ScrollRail(this);
			base.Controls.Add(list);
			base.Controls.Add(rail);
			list.ViewportChanged += OnViewportChanged;
			list.BackColorChanged += OnViewportChanged;
			SyncViewport();
		}

		protected override void OnLayout(LayoutEventArgs e)
		{
			base.OnLayout(e);
			if (list != null && rail != null && !list.IsDisposed && list.Parent == this)
			{
				int width = (scrollNeeded ? Math.Min(base.ClientSize.Width, Theme.S(14)) : 0);
				list.SetBounds(0, 0, Math.Max(0, base.ClientSize.Width - width), base.ClientSize.Height);
				rail.SetBounds(base.ClientSize.Width - width, 0, width, base.ClientSize.Height);
				SyncViewport();
			}
		}

		private void OnViewportChanged(object sender, EventArgs e)
		{
			SyncViewport();
		}

		internal void SyncViewport()
		{
			if (syncing || base.IsDisposed || base.Disposing || list.IsDisposed || list.Parent != this)
			{
				return;
			}
			syncing = true;
			try
			{
				int lowerBound = Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
				int lowerBound2 = Math.Max(0, list.Items.Count - lowerBound);
				bool anyConditionMet = lowerBound != visibleRows || lowerBound2 != maximumTop;
				visibleRows = lowerBound;
				maximumTop = lowerBound2;
				bool visibleValue = lowerBound2 > 0;
				BackColor = list.BackColor;
				rail.BackColor = list.BackColor;
				if (visibleValue != scrollNeeded)
				{
					scrollNeeded = visibleValue;
					rail.Visible = visibleValue;
					PerformLayout();
				}
				if (anyConditionMet || !visibleValue)
				{
					rail.CancelDrag();
				}
				rail.Invalidate();
			}
			finally
			{
				syncing = false;
			}
		}

		internal void ScrollTo(int top)
		{
			if (!base.IsDisposed && !base.Disposing && !list.IsDisposed && list.Parent == this && list.Items.Count != 0)
			{
				int topIndexValue = Math.Max(0, Math.Min(maximumTop, top));
				if (list.TopIndex != topIndexValue)
				{
					list.TopIndex = topIndexValue;
				}
				SyncViewport();
			}
		}

		protected override void OnControlRemoved(ControlEventArgs e)
		{
			base.OnControlRemoved(e);
			if (e.Control == list && list != null)
			{
				Detach();
				if (!list.IsDisposed && !list.Disposing && !base.Disposing && !base.IsDisposed)
				{
					list.ExternalScrollBar = false;
				}
				if (rail != null && !rail.IsDisposed)
				{
					rail.CancelDrag();
					rail.Visible = false;
				}
			}
		}

		private void Detach()
		{
			list.ViewportChanged -= OnViewportChanged;
			list.BackColorChanged -= OnViewportChanged;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Detach();
			}
			base.Dispose(disposing);
		}
	}
}
