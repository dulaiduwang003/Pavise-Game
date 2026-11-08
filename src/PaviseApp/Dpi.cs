using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class Dpi
	{
		public const float MaxScale = 3f;

		public const float FitEpsilon = 0.02f;

		public static float Scale = 1f;

		public static bool NoFit;

		private static int fitW;

		private static int fitH;

		public static void Init()
		{
			try
			{
				Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
			}
			catch
			{
			}
			try
			{
				Scale = (float)Native.GetDpiForSystem() / 96f;
			}
			catch
			{
				Scale = 1f;
			}
			if (Scale < 1f)
			{
				Scale = 1f;
			}
			Scale = Fit(Scale);
		}

		public static bool SetDesignSize(int width, int height)
		{
			fitW = width;
			fitH = height;
			float fitResult = Fit(Scale);
			if (Math.Abs(fitResult - Scale) < 0.001f)
			{
				return false;
			}
			Scale = fitResult;
			return true;
		}

		internal static float Fit(float scale)
		{
			if (NoFit || fitW <= 0 || fitH <= 0) return scale;
			try
			{
				Rectangle area = Screen.PrimaryScreen.WorkingArea;
				if (area.Width <= 0 || area.Height <= 0) return scale;
				float cap = Math.Min(area.Width / (float)fitW, area.Height / (float)fitH);
				if (cap < 1f) cap = 1f;
				return scale > cap ? cap : scale;
			}
			catch { return scale; }
		}

		public static float ScaleFor(int dpi)
		{
			float dpiRatio = (float)dpi / 96f;
			if (dpiRatio < 1f)
			{
				dpiRatio = 1f;
			}
			return Fit(dpiRatio);
		}

		public static float FitScale(int clientW, int clientH)
		{
			if (fitW <= 0 || fitH <= 0 || clientW <= 0 || clientH <= 0)
			{
				return Scale;
			}
			float upperBound = Math.Min((float)clientW / (float)fitW, (float)clientH / (float)fitH);
			if (upperBound < 1f)
			{
				upperBound = 1f;
			}
			if (upperBound > 3f)
			{
				upperBound = 3f;
			}
			return upperBound;
		}

		public static bool FitDiffers(int clientW, int clientH)
		{
			return Math.Abs(FitScale(clientW, clientH) - Scale) >= 0.02f;
		}

		public static bool WouldChange(int dpi)
		{
			if (dpi <= 0)
			{
				return false;
			}
			return Math.Abs(ScaleFor(dpi) - Scale) >= 0.001f;
		}

		public static bool Update(int dpi)
		{
			if (!WouldChange(dpi))
			{
				return false;
			}
			Scale = ScaleFor(dpi);
			return true;
		}

		public static int WindowDpi(IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero)
			{
				return 0;
			}
			try
			{
				return (int)Native.GetDpiForWindow(hwnd);
			}
			catch
			{
				return 0;
			}
		}

		public static int S(int v)
		{
			return (int)Math.Round((float)v * Scale);
		}

		public static int U(int px)
		{
			if (!(Scale <= 0f))
			{
				return (int)Math.Round((float)px / Scale);
			}
			return px;
		}

		public static float CrispPoint(float points)
		{
			float roundLimit = (float)Math.Max(1.0, Math.Round(points * Scale * 96f / 72f));
			return roundLimit * 72f / (96f * Scale);
		}
	}
}
