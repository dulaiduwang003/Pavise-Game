using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class PerformanceInformationTip
	{
		[ThreadStatic]
		private static ToolTip tip;

		[ThreadStatic]
		private static Timer delay;

		[ThreadStatic]
		private static Control target;

		[ThreadStatic]
		private static string text;

		[ThreadStatic]
		private static int owners;

		internal static void AddOwner()
		{
			owners++;
		}

		internal static void ReleaseOwner()
		{
			if (--owners <= 0)
			{
				owners = 0;
				if (delay != null)
				{
					delay.Stop();
					delay.Dispose();
					delay = null;
				}
				if (tip != null)
				{
					tip.Dispose();
					tip = null;
				}
				target = null;
				text = null;
			}
		}

		internal static void Schedule(Control owner, string details)
		{
			if (owner == null || owner.IsDisposed)
			{
				return;
			}
			if (target != null)
			{
				Cancel(target);
			}
			target = owner;
			text = details ?? "";
			if (delay == null)
			{
				Timer timer = new Timer();
				timer.Interval = 350;
				delay = timer;
				delay.Tick += delegate
				{
					delay.Stop();
					if (target != null && !target.IsDisposed && target.Visible && target.IsHandleCreated && target.ClientRectangle.Contains(target.PointToClient(Cursor.Position)))
					{
						EnsureTip();
						tip.Show(text, target, new Point(0, target.Height + Theme.S(4)), 24000);
					}
				};
			}
			delay.Start();
		}

		internal static void Refresh(Control owner, string details)
		{
			if (object.ReferenceEquals(target, owner))
			{
				text = details ?? "";
			}
        }
        internal static void Show(Control owner, string details)
        {
            if (owner == null || owner.IsDisposed || !owner.Visible || !owner.IsHandleCreated) return;
            if (target != null) Cancel(target);
            target = owner;
            text = details ?? "";
            EnsureTip();
            tip.Show(text, target, new Point(0, target.Height + Theme.S(4)), 24000);
        }

		internal static void Cancel(Control owner)
		{
			if (object.ReferenceEquals(target, owner))
			{
				if (delay != null)
				{
					delay.Stop();
				}
				if (tip != null && target != null && !target.IsDisposed && target.IsHandleCreated)
				{
					tip.Hide(target);
				}
				target = null;
				text = "";
			}
		}

		private static void EnsureTip()
		{
			if (tip == null)
			{
				ToolTip toolTip = new ToolTip();
				toolTip.OwnerDraw = true;
				toolTip.ShowAlways = true;
				tip = toolTip;
				tip.Popup += delegate(object sender, PopupEventArgs e)
				{
					int maxLimit = Math.Min(Theme.S(460), Math.Max(Theme.S(200), Screen.FromControl(e.AssociatedControl).WorkingArea.Width - Theme.S(32)));
					Size size = TextRenderer.MeasureText(text, Theme.UI(9f, false), new Size(Math.Max(1, maxLimit - Theme.S(28)), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
					e.ToolTipSize = new Size(Math.Min(maxLimit, Math.Max(Theme.S(200), size.Width + Theme.S(28))), size.Height + Theme.S(24));
				};
				tip.Draw += delegate(object sender, DrawToolTipEventArgs e)
				{
					e.Graphics.Clear(Theme.Card);
					Rectangle r = new Rectangle(0, 0, Math.Max(1, e.Bounds.Width - 1), Math.Max(1, e.Bounds.Height - 1));
					DsPaint.TechFrame(e.Graphics, r, Theme.S(6), Theme.Card, Theme.StrokeHi);
					TextRenderer.DrawText(e.Graphics, text, Theme.UI(9f, false), Rectangle.Inflate(e.Bounds, -Theme.S(14), -Theme.S(12)), Theme.Fg, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
				};
			}
		}
	}
}
