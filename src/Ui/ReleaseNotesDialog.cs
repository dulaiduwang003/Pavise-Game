// @author bdth 2074055628@qq.com
// 文件用途 展示内置的版本说明

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ReleaseNotesDialog : Form
    {
        private const int DlgW = 640, DlgH = 520;
        private bool clockWasSuspended;

        public ReleaseNotesDialog()
        {
            Text = Lang.T("notes.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(Theme.S(DlgW), Theme.S(DlgH));
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);

            var title = new Label();
            title.Text = Lang.T("notes.title");
            title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg; title.Font = Theme.UI(14f, true);
            title.UseCompatibleTextRendering = false;
            title.SetBounds(Theme.S(22), Theme.S(18), Theme.S(DlgW - 100), Theme.S(30));
            title.MouseDown += DragMove;

            var lblClose = new Label();
            lblClose.Text = "✕";
            lblClose.ForeColor = Theme.Dim; lblClose.BackColor = Theme.Bg;
            lblClose.TextAlign = ContentAlignment.MiddleCenter;
            lblClose.Cursor = Cursors.Hand;
            lblClose.SetBounds(Theme.S(DlgW - 46), Theme.S(16), Theme.S(26), Theme.S(26));
            lblClose.Click += delegate { Close(); };

            var note = new Label();
            note.Text = Lang.F("notes.sub", App.VersionTag);
            note.ForeColor = Theme.Dim; note.BackColor = Theme.Bg; note.Font = Theme.UI(8.5f, false);
            note.UseCompatibleTextRendering = false;
            note.SetBounds(Theme.S(22), Theme.S(50), Theme.S(DlgW - 44), Theme.S(22));

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(22), Theme.S(80), Theme.S(DlgW - 44), Theme.S(DlgH - 146));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);

            int cardW = Theme.S(DlgW - 44 - 24);
            int textW = cardW - Theme.S(60);
            Font bodyFont = Theme.UI(8.8f, false);
            int sy = 0;

            foreach (ReleaseNote rn in ReleaseNotes.All)
            {
                bool current = string.Equals(rn.Version, App.Version, StringComparison.OrdinalIgnoreCase);
                var heights = new int[rn.Count];
                int bodyH = 0;
                for (int i = 0; i < rn.Count; i++)
                {
                    heights[i] = MeasureItem(rn.Item(i), textW, bodyFont);
                    bodyH += heights[i] + Theme.S(9);
                }

                var card = new NoteCard(rn, current, textW, bodyFont, heights);
                card.SetBounds(Theme.S(2), sy, cardW, Theme.S(46) + bodyH + Theme.S(10));
                card.Fill = Theme.Card; card.Border = Theme.Stroke; card.AccentEdge = current;
                card.Radius = Theme.S(12);

                scroll.Controls.Add(card);
                sy += card.Height + Theme.S(14);
            }

            var btnRepo = new PillButton(Lang.T("notes.online"), BtnKind.Normal);
            btnRepo.Bg = Theme.Bg;
            btnRepo.SetBounds(Theme.S(22), Theme.S(DlgH - 52), Theme.S(200), Theme.S(36));
            btnRepo.Click += delegate
            {
                try { using (System.Diagnostics.Process.Start(App.ReleasesUrl)) { } } catch { }
            };

            var btnClose = new PillButton(Lang.T("notes.close"), BtnKind.Primary);
            btnClose.Bg = Theme.Bg;
            btnClose.SetBounds(Theme.S(DlgW - 162), Theme.S(DlgH - 52), Theme.S(140), Theme.S(36));
            btnClose.Click += delegate { Close(); };

            Controls.AddRange(new Control[] { title, lblClose, note, scroll, btnRepo, btnClose });
            MouseDown += DragMove;
            ReleaseNotes.MarkSeen();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            clockWasSuspended = UiClock.Borrow();
            Fx.EnterForm(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UiClock.Return(clockWasSuspended);
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }

        private sealed class NoteCard : RoundPanel
        {
            private const TextFormatFlags LineFlags =
                TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
            private const TextFormatFlags BodyFlags =
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;

            private readonly string verText, dateText;
            private readonly string[] items;
            private readonly int[] heights;
            private readonly int textW;
            private readonly Font verFont, dateFont, bodyFont;
            private readonly Color verInk;

            public NoteCard(ReleaseNote rn, bool current, int textWidth, Font body, int[] itemHeights)
            {
                verText = rn.Tag + (current ? " " + Lang.T("notes.current") : "");
                dateText = rn.Date;
                items = new string[rn.Count];
                for (int i = 0; i < rn.Count; i++) items[i] = rn.Item(i);
                heights = itemHeights;
                textW = textWidth;
                bodyFont = body;
                verFont = Theme.UI(11f, true);
                dateFont = Theme.UI(8f, false);
                verInk = current ? Theme.Accent : Theme.Fg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                TextRenderer.DrawText(g, verText, verFont,
                    new Rectangle(Theme.S(20), Theme.S(12), Width - Theme.S(140), Theme.S(24)),
                    verInk, LineFlags);
                TextRenderer.DrawText(g, dateText, dateFont,
                    new Rectangle(Width - Theme.S(118), Theme.S(14), Theme.S(98), Theme.S(20)),
                    Theme.Faint, LineFlags | TextFormatFlags.Right);

                int iy = Theme.S(44);
                for (int i = 0; i < items.Length; i++)
                {
                    TextRenderer.DrawText(g, items[i], bodyFont,
                        new Rectangle(Theme.S(38), iy, textW, heights[i]), Theme.Dim, BodyFlags);
                    iy += heights[i] + Theme.S(9);
                }
            }
        }

        private static int MeasureItem(string text, int widthPx, Font font)
        {
            if (string.IsNullOrEmpty(text)) return Theme.S(18);
            Size s = TextRenderer.MeasureText(text, font, new Size(widthPx, 0), TextFormatFlags.WordBreak);
            return Math.Max(Theme.S(18), s.Height + Theme.S(2));
        }
    }
}
