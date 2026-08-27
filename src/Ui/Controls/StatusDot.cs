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

namespace PaviseApp
{
    internal class StatusDot : FxControl
    {
        private Color color = Theme.Dim;
        private bool pulse;
        private float phase;
        private bool followAccent;

        public Color Color { get { return color; } set { if (color != value) { color = value; Invalidate(); } } }

        // 表示"程序在跑"这类点用主题强调色 得跟着档位换
        //   状态语义色 绿的红的黄的 不要开这个 它们表达的是状态不是主题
        public bool FollowAccent
        {
            get { return followAccent; }
            set { if (followAccent == value) return; followAccent = value; Invalidate(); }
        }

        private Color PaintColor { get { return followAccent ? Theme.Accent : color; } }
        public bool Pulse { get { return pulse; } set { if (pulse != value) { pulse = value; if (value) UiClock.WakeSlow(); Invalidate(); } } }

        public StatusDot() { Cursor = Cursors.Default; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiClock.SlowFrame += OnSlowFrame;
            if (pulse && Visible) UiClock.WakeSlow();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.SlowFrame -= OnSlowFrame;
            base.OnHandleDestroyed(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (pulse && Visible) UiClock.WakeSlow();
        }

        private void OnSlowFrame(object sender, EventArgs e)
        {
            if (!pulse || !Visible) return;
            UiClock.WakeSlow();
            phase += 0.72f;
            if (phase > 6.2832f) phase -= 6.2832f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2, r = Dpi.S(5);
            Color paint = PaintColor;
            if (pulse)
            {
                float k = (float)((Math.Sin(phase) + 1) / 2);
                int rr = r + (int)(Dpi.S(6) * k);
                using (var ring = new SolidBrush(Col.Alpha(paint, (int)(80 * (1 - k)))))
                    g.FillEllipse(ring, cx - rr, cy - rr, rr * 2, rr * 2);
            }
            using (var b = new SolidBrush(paint)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }
    }

}
