// @author bdth 2074055628@qq.com
// File purpose Status indicator dot control
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

        // Dots meaning the app is running use the theme accent and must follow the tier
        //   Do not enable this for status semantic colors (green/red/yellow); they express state, not theme
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
