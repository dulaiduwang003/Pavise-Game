// @author bdth 2074055628@qq.com
// 文件用途 面板各页通用的控件工厂 页眉 分节 开关与设置卡

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private int PageHeader(DBPanel page, string title, string sub, int subLines)
        {
            var rail = new AccentLine();
            rail.SetBounds(Theme.S(26), Theme.S(5), Theme.S(28), Math.Max(1, Theme.S(2)));
            page.Controls.Add(rail);

            var sys = new Label();
            sys.Text = "PAVISE  //  CONTROL";
            sys.ForeColor = Theme.Faint; sys.BackColor = Theme.Bg;
            sys.Font = Theme.Mono(6.75f);
            sys.UseCompatibleTextRendering = false;
            sys.SetBounds(Theme.S(62), 0, Theme.S(190), Theme.S(14));
            page.Controls.Add(sys);

            var t = new Label();
            t.Text = title;
            t.ForeColor = Theme.Fg; t.BackColor = Theme.Bg;
            t.Font = Theme.UI(14.5f, true);
            t.UseCompatibleTextRendering = false;
            t.SetBounds(Theme.S(26), Theme.S(17), Theme.S(ContentW - 80), Theme.S(32));
            page.Controls.Add(t);
            int y = 50;
            if (!string.IsNullOrEmpty(sub))
            {
                var s2 = new Label();
                s2.Text = sub;
                s2.ForeColor = Theme.Dim; s2.BackColor = Theme.Bg;
                s2.Font = Theme.UI(8.5f, false);
                s2.UseCompatibleTextRendering = false;
                s2.AutoEllipsis = true;
                s2.SetBounds(Theme.S(27), Theme.S(y), Theme.S(ContentW - 2), Theme.S(16 * subLines + 2));
                page.Controls.Add(s2);
                y += 16 * subLines + 8;
            }
            return y + 8;
        }

        private Label Section(Control parent, string text, int x, int y)
        {
            var mark = new AccentLine();
            mark.SetBounds(Theme.S(x + 4), Theme.S(y + 5), Theme.S(3), Theme.S(8));
            parent.Controls.Add(mark);
            var l = new Label();
            l.Text = text;
            l.ForeColor = Theme.Faint; l.BackColor = Theme.Bg;
            l.Font = Theme.UI(8.25f, true);
            l.UseCompatibleTextRendering = false;
            l.SetBounds(Theme.S(x + 14), Theme.S(y), Theme.S(400), Theme.S(18));
            parent.Controls.Add(l);
            return l;
        }

        private Toggle MakeSwitch(bool on, EventHandler handler)
        {
            var t = new Toggle();
            t.Size = new Size(Theme.S(46), Theme.S(24));
            t.Bg = Theme.Card;
            t.SetSilently(on);
            if (handler != null) t.CheckedChanged += handler;
            return t;
        }

        private int AutoCardHeight(string desc, int cardW, Control host, int minHeight)
        {
            if (string.IsNullOrEmpty(desc)) return minHeight;
            int padL = Theme.S(18);
            int reserve = padL + (host != null ? host.Width + Theme.S(14) : 0);
            int textW = Theme.S(cardW) - padL - reserve;
            if (textW <= 0) return minHeight;
            Font font = Theme.UI(8.5f, false);
            int lineH = TextRenderer.MeasureText("Ag", font).Height;
            if (lineH <= 0) return minHeight;
            int need = TextRenderer.MeasureText(
                desc, font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak).Height;
            int lines = (need + lineH - 1) / lineH;
            if (lines < 1) lines = 1;
            int scale100 = Theme.S(100);
            if (scale100 <= 0) return minHeight;
            int px = lines * lineH + Theme.S(44);
            int logical = (px * 100 + scale100 - 1) / scale100;
            return logical > minHeight ? logical : minHeight;
        }

        private SettingCard MakeAutoCard(
            Control parent, int x, int y, int w, int minH, string title, string desc, Control host, out int used)
        {
            used = AutoCardHeight(desc, w, host, minH);
            return MakeCard(parent, x, y, w, used, title, desc, host);
        }

        private SettingCard MakeCard(Control parent, int x, int y, int w, int h, string title, string desc, Control host)
        {
            var c = new SettingCard();
            c.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            c.Title = title;
            c.Desc = desc ?? "";
            if (host != null) c.Host(host);
            parent.Controls.Add(c);
            return c;
        }

        private RoundPanel MakeConsolePanel(Control parent, int x, int y, int width, int height, bool accent)
        {
            var panel = new RoundPanel();
            panel.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(height));
            panel.BackColor = Theme.Bg; panel.Fill = Theme.Card; panel.Border = Theme.Stroke;
            panel.Radius = Theme.S(14); panel.AccentEdge = accent;
            parent.Controls.Add(panel); return panel;
        }

        private Label CardLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            var label = new Label();
            label.Text = text; label.ForeColor = color; label.BackColor = Color.Transparent;
            label.Font = Theme.UI(size, bold); label.AutoEllipsis = true;
            label.UseCompatibleTextRendering = false;
            label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            parent.Controls.Add(label); return label;
        }

        private DialogResult ShowDim(Form dlg)
        {
            Opacity = 0.55;
            try { return dlg.ShowDialog(this); }
            finally { Opacity = 1.0; }
        }
    }
}
