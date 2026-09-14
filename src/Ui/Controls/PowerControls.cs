// @author bdth 2074055628@qq.com
// File purpose Title-bar match power plan: chamfered button and picker overlay; Don't switch / PG managed / local plan
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class PowerButton : FxControl
    {
        private bool active;
        private string label = "";
        public Action Clicked;

        public PowerButton()
        {
            Bg = Theme.Bg;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            AccessibleName = Lang.T("power.name");
        }

        public void SetState(bool on, string text)
        {
            string v = text ?? "";
            if (active == on && label == v) return;
            active = on; label = v;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Clicked != null) Clicked();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; FillBg(g); g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value;
            // The inset must go through Theme.S so the visible borders of the four title-bar controls line up at high DPI
            Rectangle r = new Rectangle(0, Theme.S(2), Width - 1, Height - Theme.S(5));
            using (GraphicsPath p = Theme.TechPath(r, Theme.S(9)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, h))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent,
                    (active ? 0.46f : 0.30f) + h * 0.45f))) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, label, Theme.UI(9.25f, true),
                new Rectangle(Theme.S(14), Theme.S(5), Width - Theme.S(42), Theme.S(22)),
                active ? Theme.Fg : Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Lang.T("plan.pick.title"),
                Theme.UI(7.25f, false), new Rectangle(Theme.S(14), Theme.S(24), Width - Theme.S(42), Theme.S(15)),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            PointF[] chevron = {
                new PointF(Width - Theme.S(20), Theme.S(18)),
                new PointF(Width - Theme.S(15), Theme.S(23)),
                new PointF(Width - Theme.S(10), Theme.S(18)) };
            using (var pen = new Pen(Theme.Dim, Math.Max(1f, Theme.S(1)))) g.DrawLines(pen, chevron);
        }
    }

    internal sealed class PowerChoiceRow : FxControl
    {
        public readonly string Id;
        private readonly string label;
        private readonly MenuBadge badge;
        private readonly bool selected;
        public Action<string> Chosen;

        public PowerChoiceRow(string id, string text, MenuBadge tag, bool on)
        {
            Id = id; label = text; badge = tag; selected = on;
            Bg = Theme.Card;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Chosen != null) Chosen(Id);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; FillBg(g); g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.TechPath(r, Theme.S(8)))
            {
                Color fill = selected
                    ? Col.Lerp(Theme.Card, Theme.Accent, 0.13f)
                    : Col.Lerp(Theme.Card, Theme.CardHover, h);
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(selected ? Col.Alpha(Theme.Accent, 215)
                    : Col.Lerp(Theme.Stroke, Theme.StrokeHi, h))) g.DrawPath(pen, p);
            }
            int dot = Theme.S(8);
            int cy = Height / 2;
            if (selected)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, Theme.S(14), cy - dot / 2, dot, dot);
            else
                using (var pen = new Pen(Theme.Faint))
                    g.DrawEllipse(pen, Theme.S(14), cy - dot / 2, dot, dot);

            int badgeW = 0;
            if (badge != null)
            {
                Font bf = Theme.MonoFor(badge.Text, 7.0f);
                Size bs = TextRenderer.MeasureText(g, badge.Text, bf);
                badgeW = bs.Width + Theme.S(12);
                var pill = new Rectangle(Width - Theme.S(14) - badgeW, cy - Theme.S(17) / 2,
                    badgeW, Theme.S(17));
                Color fill = badge.Strong ? Theme.Accent : Theme.Inset;
                Color edge = badge.Strong ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke;
                Color ink = badge.Strong ? Theme.OnAccent : Theme.Faint;
                using (GraphicsPath path = Theme.TechPath(pill, Theme.S(4)))
                {
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var p = new Pen(edge)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, badge.Text, bf, pill, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                badgeW += Theme.S(10);
            }

            var tr = new Rectangle(Theme.S(32), 0, Width - Theme.S(32 + 16) - badgeW, Height);
            TextRenderer.DrawText(g, label, Theme.UI(9.25f, selected),
                tr, selected ? Theme.Fg : Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class PowerFlyout : RoundPanel
    {
        private readonly Label title;
        private readonly Label sub;
        private readonly List<PowerChoiceRow> rows = new List<PowerChoiceRow>();
        public Action<string> Chosen;

        public PowerFlyout()
        {
            UseBackdrop = false;
            Fill = Theme.Card; Border = Theme.StrokeHi; BackColor = Theme.Bg; Radius = Theme.S(14); AccentEdge = true;
            title = new Label
            {
                Text = Lang.T("plan.pick.title"), ForeColor = Theme.Fg, BackColor = Theme.Card,
                Font = Theme.UI(11f, true), AutoEllipsis = true
            };
            title.UseCompatibleTextRendering = false;
            title.SetBounds(Theme.S(18), Theme.S(13), Theme.S(356), Theme.S(25));
            sub = new Label
            {
                ForeColor = Theme.Dim, BackColor = Theme.Card,
                Font = Theme.UI(7.8f, false), AutoEllipsis = true
            };
            sub.UseCompatibleTextRendering = false;
            sub.SetBounds(Theme.S(18), Theme.S(38), Theme.S(356), Theme.S(19));
            Controls.AddRange(new Control[] { title, sub });
        }

        public void Open(bool switching, string effectiveId)
        {
            foreach (PowerChoiceRow row in rows) row.Dispose();
            rows.Clear();
            sub.Text = Lang.F("plan.fly.cur", PowerPlan.CurrentPlanLabel());

            var ids = new List<string> { null, PowerPlan.ManagedChoice };
            var labels = new List<string> { Lang.T("plan.pick.off"), PowerPlan.ManagedPlanTitle };
            var badges = new List<MenuBadge> { null, new MenuBadge(Lang.T("plan.pick.managed"), true) };
            foreach (PowerPlanEntry entry in PowerPlan.ListUserPlans())
            {
                ids.Add(entry.Id.ToString());
                labels.Add(entry.Name);
                badges.Add(new MenuBadge(Lang.T("plan.pick.local"), false));
            }

            int y = Theme.S(64);
            for (int i = 0; i < ids.Count; i++)
            {
                bool on = switching
                    ? string.Equals(ids[i], effectiveId, StringComparison.OrdinalIgnoreCase)
                    : ids[i] == null;
                var row = new PowerChoiceRow(ids[i], labels[i], badges[i], on);
                row.SetBounds(Theme.S(14), y, Theme.S(368), Theme.S(44));
                row.Chosen = delegate(string id) { if (Chosen != null) Chosen(id); };
                Controls.Add(row);
                rows.Add(row);
                y += Theme.S(50);
            }
            Height = y + Theme.S(12);
        }
    }
}
