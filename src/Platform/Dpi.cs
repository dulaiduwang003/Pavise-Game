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

        // 进程声明的是 PerMonitorV2，所以切换显示器、改分辨率或改缩放都会换 DPI。
        // 缩放变了以后所有已算好的控件坐标和缓存字体都作废，必须整窗重建，
        // 否则系统只把顶层窗口放大、内容留在左上角，放大出来的部分是空背景。
        public static bool Update(int dpi)
        {
            if (dpi <= 0) return false;
            float next = dpi / 96f;
            if (next < 1f) next = 1f;
            if (Math.Abs(next - Scale) < 0.001f) return false;
            Scale = next;
            return true;
        }

        // 启动时用的是系统 DPI；窗口真正落在哪块屏上要用它自己的 DPI 校正一次
        public static bool SyncToWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return Update((int)Native.GetDpiForWindow(hwnd)); }
            catch { return false; }
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static float CrispPoint(float points)
        {
            float pixels = (float)Math.Max(1, Math.Round(points * Scale * 96f / 72f));
            return pixels * 72f / (96f * Scale);
        }
    }

}
