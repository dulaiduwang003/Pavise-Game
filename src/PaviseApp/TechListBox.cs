using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class TechListBox : ListBox
	{
		private const int WmEraseBkgnd = 20;

		private const int WmSetRedraw = 11;

		private const int WsVScroll = 2097152;

		private const int SrcCopy = 13369376;

		private IntPtr memDc = IntPtr.Zero;

		private IntPtr memBmp = IntPtr.Zero;

		private IntPtr oldBmp = IntPtr.Zero;

		private int bufW;

		private int bufH;

		private Graphics surface;

		private bool drawFaultLogged;

		private bool externalScrollBar;

		private bool viewportQueued;

		private bool redrawEnabled = true;

		private int viewportGeneration;

		private long wheelDelta;

		internal bool ExternalScrollBar
		{
			get
			{
				return externalScrollBar;
			}
			set
			{
				if (externalScrollBar != value)
				{
					externalScrollBar = value;
					if (base.IsHandleCreated)
					{
						RecreateHandle();
					}
				}
			}
		}

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				if (externalScrollBar)
				{
					createParams.Style &= -2097153;
				}
				return createParams;
			}
		}

		internal event EventHandler ViewportChanged;

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

		[DllImport("gdi32.dll")]
		private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr obj);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteDC(IntPtr hdc);

		[DllImport("gdi32.dll")]
		private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			QueueViewportChanged();
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == 20 && m.WParam != IntPtr.Zero)
			{
				FillTail(m.WParam);
				m.Result = (IntPtr)1;
				return;
			}
			if (externalScrollBar && m.Msg == 11)
			{
				redrawEnabled = m.WParam != IntPtr.Zero;
			}
			base.WndProc(ref m);
			if (externalScrollBar && redrawEnabled)
			{
				switch (m.Msg)
				{
				case 5:
				case 11:
				case 256:
				case 258:
				case 275:
				case 277:
				case 384:
				case 385:
				case 386:
				case 388:
				case 389:
				case 390:
				case 407:
				case 415:
				case 416:
				case 423:
				case 512:
				case 513:
				case 522:
					QueueViewportChanged();
					break;
				}
			}
		}

		private void QueueViewportChanged()
		{
			if (!externalScrollBar || viewportQueued || !base.IsHandleCreated || base.IsDisposed || base.Disposing)
			{
				return;
			}
			viewportQueued = true;
			int generation = viewportGeneration;
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (generation == viewportGeneration)
					{
						viewportQueued = false;
						if (externalScrollBar && !base.IsDisposed && !base.Disposing && redrawEnabled)
						{
							EventHandler viewportChanged = this.ViewportChanged;
							if (viewportChanged != null)
							{
								viewportChanged(this, EventArgs.Empty);
							}
						}
					}
				});
			}
			catch
			{
				viewportQueued = false;
			}
		}

		protected override void OnSelectedIndexChanged(EventArgs e)
		{
			base.OnSelectedIndexChanged(e);
			QueueViewportChanged();
		}

		protected override void OnMouseWheel(MouseEventArgs e)
		{
			base.OnMouseWheel(e);
			if (!externalScrollBar || base.MultiColumn)
			{
				return;
			}
			HandledMouseEventArgs handledMouseEventArgs = e as HandledMouseEventArgs;
			if (handledMouseEventArgs != null && handledMouseEventArgs.Handled)
			{
				return;
			}
			if (handledMouseEventArgs != null)
			{
				handledMouseEventArgs.Handled = true;
			}
			int mouseWheelScrollLines = SystemInformation.MouseWheelScrollLines;
			if (mouseWheelScrollLines != 0)
			{
				int mouseWheelScrollDelta = SystemInformation.MouseWheelScrollDelta;
				wheelDelta += e.Delta;
				int num = (int)(wheelDelta / mouseWheelScrollDelta);
				wheelDelta %= mouseWheelScrollDelta;
				if (num != 0 && base.Items.Count != 0)
				{
					int num2 = Math.Max(1, base.ClientSize.Height / Math.Max(1, ItemHeight));
					int num3 = ((mouseWheelScrollLines < 0) ? num2 : mouseWheelScrollLines);
					long val = base.TopIndex - (long)num * (long)num3;
					base.TopIndex = (int)Math.Max(0L, Math.Min(Math.Max(0, base.Items.Count - num2), val));
					QueueViewportChanged();
				}
			}
		}

		protected override void OnDrawItem(DrawItemEventArgs e)
		{
			Rectangle bounds = e.Bounds;
			if (bounds.Width <= 0 || bounds.Height <= 0 || !EnsureBuffer(e.Graphics, bounds))
			{
				base.OnDrawItem(e);
				return;
			}
			surface.SetClip(bounds);
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(surface, this, bounds);
			}
			using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, BackColor)))
			{
				surface.FillRectangle(brush, bounds);
			}
			try
			{
				base.OnDrawItem(new DrawItemEventArgs(surface, e.Font, bounds, e.Index, e.State, e.ForeColor, e.BackColor));
			}
			catch (Exception ex)
			{
				if (!drawFaultLogged)
				{
					drawFaultLogged = true;
					Logger.Log(Lang.T("log.techlistbox.1") + e.Index + " " + ex.GetType().Name + " " + ex.Message);
				}
			}
			finally
			{
				surface.ResetClip();
				IntPtr hdc = e.Graphics.GetHdc();
				try
				{
					BitBlt(hdc, bounds.Left, bounds.Top, bounds.Width, bounds.Height, memDc, bounds.Left, bounds.Top, 13369376);
				}
				finally
				{
					e.Graphics.ReleaseHdc(hdc);
				}
			}
		}

		private bool EnsureBuffer(Graphics target, Rectangle bounds)
		{
			int num = Math.Max(base.ClientSize.Width, bounds.Right);
			int num2 = Math.Max(base.ClientSize.Height, bounds.Bottom);
			if (num <= 0 || num2 <= 0)
			{
				return false;
			}
			if (memDc != IntPtr.Zero && bufW >= num && bufH >= num2)
			{
				return true;
			}
			ReleaseBuffer();
			IntPtr hdc = target.GetHdc();
			try
			{
				IntPtr intPtr = CreateCompatibleDC(hdc);
				if (intPtr == IntPtr.Zero)
				{
					return false;
				}
				IntPtr intPtr2 = CreateCompatibleBitmap(hdc, num, num2);
				if (intPtr2 == IntPtr.Zero)
				{
					DeleteDC(intPtr);
					return false;
				}
				memDc = intPtr;
				memBmp = intPtr2;
				oldBmp = SelectObject(intPtr, intPtr2);
				bufW = num;
				bufH = num2;
			}
			finally
			{
				target.ReleaseHdc(hdc);
			}
			try
			{
				surface = Graphics.FromHdc(memDc);
			}
			catch
			{
				ReleaseBuffer();
				return false;
			}
			return true;
		}

		private void ReleaseBuffer()
		{
			if (surface != null)
			{
				surface.Dispose();
				surface = null;
			}
			if (memDc != IntPtr.Zero)
			{
				if (oldBmp != IntPtr.Zero)
				{
					SelectObject(memDc, oldBmp);
				}
				DeleteDC(memDc);
				memDc = IntPtr.Zero;
				oldBmp = IntPtr.Zero;
			}
			if (memBmp != IntPtr.Zero)
			{
				DeleteObject(memBmp);
				memBmp = IntPtr.Zero;
			}
			bufW = 0;
			bufH = 0;
		}

		protected override void OnHandleDestroyed(EventArgs e)
		{
			viewportGeneration++;
			viewportQueued = false;
			redrawEnabled = true;
			wheelDelta = 0L;
			ReleaseBuffer();
			base.OnHandleDestroyed(e);
		}

		protected override void Dispose(bool disposing)
		{
			ReleaseBuffer();
			base.Dispose(disposing);
		}

		private void FillTail(IntPtr hdc)
		{
			int num = base.ClientSize.Height;
			int num2 = base.ClientSize.Width;
			if (num <= 0 || num2 <= 0)
			{
				return;
			}
			int num3 = 0;
			int count = base.Items.Count;
			if (count > 0 && ItemHeight > 0)
			{
				int num4 = base.TopIndex;
				if (num4 < 0)
				{
					num4 = 0;
				}
				int num5 = count - num4;
				if (num5 > 0)
				{
					num3 = ((num5 > num / ItemHeight + 2) ? num : (num5 * ItemHeight));
				}
				if (num3 > num)
				{
					num3 = num;
				}
			}
			if (num3 >= num)
			{
				return;
			}
			try
			{
				Rectangle rectangle = new Rectangle(0, num3, num2, num - num3);
				using (Graphics graphics = Graphics.FromHdc(hdc))
				{
					if (Backdrop.AppliesTo(this))
					{
						Backdrop.Paint(graphics, this, rectangle);
					}
					using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, BackColor)))
					{
						graphics.FillRectangle(brush, rectangle);
					}
				}
			}
			catch
			{
			}
		}
	}
}
