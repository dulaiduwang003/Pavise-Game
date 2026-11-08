using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class UiClock
	{
		internal const int FrameMs = 15;

		internal const int SlowMs = 200;

		internal const float BaselineFrameMs = 16.667f;

		internal const float MinDeltaScale = 0.25f;

		internal const float MaxDeltaScale = 4f;

		private static Timer timer;

		private static Timer slowTimer;

		private static int framesLeft;

		private static int slowFramesLeft;

		private static bool suspended;

		private static bool frozen;

		private static bool settling;

		private static readonly Stopwatch frameWatch = new Stopwatch();

		private static float deltaScale = 1f;

		public static float DeltaScale
		{
			get
			{
				return deltaScale;
			}
		}

		public static bool Frozen
		{
			get
			{
				return frozen;
			}
			set
			{
				if (frozen == value)
				{
					return;
				}
				frozen = value;
				if (value)
				{
					Ensure();
					framesLeft = 0;
					slowFramesLeft = 0;
					StopFrames();
					slowTimer.Stop();
					if (!suspended)
					{
						Settle();
					}
				}
			}
		}

		public static bool HasFrameListeners
		{
			get
			{
				return UiClock.Frame != null;
			}
		}

		public static bool Suspended
		{
			get
			{
				return suspended;
			}
			set
			{
				Ensure();
				if (suspended != value)
				{
					suspended = value;
					if (value)
					{
						framesLeft = 0;
						slowFramesLeft = 0;
						StopFrames();
						slowTimer.Stop();
					}
				}
			}
		}

		public static bool Running
		{
			get
			{
				if (timer != null)
				{
					return timer.Enabled;
				}
				return false;
			}
			set
			{
				Ensure();
				if (value)
				{
					Wake();
					return;
				}
				framesLeft = 0;
				StopFrames();
			}
		}

		public static event EventHandler Frame;

		public static event EventHandler SlowFrame;

		internal static float ScaleFor(double elapsedMs)
		{
			float elapsedMsRatio = (float)(elapsedMs / 16.66699981689453);
			if (elapsedMsRatio < 0.25f)
			{
				return 0.25f;
			}
			if (elapsedMsRatio > 4f)
			{
				return 4f;
			}
			return elapsedMsRatio;
		}

		private static void AdvanceDelta()
		{
			if (!frameWatch.IsRunning)
			{
				frameWatch.Reset();
				frameWatch.Start();
				deltaScale = 1f;
			}
			else
			{
				double totalMilliseconds = frameWatch.Elapsed.TotalMilliseconds;
				frameWatch.Reset();
				frameWatch.Start();
				deltaScale = ScaleFor(totalMilliseconds);
			}
		}

		private static void Ensure()
		{
			if (timer != null)
			{
				return;
			}
			timer = new Timer();
			timer.Interval = 15;
			timer.Tick += delegate
			{
				if (suspended)
				{
					framesLeft = 0;
					StopFrames();
				}
				else
				{
					AdvanceDelta();
					if (UiClock.Frame != null)
					{
						UiClock.Frame(null, EventArgs.Empty);
					}
					if (--framesLeft <= 0)
					{
						StopFrames();
					}
				}
			};
			slowTimer = new Timer();
			slowTimer.Interval = 200;
			slowTimer.Tick += delegate
			{
				if (suspended)
				{
					slowFramesLeft = 0;
					slowTimer.Stop();
				}
				else
				{
					if (UiClock.SlowFrame != null)
					{
						UiClock.SlowFrame(null, EventArgs.Empty);
					}
					if (--slowFramesLeft <= 0)
					{
						slowTimer.Stop();
					}
				}
			};
		}

		private static void StopFrames()
		{
			timer.Stop();
			frameWatch.Reset();
		}

		private static void Settle()
		{
			if (settling)
			{
				return;
			}
			settling = true;
			try
			{
				deltaScale = 1f;
				if (UiClock.Frame != null)
				{
					UiClock.Frame(null, EventArgs.Empty);
				}
			}
			finally
			{
				settling = false;
			}
		}

		public static void Wake(int frames = 48)
		{
			if (UiClock.Frame == null)
			{
				return;
			}
			Ensure();
			if (suspended)
			{
				return;
			}
			if (frozen)
			{
				Settle();
				return;
			}
			if (frames > framesLeft)
			{
				framesLeft = frames;
			}
			if (!timer.Enabled)
			{
				timer.Start();
			}
		}

		public static void WakeSlow(int frames = 12)
		{
			if (UiClock.SlowFrame == null)
			{
				return;
			}
			Ensure();
			if (!suspended && !frozen)
			{
				if (frames > slowFramesLeft)
				{
					slowFramesLeft = frames;
				}
				if (!slowTimer.Enabled)
				{
					slowTimer.Start();
				}
			}
		}

		public static bool Borrow()
		{
			bool suspendedSnapshot = suspended;
			if (suspendedSnapshot)
			{
				Suspended = false;
			}
			Wake();
			return suspendedSnapshot;
		}

		public static void Return(bool was)
		{
			if (was)
			{
				Suspended = true;
			}
		}
	}
}
