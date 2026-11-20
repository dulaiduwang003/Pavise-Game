using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class Dpi
    {
        public const float MaxScale = 3f;

        public const float FitEpsilon = 0.02f;

        private const string ScalePreferenceKey = "UiScalePercent";

        private static int scalePreferencePercent;

        private static int currentDpi = 96;

        private static Rectangle currentWorkingArea;

        public static float Scale = 1f;

        public static bool NoFit;

        private static int fitW;

        private static int fitH;

        public static int ScalePreferencePercent
        {
            get { return scalePreferencePercent; }
        }

        public static void Init()
        {
            currentWorkingArea = Rectangle.Empty;
            try
            {
                Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
            }
            catch
            {
            }
            currentDpi = 96;
            try
            {
                currentDpi = (int)Native.GetDpiForSystem();
                if (currentDpi <= 0) currentDpi = 96;
            }
            catch
            {
                currentDpi = 96;
            }
            int storedScale;
            scalePreferencePercent = int.TryParse(Settings.LoadStr(ScalePreferenceKey, "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out storedScale) && IsScalePreferencePercent(storedScale) ? storedScale : 0;
            Scale = Fit(RequestedScaleFor(currentDpi));
        }

        internal static bool IsScalePreferencePercent(int percent)
        {
            return percent == 0 || (percent >= 100 && percent <= 300 && percent % 25 == 0);
        }

        private static float RequestedScaleFor(int dpi)
        {
            if (scalePreferencePercent > 0)
            {
                return (float)scalePreferencePercent / 100f;
            }
            float dpiScale = (float)dpi / 96f;
            return Math.Max(1f, dpiScale);
        }

        public static bool SetScalePreference(int percent)
        {
            if (!IsScalePreferencePercent(percent)) return false;
            scalePreferencePercent = percent;
            float nextScale = Fit(RequestedScaleFor(currentDpi));
            if (Math.Abs(nextScale - Scale) < 0.001f) return false;
            Scale = nextScale;
            return true;
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
                Rectangle area = currentWorkingArea;
                if (area.Width <= 0 || area.Height <= 0) area = Screen.PrimaryScreen.WorkingArea;
                if (area.Width <= 0 || area.Height <= 0) return scale;
                float cap = Math.Min(area.Width / (float)fitW, area.Height / (float)fitH);
                if (cap < 1f) cap = 1f;
                return scale > cap ? cap : scale;
            }
            catch { return scale; }
        }

        public static float ScaleFor(int dpi)
        {
            return Fit(RequestedScaleFor(dpi > 0 ? dpi : 96));
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
            if (upperBound > MaxScale)
            {
                upperBound = MaxScale;
            }
            if (scalePreferencePercent > 0)
            {
                // 客户区已提供可用空间 不能再套用另一块屏幕的适配上限
                upperBound = Math.Min(upperBound, RequestedScaleFor(currentDpi));
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
            if (dpi <= 0)
            {
                return false;
            }
            currentDpi = dpi;
            if (!WouldChange(dpi))
            {
                return false;
            }
            Scale = ScaleFor(dpi);
            return true;
        }

        internal static bool UpdateWindowEnvironment(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            int previousDpi = currentDpi;
            Rectangle previousArea = currentWorkingArea;
            int dpi = WindowDpi(hwnd);
            if (dpi > 0) currentDpi = dpi;
            try
            {
                Rectangle area = Screen.FromHandle(hwnd).WorkingArea;
                if (area.Width > 0 && area.Height > 0) currentWorkingArea = area;
            }
            catch { }
            return previousDpi != currentDpi || previousArea != currentWorkingArea;
        }

        public static bool UpdateWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            UpdateWindowEnvironment(hwnd);
            return Update(currentDpi);
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
