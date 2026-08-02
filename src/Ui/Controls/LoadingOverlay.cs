// @author bdth 2074055628@qq.com
// 文件用途 长耗时操作期间覆盖整个窗口的遮罩与转圈动画

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class LoadingOverlay : Control
    {
        private float angle;
        private string caption = "";
        private PillButton cancelButton;
        private Action cancelAction;

        public LoadingOverlay()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw, true);
            Visible = false;
            TabStop = false;
        }

        public string Caption
        {
            get { return caption; }
            set
            {
                string next = value ?? "";
                if (caption == next) return;
                caption = next;
                if (Visible) Invalidate();
            }
        }

        public void ShowOverlay(string text)
        {
            ShowOverlay(text, null, null);
        }

        public void ShowOverlay(string text, string cancelText, Action onCancel)
        {
            Caption = text;
            cancelAction = onCancel;
            if (onCancel != null)
            {
                if (cancelButton == null)
                {
                    cancelButton = new PillButton("", BtnKind.Normal);
                    cancelButton.Click += OnCancelClick;
                    Controls.Add(cancelButton);
                }
                cancelButton.Text = cancelText ?? "";
                cancelButton.Enabled = true;
                cancelButton.Visible = true;
                LayoutCancelButton();
            }
            else if (cancelButton != null) cancelButton.Visible = false;
            if (Visible) return;
            angle = 0f;
            Visible = true;
            BringToFront();
            UiClock.Frame += OnFrame;
            UiClock.Wake();
        }

        public void HideOverlay()
        {
            cancelAction = null;
            if (!Visible) return;
            UiClock.Frame -= OnFrame;
            Visible = false;
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            Action pending = cancelAction;
            cancelAction = null;
            if (cancelButton != null) cancelButton.Enabled = false;
            if (pending != null) { try { pending(); } catch { } }
        }

        private void LayoutCancelButton()
        {
            if (cancelButton == null) return;
            int bw = Theme.S(132);
            int bh = Theme.S(32);
            cancelButton.SetBounds((Width - bw) / 2, Height / 2 + Theme.S(88), bw, bh);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCancelButton();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.Frame -= OnFrame;
            base.OnHandleDestroyed(e);
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (!Visible) return;
            angle += 4.5f;
            if (angle >= 360f) angle -= 360f;
            UiClock.Wake();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var shade = new SolidBrush(Color.FromArgb(216, 8, 9, 12)))
                g.FillRectangle(shade, ClientRectangle);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int ring = Theme.S(46);
            int thickness = Math.Max(2, Theme.S(3));
            var box = new Rectangle(
                (Width - ring) / 2,
                (Height - ring) / 2 - Theme.S(14),
                ring,
                ring);

            using (var track = new Pen(Color.FromArgb(52, 255, 255, 255), thickness))
                g.DrawEllipse(track, box);
            using (var arc = new Pen(Theme.Accent, thickness))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, box, angle, 96f);
            }

            if (caption.Length == 0) return;
            Font font = Theme.UI(9.5f, false);
            using (var brush = new SolidBrush(Theme.Fg))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Near;
                var textBox = new Rectangle(
                    Theme.S(20),
                    box.Bottom + Theme.S(18),
                    Width - Theme.S(40),
                    Theme.S(48));
                g.DrawString(caption, font, brush, textBox, format);
            }
        }
    }
}
