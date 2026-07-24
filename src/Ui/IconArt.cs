// @author bdth 2074055628@qq.com
// 文件用途 生成程序图标和托盘图像

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
    internal static class IconArt
    {
        private static readonly Color Ink   = Color.FromArgb(24, 26, 32);
        private static readonly Color Rim   = Color.FromArgb(40, 255, 255, 255);

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

        public static Icon MakeIcon(int px)
        {
            return MakeIcon(px, PerformancePreset.Standard, true);
        }

        public static Icon MakeIcon(int px, PerformancePreset mode, bool enabled)
        {
            using (var bmp = Render(px, mode, enabled))
            {
                IntPtr hIcon = bmp.GetHicon();
                try { using (var tmp = Icon.FromHandle(hIcon)) return (Icon)tmp.Clone(); }
                finally { DestroyIcon(hIcon); }
            }
        }

        public static Icon MakeMultiIcon()
        {
            return MakeMultiIcon(PerformancePreset.Standard, true);
        }

        public static Icon MakeMultiIcon(PerformancePreset mode, bool enabled)
        {
            byte[] data = IcoWriter.Build(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }, mode, enabled);
            using (var ms = new MemoryStream(data)) return new Icon(ms);
        }

        public static Bitmap Render(int px)
        {
            return Render(px, PerformancePreset.Standard, true);
        }

        public static Bitmap Render(int px, PerformancePreset mode, bool enabled)
        {
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                g.ScaleTransform(px / 100f, px / 100f);

                Color modeColor = Theme.ModeColor(mode);
                Color bolt = enabled ? modeColor : Col.Lerp(modeColor, Color.FromArgb(105, 110, 121), 0.58f);
                Color rim = enabled ? Col.Alpha(bolt, 105) : Rim;
                using (var badge = Squircle(2.5f, 2.5f, 95, 95, 25))
                {
                    using (var b = new SolidBrush(Ink)) g.FillPath(b, badge);
                    using (var pen = new Pen(rim, 1.8f)) g.DrawPath(pen, badge);
                }
                using (var b = new SolidBrush(bolt)) g.FillPolygon(b, BoltPts());
            }
            return bmp;
        }

        private static GraphicsPath Squircle(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath(); float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static PointF[] BoltPts()
        {
            PointF[] b = { new PointF(58, 26), new PointF(38, 54), new PointF(50, 54), new PointF(43, 76), new PointF(65, 44), new PointF(53, 44) };
            const float s = 1.36f;
            for (int i = 0; i < b.Length; i++) b[i] = new PointF(50 + (b[i].X - 50) * s, 50 + (b[i].Y - 50) * s);
            return b;
        }
    }

    internal static class IcoWriter
    {
        public static byte[] Build(int[] sizes)
        {
            return Build(sizes, PerformancePreset.Standard, true);
        }

        public static byte[] Build(int[] sizes, PerformancePreset mode, bool enabled)
        {
            var frames = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = IconArt.Render(s, mode, enabled))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    frames.Add(ms.ToArray());
                }

            using (var outMs = new MemoryStream())
            using (var w = new BinaryWriter(outMs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
                int offset = 6 + 16 * frames.Count;
                for (int i = 0; i < frames.Count; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
                w.Flush();
                return outMs.ToArray();
            }
        }

        public static void Save(string path, int[] sizes) { File.WriteAllBytes(path, Build(sizes)); }
    }


}
