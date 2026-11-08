using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class DropdownHost : IDisposable
	{
		private readonly Control owner;

		private readonly IBusyInteractive busyAware;

		private readonly FxControl busySource;

		private readonly List<Control> watched = new List<Control>();

		private ContextMenuStrip menu;

		private bool closeOnClick;

		private bool disposed;

		public bool AlignRight;

		public int GapLogical = 4;

		public int EdgeMarginLogical = 6;

		public Control Owner
		{
			get
			{
				return owner;
			}
		}

		public ContextMenuStrip Menu
		{
			get
			{
				if (menu == null || menu.IsDisposed)
				{
					return null;
				}
				return menu;
			}
		}

		public bool IsCreated
		{
			get
			{
				return Menu != null;
			}
		}

		public bool IsVisible
		{
			get
			{
				if (IsCreated)
				{
					return menu.Visible;
				}
				return false;
			}
		}

		public Rectangle LastPlacement { get; private set; }

		public bool OwnerCanInteract
		{
			get
			{
				if (disposed || owner.IsDisposed || !owner.Enabled)
				{
					return false;
				}
				if (busyAware != null)
				{
					return busyAware.CanInteract;
				}
				return true;
			}
		}

		public event EventHandler Closed;

		public DropdownHost(Control owner)
		{
			if (owner == null)
			{
				throw new ArgumentNullException("owner");
			}
			this.owner = owner;
			busyAware = owner as IBusyInteractive;
			busySource = owner as FxControl;
			if (busySource != null)
			{
				busySource.BusyChanged += OwnerBusyChanged;
			}
			owner.ParentChanged += ContextChanged;
			owner.LocationChanged += ContextChanged;
			owner.SizeChanged += ContextChanged;
			owner.HandleDestroyed += ContextChanged;
			owner.VisibleChanged += OwnerVisibleChanged;
			owner.EnabledChanged += OwnerEnabledChanged;
			owner.Disposed += OwnerDisposed;
		}

		public void NoteMouseDown()
		{
			closeOnClick = closeOnClick || IsVisible;
		}

		public void NoteMouseUp()
		{
			closeOnClick = false;
		}

		public bool ConsumeCloseClick()
		{
			if (!closeOnClick && !IsVisible)
			{
				return false;
			}
			closeOnClick = false;
			Close();
			return true;
		}

		public Rectangle Placement(Size size)
		{
			Rectangle workingArea = Screen.FromControl(owner).WorkingArea;
			Rectangle anchor = owner.RectangleToScreen(owner.ClientRectangle);
			return DsPopup.Place(anchor, size, workingArea, Theme.S(GapLogical), Theme.S(EdgeMarginLogical), AlignRight);
		}

		public void Attach(ContextMenuStrip next)
		{
			if (next != null && object.ReferenceEquals(next, menu))
			{
				return;
			}
			Close();
			if (next != null)
			{
				if (disposed || owner.IsDisposed)
				{
					next.Dispose();
					return;
				}
				closeOnClick = false;
				menu = next;
				next.Closing += MenuClosing;
				next.Closed += MenuClosed;
				WatchContext();
			}
		}

		public void Show(ContextMenuStrip next)
		{
			if (next == null)
			{
				Close();
				return;
			}
			bool referenceEqualsSucceeded = object.ReferenceEquals(next, menu);
			if (!OwnerCanInteract)
			{
				Close();
				if (!referenceEqualsSucceeded)
				{
					next.Dispose();
				}
				return;
			}
			if (!referenceEqualsSucceeded)
			{
				Attach(next);
			}
			if (object.ReferenceEquals(next, menu))
			{
				closeOnClick = false;
				Rectangle rectangle2 = (LastPlacement = Placement(next.Size));
				if (next.Size != rectangle2.Size)
				{
					next.Size = rectangle2.Size;
				}
				try
				{
					next.Show(owner, owner.PointToClient(rectangle2.Location));
				}
				catch
				{
					Close();
					throw;
				}
				if (!owner.IsDisposed)
				{
					owner.Invalidate();
				}
			}
		}

		public void Close()
		{
			ContextMenuStrip owned = menu;
			menu = null;
			UnwatchContext();
			if (owned != null)
			{
				owned.Closing -= MenuClosing;
				owned.Closed -= MenuClosed;
				try
				{
					if (!owned.IsDisposed && owned.Visible)
					{
						owned.Close();
					}
				}
				finally
				{
					if (!owned.IsDisposed)
					{
						if (owned.IsHandleCreated)
						{
							owned.BeginInvoke((MethodInvoker)delegate
							{
								if (!owned.IsDisposed)
								{
									owned.Dispose();
								}
							});
						}
						else
						{
							owned.Dispose();
						}
					}
				}
				EventHandler closed = this.Closed;
				if (closed != null)
				{
					closed(this, EventArgs.Empty);
				}
			}
			if (!owner.IsDisposed)
			{
				owner.Invalidate();
			}
		}

		public bool HandleCommandKey(Keys keys, bool menuButton, Action open)
		{
			if (keys == Keys.Escape)
			{
				if (!IsCreated)
				{
					return false;
				}
				Close();
				return true;
			}
			if (DsKeys.IsTab(keys))
			{
				Close();
				return false;
			}
			if (DsKeys.IsDropdownOpen(keys, menuButton))
			{
				if (open != null)
				{
					open();
				}
				return true;
			}
			return false;
		}

		private void MenuClosing(object sender, ToolStripDropDownClosingEventArgs args)
		{
			if (args.CloseReason == ToolStripDropDownCloseReason.AppClicked && !owner.IsDisposed && owner.IsHandleCreated && owner.ClientRectangle.Contains(owner.PointToClient(Cursor.Position)))
			{
				closeOnClick = true;
			}
		}

		private void MenuClosed(object sender, ToolStripDropDownClosedEventArgs args)
		{
			if (object.ReferenceEquals(sender, menu))
			{
				Close();
			}
		}

		private void WatchContext()
		{
			UnwatchContext();
			for (Control parent = owner.Parent; parent != null; parent = parent.Parent)
			{
				watched.Add(parent);
				parent.LocationChanged += ContextChanged;
				parent.SizeChanged += ContextChanged;
				parent.VisibleChanged += ContextChanged;
				parent.ParentChanged += ContextChanged;
				ScrollableControl scrollableControl = parent as ScrollableControl;
				if (scrollableControl != null)
				{
					scrollableControl.Scroll += ContextScrolled;
				}
			}
		}

		private void UnwatchContext()
		{
			foreach (Control item in watched)
			{
				item.LocationChanged -= ContextChanged;
				item.SizeChanged -= ContextChanged;
				item.VisibleChanged -= ContextChanged;
				item.ParentChanged -= ContextChanged;
				ScrollableControl scrollableControl = item as ScrollableControl;
				if (scrollableControl != null)
				{
					scrollableControl.Scroll -= ContextScrolled;
				}
			}
			watched.Clear();
		}

		private void ContextChanged(object sender, EventArgs args)
		{
			Close();
		}

		private void ContextScrolled(object sender, ScrollEventArgs args)
		{
			Close();
		}

		private void OwnerVisibleChanged(object sender, EventArgs args)
		{
			if (!owner.Visible)
			{
				Close();
			}
		}

		private void OwnerEnabledChanged(object sender, EventArgs args)
		{
			if (!owner.Enabled)
			{
				Close();
			}
		}

		private void OwnerBusyChanged(object sender, EventArgs args)
		{
			if (busyAware != null && busyAware.Busy)
			{
				Close();
			}
		}

		private void OwnerDisposed(object sender, EventArgs args)
		{
			Dispose();
		}

		public void Dispose()
		{
			if (!disposed)
			{
				disposed = true;
				Close();
				if (busySource != null)
				{
					busySource.BusyChanged -= OwnerBusyChanged;
				}
				owner.ParentChanged -= ContextChanged;
				owner.LocationChanged -= ContextChanged;
				owner.SizeChanged -= ContextChanged;
				owner.HandleDestroyed -= ContextChanged;
				owner.VisibleChanged -= OwnerVisibleChanged;
				owner.EnabledChanged -= OwnerEnabledChanged;
				owner.Disposed -= OwnerDisposed;
			}
		}
	}
}
