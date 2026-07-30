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

        public static float ScaleFor(int dpi)
        {
            float next = dpi / 96f;
            // 低于 100% 的缩放会把固定布局压塌，一律夹到 1
            return next < 1f ? 1f : next;
        }

        // 只判断不写。调用方需要先知道"这个 DPI 值到底会不会改变缩放"，
        // 才能决定要不要连带整窗重建——不能先改了再决定。
        public static bool WouldChange(int dpi)
        {
            if (dpi <= 0) return false;
            return Math.Abs(ScaleFor(dpi) - Scale) >= 0.001f;
        }

        // 进程声明的是 PerMonitorV2，所以切换显示器、改分辨率或改缩放都会换 DPI。
        // 缩放变了以后所有已算好的控件坐标和缓存字体都作废，必须整窗重建，
        // 否则系统只把顶层窗口放大、内容留在左上角，放大出来的部分是空背景。
        //
        // 只允许在紧接着就会整窗重建的地方调用。改了 Scale 却不重建，界面会停在
        // 一半新一半旧的状态：布局坐标是旧缩放算死的，而自绘控件在 OnPaint 里
        // 现取字体，拿到的是新缩放的字号，于是文字溢出、裁切、控件错位。
        public static bool Update(int dpi)
        {
            if (!WouldChange(dpi)) return false;
            Scale = ScaleFor(dpi);
            return true;
        }

        // 启动时用的是系统 DPI；窗口真正落在哪块屏上要用它自己的 DPI 校正。
        // 这里只读不写，改不改缩放由调用方连同重建一起决定。
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
