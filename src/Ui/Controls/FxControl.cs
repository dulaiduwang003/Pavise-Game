// @author bdth 2074055628@qq.com
// 文件用途 提供支持动画刷新的控件基类

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
    internal abstract class FxControl : Control
    {
        protected Motion hover;
        protected bool pressed;
        public Color Bg = Color.Empty;

        protected FxControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            hover.Speed = 0.28f;
        }

        protected Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
        protected Color EffBg { get { return Bg != Color.Empty ? Bg : ParentBg; } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UiClock.Frame += OnFrame; }
        protected override void OnHandleDestroyed(EventArgs e) { UiClock.Frame -= OnFrame; base.OnHandleDestroyed(e); }

        protected virtual bool StepAll() { return hover.Step(); }
        protected virtual void OnFrame(object s, EventArgs e) { if (StepAll()) Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1f); UiClock.Wake(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pressed = false; hover.To(0f); UiClock.Wake(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }

        protected void FillBg(Graphics g) { using (var b = new SolidBrush(EffBg)) g.FillRectangle(b, ClientRectangle); }
    }


}
