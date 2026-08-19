// @author bdth 2074055628@qq.com
// 文件用途 帧卡顿溯源页 判决 证据 数据 处置全在这一页 不往日志页甩
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ExplainBar : Control
    {
        private string caption = "";
        private double value;
        private bool strong;

        public ExplainBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }

        public void Set(string text, double share, bool isStrong)
        {
            caption = text ?? "";
            value = share < 0 ? 0 : (share > 1 ? 1 : share);
            strong = isStrong;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int nameW = Dpi.S(96);
            int numW = Dpi.S(64);
            int barX = nameW;
            int barW = Width - nameW - numW;
            if (barW < Dpi.S(20)) return;
            int barH = Dpi.S(8);
            int barY = (Height - barH) / 2;

            using (var b = new SolidBrush(Theme.Dim))
                g.DrawString(caption, Theme.UI(8.5f, false), b, 0, (Height - Dpi.S(15)) / 2f);

            using (var track = new SolidBrush(Theme.Inset))
                g.FillRectangle(track, barX, barY, barW, barH);
            int fill = (int)Math.Round(barW * value);
            if (fill > 0)
                using (var br = new SolidBrush(strong ? Theme.Accent : Theme.Faint))
                    g.FillRectangle(br, barX, barY, fill, barH);

            string txt = (value * 100).ToString("F1") + "%";
            using (var b = new SolidBrush(strong ? Theme.Accent : Theme.Dim))
                g.DrawString(txt, Theme.Mono(8.5f), b, barX + barW + Dpi.S(6), (Height - Dpi.S(15)) / 2f);
        }
    }

    internal partial class PanelForm : Form
    {
        private DBPanel pageStutter;
        private Label lblStutterState;
        private Label lblStutterQuality;
        private Label lblStutterVerdict;
        private Label lblStutterPath;
        private Label lblStutterAction;
        private Label lblStutterBlocked;
        private ExplainBar barIntr, barCpu, barDisk;
        private PillButton btnStutterApply;
        private PillButton btnStutterRevert;
        private PillButton btnStutterExport;
        private TechListBox lstStutterWindows;
        private RemedyPlan stutterPlan;
        private int stutterRowCount = -1;
        private Toggle swStutterDiag, swStutterAct;
        private SettingCard cardStutterDiag, cardStutterAct;

        private void BuildStutterPage()
        {
            int top = PageHeader(pageStutter, Lang.T("nav.stutter"), Lang.T("stutter.sub"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(ContentX), Theme.S(top),
                Theme.S(ContentW + 12), Theme.S(PageH - top - 8));
            const int InnerW = ContentW - 12;
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageStutter.Controls.Add(scroll);
            int y = 2;

            Section(scroll, Lang.T("stutter.sec.switch"), 0, y);
            y += 22;
            int cardH;
            swStutterDiag = MakeSwitch(gameMode.FrameDiagOn, null);
            swStutterDiag.CheckedChanged += delegate
            {
                gameMode.FrameDiagOn = swStutterDiag.Checked;
                RefreshStutterPage();
            };
            cardStutterDiag = MakeAutoCard(scroll, 0, y, InnerW, 66,
                Lang.T("gm.framediag"), Lang.T("gm.framediag.sub"), swStutterDiag, out cardH);
            y += cardH + 6;
            policySync.Add(delegate { if (swStutterDiag != null) swStutterDiag.SetSilently(gameMode.FrameDiagOn); });

            swStutterAct = MakeSwitch(gameMode.FrameActOn, null);
            swStutterAct.CheckedChanged += delegate
            {
                gameMode.FrameActOn = swStutterAct.Checked;
                RefreshStutterPage();
            };
            cardStutterAct = MakeAutoCard(scroll, 0, y, InnerW, 66,
                Lang.T("gm.frameact"), Lang.T("gm.frameact.sub"), swStutterAct, out cardH);
            y += cardH + 10;
            policySync.Add(delegate { if (swStutterAct != null) swStutterAct.SetSilently(gameMode.FrameActOn); });

            Section(scroll, Lang.T("stutter.sec.state"), 0, y);
            y += 22;
            lblStutterState = CardLabel(scroll, "", 4, y, InnerW - 8, 18, 8.5f, false, Theme.Dim);
            y += 22;
            lblStutterQuality = CardLabel(scroll, "", 4, y, InnerW - 8, 22, 10f, true, Theme.Fg);
            y += 30;

            Section(scroll, Lang.T("stutter.sec.verdict"), 0, y);
            y += 22;
            lblStutterVerdict = CardLabel(scroll, "", 4, y, InnerW - 8, 24, 11.5f, true, Theme.Fg);
            y += 28;

            barIntr = MakeBar(scroll, 4, y); y += 20;
            barCpu = MakeBar(scroll, 4, y); y += 20;
            barDisk = MakeBar(scroll, 4, y); y += 24;

            lblStutterPath = CardLabel(scroll, "", 4, y, InnerW - 8, 34, 8.5f, false, Theme.Dim);
            y += 40;

            Section(scroll, Lang.T("stutter.sec.action"), 0, y);
            y += 22;
            lblStutterAction = CardLabel(scroll, "", 4, y, InnerW - 8, 22, 9.5f, true, Theme.Fg);
            y += 24;
            lblStutterBlocked = CardLabel(scroll, "", 4, y, InnerW - 8, 32, 8.5f, false, Theme.Faint);
            y += 38;

            btnStutterApply = new PillButton(Lang.T("stutter.apply"), BtnKind.Primary);
            btnStutterApply.SetBounds(Theme.S(0), Theme.S(y), Theme.S(180), Theme.S(32));
            btnStutterApply.Click += OnStutterApply;
            scroll.Controls.Add(btnStutterApply);

            btnStutterRevert = new PillButton(Lang.T("stutter.revert"), BtnKind.Normal);
            btnStutterRevert.SetBounds(Theme.S(188), Theme.S(y), Theme.S(130), Theme.S(32));
            btnStutterRevert.Click += OnStutterRevert;
            scroll.Controls.Add(btnStutterRevert);

            btnStutterExport = new PillButton(Lang.T("stutter.export"), BtnKind.Normal);
            btnStutterExport.SetBounds(Theme.S(326), Theme.S(y), Theme.S(150), Theme.S(32));
            btnStutterExport.Click += OnStutterExport;
            scroll.Controls.Add(btnStutterExport);
            y += 42;

            Section(scroll, Lang.T("stutter.sec.windows"), 0, y);
            y += 22;
            lstStutterWindows = new TechListBox();
            lstStutterWindows.SetBounds(Theme.S(0), Theme.S(y), Theme.S(InnerW), Theme.S(300));
            Theme.StyleList(lstStutterWindows, true);
            lstStutterWindows.ItemHeight = Theme.S(26);
            lstStutterWindows.Font = Theme.UI(8f, false);
            lstStutterWindows.HorizontalScrollbar = true;
            scroll.Controls.Add(lstStutterWindows);

            RefreshStutterPage();
        }

        private ExplainBar MakeBar(Control parent, int x, int y)
        {
            var b = new ExplainBar();
            b.SetBounds(Theme.S(x), Theme.S(y), Theme.S(ContentW - 28), Theme.S(18));
            b.BackColor = Theme.Bg;
            parent.Controls.Add(b);
            return b;
        }

        private void RefreshStutterPage()
        {
            if (lblStutterState == null) return;

            if (swStutterDiag != null)
            {
                bool admin = false;
                try { admin = Native.IsElevated(); } catch { }
                swStutterDiag.Enabled = admin;
                if (cardStutterDiag != null && !admin)
                    cardStutterDiag.Desc = Lang.T("gm.framediag.needadmin");
            }
            if (swStutterAct != null)
            {
                swStutterAct.Enabled = gameMode.FrameDiagOn;
                if (cardStutterAct != null)
                    cardStutterAct.Desc = gameMode.FrameDiagOn
                        ? Lang.T("gm.frameact.sub") : Lang.T("gm.frameact.needdiag");
            }

            bool running = false;
            try { running = FrameDiagnostics.Running; } catch { }
            double peak = 0; long lost = 0; bool frozen = false;
            try { peak = FrameDiagnostics.PeakSelfCpu; lost = FrameDiagnostics.LostEvents; frozen = FrameDiagnostics.ActionFrozen; } catch { }

            lblStutterState.Text = (running ? Lang.T("stutter.state.on") : Lang.T("stutter.state.off"))
                + "   " + Lang.F("stutter.overhead", (peak * 100).ToString("F3"))
                + (lost > 0 ? "   " + Lang.F("stutter.lost", lost) : "")
                + (frozen ? "   " + Lang.T("stutter.frozen") : "");
            lblStutterState.ForeColor = frozen ? Theme.Danger : (running ? Theme.Accent : Theme.Faint);

            FrameWindow w = null;
            try { w = FrameDiagnostics.LastWindow; } catch { }
            lblStutterQuality.Text = w == null || w.Frames == 0
                ? Lang.T("stutter.noframes")
                : Lang.F("stutter.quality",
                    w.MedianMs.ToString("F2"), w.P99Ms.ToString("F2"),
                    w.OnePercentLowMs.ToString("F2"), w.OverBudget, w.Frames);
            lblStutterQuality.Font = Theme.MonoFor(lblStutterQuality.Text, 10f);

            FrameFaultVerdict v = null;
            try { v = FrameDiagnostics.LastVerdict; } catch { }
            RemedyPlan p = null;
            try { p = FrameDiagnostics.CurrentRemedy(); } catch { }
            stutterPlan = p;

            if (v == null)
            {
                lblStutterVerdict.Text = Lang.T("stutter.none");
                barIntr.Set(Lang.T("stutter.dim.intr"), 0, false);
                barCpu.Set(Lang.T("stutter.dim.cpu"), 0, false);
                barDisk.Set(Lang.T("stutter.dim.disk"), 0, false);
                lblStutterPath.Text = Lang.T("stutter.path.none");
            }
            else
            {
                lblStutterVerdict.Text = ConclusionText(v.Conclusion);
                lblStutterVerdict.ForeColor = v.Conclusion == FaultConclusion.NoSlowFrames
                    ? Theme.Accent : Theme.Fg;
                barIntr.Set(Lang.T("stutter.dim.intr"), v.IntrExplained,
                    v.Conclusion == FaultConclusion.Interrupts || v.Conclusion == FaultConclusion.Mixed);
                barCpu.Set(Lang.T("stutter.dim.cpu"), v.PreemptExplained,
                    v.Conclusion == FaultConclusion.CpuPreemption || v.Conclusion == FaultConclusion.Mixed);
                barDisk.Set(Lang.T("stutter.dim.disk"), v.DiskExplained,
                    v.Conclusion == FaultConclusion.DiskStall);
                lblStutterPath.Text = v.PathSlowWalked > 0 || v.PathKnown
                    ? Lang.F("stutter.path", (v.PathCertShare * 100).ToString("F0"),
                        (v.PathCoverage * 100).ToString("F0"),
                        (v.PathSpineExplained * 100).ToString("F1"),
                        (v.PreemptExplained * 100).ToString("F1"))
                      + (v.PathKnown ? "" : "   " + v.PathWhy)
                    : Lang.T("stutter.path.none");
            }

            if (p == null)
            {
                lblStutterAction.Text = "";
                lblStutterBlocked.Text = "";
            }
            else
            {
                lblStutterAction.Text = p.Title;
                lblStutterAction.ForeColor = p.CanApply ? Theme.Accent : Theme.Dim;
                string tail = p.Blocked ?? "";
                if (!string.IsNullOrEmpty(p.Why)) tail = tail.Length > 0 ? p.Why + "\n" + tail : p.Why;
                lblStutterBlocked.Text = tail;
            }

            bool applied = false;
            try { applied = FrameRemedy.Applied || FrameRemedy.HasResidue; } catch { }
            btnStutterApply.Enabled = p != null && p.CanApply && !applied;
            btnStutterRevert.Enabled = applied;

            RefreshStutterWindows();
        }

        private void RefreshStutterWindows()
        {
            if (lstStutterWindows == null) return;
            string[] rows;
            try { rows = FrameDiagnostics.WindowRows(); } catch { return; }
            if (rows.Length == stutterRowCount) return;
            stutterRowCount = rows.Length;
            lstStutterWindows.BeginUpdate();
            lstStutterWindows.Items.Clear();
            if (rows.Length == 0) lstStutterWindows.Items.Add(Lang.T("stutter.nowindows"));
            else for (int i = rows.Length - 1; i >= 0; i--) lstStutterWindows.Items.Add(rows[i]);
            lstStutterWindows.EndUpdate();
        }

        private static string ConclusionText(FaultConclusion c)
        {
            switch (c)
            {
                case FaultConclusion.NoSlowFrames: return Lang.T("stutter.c.none");
                case FaultConclusion.Interrupts: return Lang.T("stutter.c.intr");
                case FaultConclusion.CpuPreemption: return Lang.T("stutter.c.cpu");
                case FaultConclusion.Mixed: return Lang.T("stutter.c.mixed");
                case FaultConclusion.DiskStall: return Lang.T("stutter.c.disk");
                case FaultConclusion.Elsewhere: return Lang.T("stutter.c.elsewhere");
                default: return Lang.T("stutter.c.unknown");
            }
        }

        private bool StutterNeedsAdmin()
        {
            if (elevated) return true;
            PaviseDialog.Warn(this, App.DisplayName, Lang.T("vbs.needadmin"));
            return false;
        }

        private void OnStutterApply(object sender, EventArgs e)
        {
            RemedyPlan p = stutterPlan;
            if (p == null || !p.CanApply) return;
            if (!StutterNeedsAdmin()) return;
            bool ok = false;
            try { ok = FrameRemedy.Apply(p); } catch { }
            Logger.Log((ok ? Lang.T("log.stutter.applied") : Lang.T("log.stutter.failed")) + p.Target);
            RefreshStutterPage();
        }

        private void OnStutterRevert(object sender, EventArgs e)
        {
            if (!StutterNeedsAdmin()) return;
            try { FrameRemedy.Revert(); } catch { }
            RefreshStutterPage();
        }

        private void OnStutterExport(object sender, EventArgs e)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "Pavise.stutter.txt");
                if (FrameDiagnostics.ExportTo(path))
                    PaviseDialog.Info(this, Lang.T("nav.stutter"), Lang.T("stutter.exported") + path);
                else PaviseDialog.Warn(this, App.DisplayName, Lang.T("stutter.exportfail"));
            }
            catch { PaviseDialog.Warn(this, App.DisplayName, Lang.T("stutter.exportfail")); }
        }
    }
}
