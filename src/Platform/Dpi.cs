// @author bdth 2074055628@qq.com
// 文件用途 处理高分屏缩放和窗口坐标换算

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class Dpi
    {
        public static float Scale = 1f;

        private static int fitW, fitH;

        public static void Init()
        {
            try { Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }
            try { Scale = Native.GetDpiForSystem() / 96f; } catch { Scale = 1f; }
            if (Scale < 1f) Scale = 1f;
            Scale = Fit(Scale);
        }

        public static bool SetDesignSize(int width, int height)
        {
            fitW = width; fitH = height;
            float next = Fit(Scale);
            if (Math.Abs(next - Scale) < 0.001f) return false;
            Scale = next;
            return true;
        }

        internal static float Fit(float scale)
        {
            if (fitW <= 0 || fitH <= 0) return scale;
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
            float next = dpi / 96f;
            if (next < 1f) next = 1f;
            return Fit(next);
        }

        public const float MaxScale = 3f;
        public const float FitEpsilon = 0.02f;

        public static float FitScale(int clientW, int clientH)
        {
            if (fitW <= 0 || fitH <= 0 || clientW <= 0 || clientH <= 0) return Scale;
            float next = Math.Min(clientW / (float)fitW, clientH / (float)fitH);
            if (next < 1f) next = 1f;
            if (next > MaxScale) next = MaxScale;
            return next;
        }

        public static bool FitDiffers(int clientW, int clientH)
        {
            return Math.Abs(FitScale(clientW, clientH) - Scale) >= FitEpsilon;
        }

        public static bool WouldChange(int dpi)
        {
            if (dpi <= 0) return false;
            return Math.Abs(ScaleFor(dpi) - Scale) >= 0.001f;
        }

        public static bool Update(int dpi)
        {
            if (!WouldChange(dpi)) return false;
            Scale = ScaleFor(dpi);
            return true;
        }

        public static int WindowDpi(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try { return (int)Native.GetDpiForWindow(hwnd); }
            catch { return 0; }
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static float CrispPoint(float points)
        {
            float pixels = (float)Math.Max(1, Math.Round(points * Scale * 96f / 72f));
            return pixels * 72f / (96f * Scale);
        }
    }

}
