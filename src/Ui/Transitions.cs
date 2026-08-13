// @author bdth 2074055628@qq.com
// 文件用途 统一的界面过渡 面板切入 弹层落下 弹窗淡入 全项目的切换都走这里

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class Fx
    {
        internal const int PanelShift = 14;
        internal const float PanelSpeed = 0.28f;
        internal const int FlyoutDrop = 10;
        internal const float FlyoutSpeed = 0.24f;
        internal const int FormRise = 16;
        internal const float FormSpeed = 0.22f;

        private static readonly Dictionary<Control, Slide> live = new Dictionary<Control, Slide>();

        public static void SlideIn(Control target)
        {
            SlideIn(target, PanelShift, PanelSpeed);
        }

        public static void SlideIn(Control target, int shift, float speed)
        {
            Play(target, Theme.S(shift), 0, speed);
        }

        public static void DropIn(Control target)
        {
            DropIn(target, FlyoutDrop, FlyoutSpeed);
        }

        public static void DropIn(Control target, int shift, float speed)
        {
            Play(target, 0, Theme.S(shift), speed);
        }

        private static void Play(Control target, int dx, int dy, float speed)
        {
            if (target == null || target.IsDisposed || !target.Visible) return;
            Settle(target);
            live[target] = new Slide(target, dx, dy, speed);
            UiClock.Wake();
        }

        public static void Settle(Control target)
        {
            Slide old;
            if (target != null && live.TryGetValue(target, out old)) old.Finish();
        }

        // 弹窗不再做入场位移 直接显示
        // 位移只有十几个像素 无论怎么调曲线都只能落在十几个像素位置上 中间没有可插值的余地
        // 而窗口每动一次都要连着子控件整块重画 一次弹窗就是几十次全窗口重绘
        // 换来的是一段看得见的顿挫 不如不做
        public static void EnterForm(Form form)
        {
            EnterForm(form, FormRise);
        }

        public static void EnterForm(Form form, int rise)
        {
            if (form == null || form.IsDisposed || !form.Visible) return;
            DropLayered(form);
        }

        internal static void DropLayered(Form f)
        {
            try
            {
                if (f == null || !f.AllowTransparency) return;
                f.Opacity = 1.0;
                f.AllowTransparency = false;
                f.Invalidate(true);
            }
            catch { }
        }

        private sealed class Slide
        {
            private readonly Control target;
            private readonly int baseLeft, baseTop;
            private readonly int shiftX, shiftY;
            private Motion m;
            private bool done;

            public Slide(Control c, int dx, int dy, float speed)
            {
                target = c; baseLeft = c.Left; baseTop = c.Top;
                shiftX = dx; shiftY = dy;
                m.Speed = speed <= 0f ? PanelSpeed : speed;
                m.Set(1f); m.To(0f);
                Place(1f);
                target.Disposed += OnGone;
                UiClock.Frame += OnFrame;
            }

            private void Place(float v)
            {
                if (shiftX != 0) target.Left = baseLeft + (int)(v * shiftX);
                if (shiftY != 0) target.Top = baseTop + (int)(v * shiftY);
            }

            private void OnGone(object s, EventArgs e)
            {
                done = true;
                Detach();
            }

            private void OnFrame(object s, EventArgs e)
            {
                if (target.IsDisposed) { done = true; Detach(); return; }
                if (!m.Step()) { Finish(); return; }
                Place(m.Value);
            }

            public void Finish()
            {
                if (done) return;
                done = true;
                if (!target.IsDisposed) Place(0f);
                Detach();
            }

            private void Detach()
            {
                UiClock.Frame -= OnFrame;
                try { target.Disposed -= OnGone; } catch { }
                Slide cur;
                if (live.TryGetValue(target, out cur) && cur == this) live.Remove(target);
            }
        }

    }
}
