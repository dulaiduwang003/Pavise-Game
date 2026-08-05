// @author bdth 2074055628@qq.com
// 文件用途 构建系统体检页 聚合展示本机能力 实测数据 持久设置与带依据的结论

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private const int QuickAuditWindowMs = 3000;
        private const int PreciseAuditWindowMs = 30000;

        private DBPanel pageAudit;
        private DBPanel auditScroll;
        private Label lblAuditStatus;
        private PillButton btnAuditQuick, btnAuditPrecise, btnAuditNv;
        private int auditBusy;
        private string auditNvProbeText;

        private void BuildAuditPage()
        {
            int y = PageHeader(pageAudit, Lang.T("nav.audit"), Lang.T("audit.sub"), 2);

            btnAuditQuick = new PillButton(Lang.T("audit.rerun"), BtnKind.Primary);
            btnAuditQuick.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(150), Theme.S(34));
            btnAuditQuick.Click += delegate { StartAudit(QuickAuditWindowMs); };
            pageAudit.Controls.Add(btnAuditQuick);

            btnAuditPrecise = new PillButton(Lang.T("audit.precise"), BtnKind.Normal);
            btnAuditPrecise.SetBounds(Theme.S(ContentX + 158), Theme.S(y), Theme.S(170), Theme.S(34));
            btnAuditPrecise.Click += delegate { StartAudit(PreciseAuditWindowMs); };
            pageAudit.Controls.Add(btnAuditPrecise);

            btnAuditNv = new PillButton(Lang.T("audit.nvprobe"), BtnKind.Normal);
            btnAuditNv.SetBounds(Theme.S(ContentX + 336), Theme.S(y), Theme.S(170), Theme.S(34));
            btnAuditNv.Click += delegate { StartNvProbe(); };
            pageAudit.Controls.Add(btnAuditNv);

            lblAuditStatus = CardLabel(pageAudit, "", ContentX + 516, y + 8, ContentW - 516, 20, 8.0f, false, Theme.Dim);
            y += 44;

            auditScroll = new DBPanel();
            auditScroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            auditScroll.BackColor = Theme.Bg;
            auditScroll.AutoScroll = true;
            Native.Dark(auditScroll);
            pageAudit.Controls.Add(auditScroll);
        }

        private void StartAudit(int windowMs)
        {
            if (Interlocked.Exchange(ref auditBusy, 1) == 1) return;
            SetAuditButtons(false);
            lblAuditStatus.Text = windowMs >= 10000
                ? Lang.T("audit.measuring.long") : Lang.T("audit.measuring");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                AuditReport report = null;
                try { report = SystemAudit.Collect(windowMs); } catch { }
                Interlocked.Exchange(ref auditBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        SetAuditButtons(true);
                        lblAuditStatus.Text = report == null ? Lang.T("audit.failed") : "";
                        if (report != null) RenderAudit(report);
                    }));
                }
                catch { }
            });
        }

        private void StartNvProbe()
        {
            if (!NvApi.Available)
            {
                lblAuditStatus.Text = Lang.T("audit.nv.unavailable");
                return;
            }
            if (Interlocked.Exchange(ref auditBusy, 1) == 1) return;
            SetAuditButtons(false);
            lblAuditStatus.Text = Lang.T("audit.nv.probing");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string summary;
                try
                {
                    var results = NvDrsTweaks.ProbeWriteback();
                    if (results.Count == 0) summary = Lang.T("audit.nv.failed");
                    else
                    {
                        int ok = 0;
                        var parts = new System.Collections.Generic.List<string>();
                        foreach (var r in results)
                        {
                            if (r.Ok) ok++;
                            parts.Add(r.Key + "=" + r.Outcome);
                        }
                        summary = ok + "/" + results.Count + " 项写入生效（" + string.Join("，", parts.ToArray()) + "）";
                    }
                }
                catch (Exception ex) { summary = Lang.T("audit.nv.failed") + " " + ex.Message; }
                auditNvProbeText = summary;
                AuditReport report = null;
                try { report = SystemAudit.Collect(QuickAuditWindowMs); } catch { }
                Interlocked.Exchange(ref auditBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        SetAuditButtons(true);
                        lblAuditStatus.Text = "";
                        if (report != null) RenderAudit(report);
                    }));
                }
                catch { }
            });
        }

        private void SetAuditButtons(bool enabled)
        {
            btnAuditQuick.Enabled = enabled;
            btnAuditPrecise.Enabled = enabled;
            btnAuditNv.Enabled = enabled;
        }

        private void RenderAudit(AuditReport report)
        {
            auditScroll.SuspendLayout();
            // 先滚回顶部再重建：AutoScroll 容器会把滚动偏移加到新控件坐标上，
            // 带着偏移重建会在列表顶部留下一段空白
            auditScroll.AutoScrollPosition = Point.Empty;
            // 复制后再释放：Dispose 会把控件从 Controls 里摘掉，直接在 foreach 里做会跳过一半
            var stale = new Control[auditScroll.Controls.Count];
            auditScroll.Controls.CopyTo(stale, 0);
            auditScroll.Controls.Clear();
            foreach (Control c in stale) c.Dispose();

            int sy = 2;
            sy = RenderAuditGroup(Lang.T("audit.sec.capability"), report.Capability, sy);
            sy = RenderAuditGroup(Lang.T("audit.sec.machine"), report.Machine, sy);
            sy = RenderAuditGroup(Lang.T("audit.sec.persistent"), report.Persistent, sy);
            sy = RenderAuditGroup(Lang.T("audit.sec.verdicts"), report.Verdicts, sy);

            CardLabel(auditScroll, Lang.T("audit.footer"), 8, sy + 4, ScrollContentW - 16, 34, 7.6f, false, Theme.Faint);
            auditScroll.ResumeLayout();
            auditScroll.PerformLayout();
            auditScroll.AutoScrollPosition = Point.Empty;
        }

        private int RenderAuditGroup(string title, System.Collections.Generic.List<AuditRow> rows, int sy)
        {
            Section(auditScroll, title, 6, sy);
            sy += 26;
            foreach (AuditRow row in rows)
            {
                var panel = MakeConsolePanel(auditScroll, 6, sy, ScrollContentW, 64, false);
                CardLabel(panel, row.Name, 16, 10, 236, 20, 8.6f, true, Theme.Fg);
                CardLabel(panel, row.Value, 256, 10, ScrollContentW - 380, 20, 8.6f, true,
                    row.Warn ? Theme.Accent : Theme.Fg);
                CardLabel(panel, Lang.T("audit.evidence") + row.Evidence,
                    ScrollContentW - 118, 10, 104, 20, 7.4f, false, Theme.Faint);
                string note = row.Note ?? "";
                if (auditNvProbeText != null && row.Name == "NVIDIA 驱动接口")
                    note = Lang.T("audit.nv.result") + auditNvProbeText;
                CardLabel(panel, note, 16, 34, ScrollContentW - 32, 24, 7.8f, false, Theme.Dim);
                sy += 72;
            }
            sy += 8;
            return sy;
        }
    }
}
