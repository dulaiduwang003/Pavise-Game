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

namespace AegisApp
{
    internal static class Dpi
    {
        public static float Scale = 1f;

        public static void Init()
        {
            try { Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }
            try { Scale = Native.GetDpiForSystem() / 96f; } catch { Scale = 1f; }
            if (Scale < 1f) Scale = 1f;
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static float CrispPoint(float points)
        {
            float pixels = (float)Math.Max(1, Math.Round(points * Scale * 96f / 72f));
            return pixels * 72f / (96f * Scale);
        }
    }

}
