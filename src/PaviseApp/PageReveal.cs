using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PageReveal : Form
	{
		private const int DurationMs = 160;

		private const int Layered = 524288;

		private const int Transparent = 32;

		private const int ToolWindow = 128;

		private const int NoActivate = 134217728;

		private readonly Form host;

		private readonly Stopwatch watch = new Stopwatch();

		private WorkspacePanel surface;

		private Point surfaceOffset;

		private bool frameAttached;

		private bool layeredReady;

		private byte alpha = byte.MaxValue;

		internal WorkspacePanel Surface
		{
			get
			{
				return surface;
			}
		}

		internal byte Alpha
		{
			get
			{
				return alpha;
			}
		}

		internal bool Fading
		{
			get
			{
				return watch.IsRunning;
			}
		}

		protected override bool ShowWithoutActivation
		{
			get
			{
				return true;
			}
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ExStyle |= 134742176;
				return createParams;
			}
		}

		private bool CanReveal
		{
			get
			{
				if (!host.IsDisposed && host.IsHandleCreated && host.Visible && host.WindowState != FormWindowState.Minimized && IsWindowEnabled(host.Handle) && !UiClock.Frozen)
				{
					return !UiClock.Suspended;
				}
				return false;
			}
		}

		internal PageReveal(Form owner)
		{
			host = owner;
			base.FormBorderStyle = FormBorderStyle.None;
			base.ShowInTaskbar = false;
			base.StartPosition = FormStartPosition.Manual;
			base.AutoScaleMode = AutoScaleMode.None;
			base.TabStop = false;
			SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			host.VisibleChanged += OnHostChanged;
			host.LocationChanged += OnHostChanged;
			host.SizeChanged += OnHostChanged;
			host.Deactivate += OnHostChanged;
			host.HandleDestroyed += OnHostChanged;
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			layeredReady = SetAlpha(byte.MaxValue);
		}

		internal bool Prepare(WorkspacePanel page)
		{
			Cancel();
			if (page == null || page.IsDisposed || page.Parent == null || !CanReveal)
			{
				return false;
			}
			Rectangle a = page.Parent.RectangleToScreen(page.Bounds);
			Rectangle bounds = Rectangle.Intersect(a, host.RectangleToScreen(host.ClientRectangle));
			if (bounds.Width <= 0 || bounds.Height <= 0)
			{
				return false;
			}
			surface = page;
			surfaceOffset = new Point(bounds.Left - a.Left, bounds.Top - a.Top);
			surface.VisibleChanged += OnSurfaceVisibleChanged;
			surface.LocationChanged += OnHostChanged;
			surface.SizeChanged += OnHostChanged;
			surface.ParentChanged += OnHostChanged;
			surface.Disposed += OnHostChanged;
			base.Bounds = bounds;
			try
			{
				if (base.IsHandleCreated)
				{
					layeredReady = SetAlpha(byte.MaxValue);
				}
				Show(host);
				if (!layeredReady)
				{
					Cancel();
					return false;
				}
				Invalidate();
				Update();
				return surface != null && base.Visible;
			}
			catch
			{
				Cancel();
				return false;
			}
		}

		internal void Reveal()
		{
			if (surface == null || !surface.Visible || !CanReveal)
			{
				Cancel();
				return;
			}
			WorkspacePanel workspacePanel = surface;
			bool redrawWindowSucceeded;
			try
			{
				redrawWindowSucceeded = RedrawWindow(workspacePanel.Handle, IntPtr.Zero, IntPtr.Zero, 385u);
			}
			catch
			{
				if (surface == workspacePanel)
				{
					Cancel();
				}
				throw;
			}
			if (surface != workspacePanel)
			{
				return;
			}
			if (!redrawWindowSucceeded || workspacePanel.IsDisposed || !workspacePanel.Visible || !base.Visible || !CanReveal)
			{
				Cancel();
				return;
			}
			watch.Restart();
			if (!frameAttached)
			{
				UiClock.Frame += OnFrame;
				frameAttached = true;
			}
			UiClock.Wake();
		}

		private void OnFrame(object sender, EventArgs e)
		{
			if (surface == null || surface.IsDisposed || !surface.Visible || !CanReveal)
			{
				Cancel();
				return;
			}
			double totalMillisecondsRatio = watch.Elapsed.TotalMilliseconds / 160.0;
			if (totalMillisecondsRatio >= 1.0)
			{
				Cancel();
				return;
			}
			double remainingTotalMillisecondsRatio = 1.0 - totalMillisecondsRatio;
			byte b = (byte)Math.Max(1, (int)Math.Round(255.0 * remainingTotalMillisecondsRatio * remainingTotalMillisecondsRatio * remainingTotalMillisecondsRatio));
			if (b != alpha && !SetAlpha(b))
			{
				Cancel();
			}
		}

		internal void Cancel()
		{
			watch.Reset();
			if (frameAttached)
			{
				UiClock.Frame -= OnFrame;
				frameAttached = false;
			}
			if (surface != null)
			{
				surface.VisibleChanged -= OnSurfaceVisibleChanged;
				surface.LocationChanged -= OnHostChanged;
				surface.SizeChanged -= OnHostChanged;
				surface.ParentChanged -= OnHostChanged;
				surface.Disposed -= OnHostChanged;
				surface = null;
			}
			if (!base.IsDisposed && base.Visible)
			{
				Hide();
			}
		}

		private void OnHostChanged(object sender, EventArgs e)
		{
			Cancel();
		}

		private void OnSurfaceVisibleChanged(object sender, EventArgs e)
		{
			if (surface != null && !surface.Visible)
			{
				Cancel();
			}
		}

		private bool SetAlpha(byte value)
		{
			if (!SetLayeredWindowAttributes(base.Handle, 0u, value, 2u))
			{
				return false;
			}
			alpha = value;
			return true;
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (surface == null || surface.IsDisposed)
			{
				return;
			}
			GraphicsState gstate = e.Graphics.Save();
			try
			{
				e.Graphics.TranslateTransform(-surfaceOffset.X, -surfaceOffset.Y);
				Rectangle clip = new Rectangle(surfaceOffset, base.ClientSize);
				surface.PaintSurface(e.Graphics, clip);
			}
			finally
			{
				e.Graphics.Restore(gstate);
			}
		}

		protected override void WndProc(ref Message message)
		{
			if (message.Msg == 132)
			{
				message.Result = new IntPtr(-1);
			}
			else if (message.Msg == 33)
			{
				message.Result = new IntPtr(3);
			}
			else
			{
				base.WndProc(ref message);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Cancel();
				host.VisibleChanged -= OnHostChanged;
				host.LocationChanged -= OnHostChanged;
				host.SizeChanged -= OnHostChanged;
				host.Deactivate -= OnHostChanged;
				host.HandleDestroyed -= OnHostChanged;
			}
			base.Dispose(disposing);
		}

		[DllImport("user32.dll")]
		private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

		[DllImport("user32.dll")]
		private static extern bool IsWindowEnabled(IntPtr window);

		[DllImport("user32.dll")]
		private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);
	}
}
