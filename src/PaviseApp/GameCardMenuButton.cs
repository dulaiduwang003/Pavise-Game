using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameCardMenuButton : PillButton
	{
		private const int MenuGap = 5;

		private readonly DropdownHost host;

		internal bool MenuVisible
		{
			get
			{
				return host.IsVisible;
			}
		}

		internal ContextMenuStrip OwnedMenu
		{
			get
			{
				return host.Menu;
			}
		}

		internal Rectangle OwnedMenuPlacement
		{
			get
			{
				return host.LastPlacement;
			}
		}

		internal GameCardMenuButton()
			: base("")
		{
			host = new DropdownHost(this);
			host.AlignRight = true;
			host.GapLogical = 5;
			host.EdgeMarginLogical = 0;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			float width = Math.Max(2, Theme.S(3));
			float scaledDimension = Theme.S(5);
			float ratio = ((float)base.Width - width) / 2f;
			float top = ((float)base.Height - width) / 2f;
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (SolidBrush brush = new SolidBrush(base.Enabled ? Theme.Fg : Theme.Faint))
			{
				for (int i = -1; i <= 1; i++)
				{
					e.Graphics.FillEllipse(brush, ratio + (float)i * scaledDimension, top, width, width);
				}
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left && base.CanInteract)
			{
				host.NoteMouseDown();
				Focus();
			}
			base.OnMouseDown(e);
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			if (e.Button == MouseButtons.Left)
			{
				host.NoteMouseUp();
			}
		}

		protected override void OnClick(EventArgs e)
		{
			if (base.CanInteract && !base.IsDisposed && !host.ConsumeCloseClick())
			{
				base.OnClick(e);
			}
		}

		protected override bool ProcessCmdKey(ref Message message, Keys keys)
		{
			if (host.HandleCommandKey(keys, true, base.PerformClick))
			{
				return true;
			}
			return base.ProcessCmdKey(ref message, keys);
		}

		internal void ShowMenu(ContextMenuStrip next)
		{
			host.Show(next);
		}

		internal void CloseMenu()
		{
			host.Close();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				host.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
