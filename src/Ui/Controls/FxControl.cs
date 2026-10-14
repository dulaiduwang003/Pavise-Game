// 作者：bdth 2074055628@qq.com
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
    // 带悬停/按压动效的自绘控件基类；忙碌状态由此统一提供，子类按 CanInteract 门禁激活。
    internal abstract class FxControl : Control, IBusyInteractive
    {
        protected Motion hover;
        protected Motion press;
        protected bool pressed;
        public Color Bg = Color.Empty;

        private bool busy;
        private float busyPhase;
        public event EventHandler BusyChanged;

        protected FxControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            hover.Speed = 0.28f;
            press.Speed = 0.42f;
        }

        protected Color ParentBg { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
        protected Color EffBg { get { return Bg != Color.Empty ? Bg : ParentBg; } }

        // 忙碌：保持可见与可读，拒绝激活；切换时重置扫光相位并取消按压。
        public bool Busy
        {
            get { return busy; }
            set
            {
                if (busy == value) return;
                busy = value;
                busyPhase = 0f;
                pressed = false;
                press.To(0f);
                if (value) UiClock.Wake();
                Invalidate();
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                EventHandler changed = BusyChanged;
                if (changed != null) changed(this, EventArgs.Empty);
            }
        }

        public bool CanInteract { get { return DsInteraction.CanActivate(this, busy); } }

        protected float BusyPhase { get { return busyPhase; } }

        protected DsState State
        {
            get
            {
                var s = new DsState();
                s.Enabled = Enabled; s.Busy = busy; s.Focused = Focused;
                s.Hover = hover.Value; s.Press = press.Value;
                return s;
            }
        }

        protected bool WantsFocusCue { get { return DsFocus.Wants(Focused, ShowFocusCues, CanInteract); } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UiClock.Frame += OnFrame; }
        protected override void OnHandleDestroyed(EventArgs e) { UiClock.Frame -= OnFrame; base.OnHandleDestroyed(e); }

        protected virtual bool StepAll()
        {
            bool a = hover.Step(); bool b = press.Step(); bool c = StepBusy();
            return a || b || c;
        }

        // 忙碌扫光随帧推进；冻结或挂起时钟下不推进，静态显示轨道。
        protected bool StepBusy()
        {
            if (!busy || UiClock.Frozen || UiClock.Suspended || !Visible) return false;
            busyPhase += 0.018f * UiClock.DeltaScale;
            if (busyPhase >= 1f) busyPhase -= 1f;
            UiClock.Wake(4);
            return true;
        }

        protected virtual void OnFrame(object s, EventArgs e) { if (StepAll()) Invalidate(); }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover.To(1f); UiClock.Wake(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pressed = false; press.To(0f); hover.To(0f); UiClock.Wake(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && !busy) { pressed = true; press.Set(1f); UiClock.Wake(); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; press.To(0f); UiClock.Wake(); Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            if (!CanInteract) return;
            base.OnClick(e);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new FxAccessibleObject(this); }

        // 辅助功能对象：忙碌时附加 Busy 状态，子类可继续扩展。
        protected class FxAccessibleObject : ControlAccessibleObject
        {
            private readonly FxControl fx;
            public FxAccessibleObject(FxControl owner) : base(owner) { fx = owner; }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = base.State;
                    if (fx.busy) state |= AccessibleStates.Busy;
                    return state;
                }
            }
        }

        protected void FillBg(Graphics g)
        {
            DsPaint.FillBackground(g, this, EffBg);
        }

        // 忙碌轨道绘制在给定区域底部，可选按切角路径裁剪。
        protected void PaintBusy(Graphics g, Rectangle bounds, GraphicsPath clip)
        {
            if (!busy) return;
            GraphicsState state = g.Save();
            try
            {
                if (clip != null) g.SetClip(clip, CombineMode.Intersect);
                DsPaint.BusyRail(g, bounds, busyPhase, Theme.Accent);
            }
            finally { g.Restore(state); }
        }
    }

}
