// @author bdth 2074055628@qq.com
// 文件用途 绘制状态指示点控件

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
    internal class StatusDot : FxControl
    {
        private Color color = Theme.Dim;
        private bool pulse;
        private float phase;

        public Color Color { get { return color; } set { if (color != value) { color = value; Invalidate(); } } }
        public bool Pulse { get { return pulse; } set { if (pulse != value) { pulse = value; if (value) UiClock.Wake(); Invalidate(); } } }

        public StatusDot() { Cursor = Cursors.Default; }

        protected override void OnFrame(object s, EventArgs e) { if (pulse) { if (Visible) UiClock.Wake(); phase += 0.09f; if (phase > 6.2832f) phase -= 6.2832f; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2, r = Dpi.S(5);
            if (pulse)
            {
                float k = (float)((Math.Sin(phase) + 1) / 2);
                int rr = r + (int)(Dpi.S(6) * k);
                using (var ring = new SolidBrush(Col.Alpha(color, (int)(80 * (1 - k)))))
                    g.FillEllipse(ring, cx - rr, cy - rr, rr * 2, rr * 2);
            }
            using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }
    }


}
