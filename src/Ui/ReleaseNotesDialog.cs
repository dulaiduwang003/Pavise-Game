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

                var card = new RoundPanel();
                card.SetBounds(Theme.S(2), sy, cardW, Theme.S(46) + bodyH + Theme.S(10));
                card.Fill = Theme.Card; card.Border = Theme.Stroke; card.AccentEdge = current;
                card.Radius = Theme.S(12);

                var ver = new Label();
                ver.Text = rn.Tag + (current ? "  //  " + Lang.T("notes.current") : "");
                ver.ForeColor = current ? Theme.Accent : Theme.Fg;
                ver.BackColor = Theme.Card; ver.Font = Theme.UI(11f, true);
                ver.UseCompatibleTextRendering = false;
                ver.SetBounds(Theme.S(20), Theme.S(12), cardW - Theme.S(140), Theme.S(24));

                var date = new Label();
                date.Text = rn.Date;
                date.ForeColor = Theme.Faint; date.BackColor = Theme.Card; date.Font = Theme.UI(8f, false);
                date.TextAlign = ContentAlignment.MiddleRight;
                date.UseCompatibleTextRendering = false;
                date.SetBounds(cardW - Theme.S(118), Theme.S(14), Theme.S(98), Theme.S(20));

                card.Controls.Add(ver);
                card.Controls.Add(date);

                int iy = Theme.S(44);
                for (int i = 0; i < rn.Count; i++)
                {
                    var dot = new Label();
                    dot.Text = "·";
                    dot.ForeColor = Theme.Accent; dot.BackColor = Theme.Card; dot.Font = Theme.UI(10f, true);
                    dot.UseCompatibleTextRendering = false;
                    dot.SetBounds(Theme.S(22), iy, Theme.S(12), Theme.S(18));

                    var body = new Label();
                    body.Text = rn.Item(i);
                    body.ForeColor = Theme.Dim; body.BackColor = Theme.Card; body.Font = bodyFont;
                    body.UseCompatibleTextRendering = false;
                    body.SetBounds(Theme.S(38), iy, textW, heights[i]);

                    card.Controls.Add(dot);
                    card.Controls.Add(body);
                    iy += heights[i] + Theme.S(9);
                }

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

        private static int MeasureItem(string text, int widthPx, Font font)
        {
            if (string.IsNullOrEmpty(text)) return Theme.S(18);
            Size s = TextRenderer.MeasureText(text, font, new Size(widthPx, 0), TextFormatFlags.WordBreak);
            return Math.Max(Theme.S(18), s.Height + Theme.S(2));
        }
    }
}
