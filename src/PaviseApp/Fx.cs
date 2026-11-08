using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class Fx
	{
		private sealed class FormEntry
		{
			private const int DurationMs = 180;

			private readonly Form form;

			private readonly Timer timer;

			private readonly Stopwatch watch = new Stopwatch();

			private bool done;

			public FormEntry(Form target)
			{
				form = target;
				timer = new Timer();
				timer.Interval = 15;
				timer.Tick += OnTick;
				form.VisibleChanged += OnGone;
				form.Disposed += OnGone;
				form.Opacity = 0.0;
				watch.Start();
				timer.Start();
			}

			private void OnTick(object sender, EventArgs e)
			{
				if (form.IsDisposed || !form.Visible || UiClock.Frozen)
				{
					Finish();
					return;
				}
				double totalMillisecondsRatio = watch.Elapsed.TotalMilliseconds / 180.0;
				if (totalMillisecondsRatio >= 1.0)
				{
					Finish();
					return;
				}
				double remainingTotalMillisecondsRatio = 1.0 - totalMillisecondsRatio;
				form.Opacity = 1.0 - remainingTotalMillisecondsRatio * remainingTotalMillisecondsRatio * remainingTotalMillisecondsRatio;
			}

			private void OnGone(object sender, EventArgs e)
			{
				Finish();
			}

			private void Finish()
			{
				if (!done)
				{
					done = true;
					timer.Stop();
					timer.Tick -= OnTick;
					timer.Dispose();
					watch.Stop();
					form.VisibleChanged -= OnGone;
					form.Disposed -= OnGone;
					if (!form.IsDisposed)
					{
						DropLayered(form);
					}
				}
			}
		}

		public static void EnterForm(Form form)
		{
			if (form != null && !form.IsDisposed && form.Visible)
			{
				if (UiClock.Frozen)
				{
					DropLayered(form);
				}
				else
				{
					new FormEntry(form);
				}
			}
		}

		internal static void DropLayered(Form f)
		{
			try
			{
				if (f != null && f.AllowTransparency)
				{
					f.Opacity = 1.0;
					f.AllowTransparency = false;
					f.Invalidate(true);
				}
			}
			catch
			{
			}
		}
	}
}
