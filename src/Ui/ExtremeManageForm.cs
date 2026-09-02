// @author bdth 2074055628@qq.com
// 文件用途 极限模式清单管理 只做减法 停用持久项立即按账本还原
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ExtremeManageForm : Form
    {
        private const int DlgW = 520;
        private const int DlgH = 620;

        private sealed class Row
        {
            public string Id;          // 会话键本名 全局 g:token 环境项 token
            public string Label;
            public string Group;
            public ExtremeItem Env;    // 环境项才有 承担停用时的即时还原与重新写入
        }

        private static readonly HashSet<string> GpuKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvShaderCache,
            PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyIntelLowLatency,
        };

        private readonly List<Row> rows = new List<Row>();
        private readonly Panel scrollBody;

        public ExtremeManageForm()
        {
            BuildRows();
            Text = Lang.T("extreme.manage.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            BackColor = Theme.Bg; ForeColor = Theme.Fg; Font = Theme.UI(9.5f, false);
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            ClientSize = new Size(Theme.S(DlgW), Theme.S(DlgH));
            Native.Dark(this);

            var title = new Label
            {
                Text = Lang.T("extreme.manage.title"),
                ForeColor = Theme.Accent, BackColor = Theme.Bg,
                Font = Theme.UI(13f, true), UseCompatibleTextRendering = false
            };
            title.SetBounds(Theme.S(22), Theme.S(16), Theme.S(DlgW - 100), Theme.S(28));
            Controls.Add(title);

            var close = new Label
            {
                Text = "✕", ForeColor = Theme.Dim, BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
            };
            close.SetBounds(Theme.S(DlgW - 46), Theme.S(14), Theme.S(26), Theme.S(26));
            close.Click += delegate { Close(); };
            Controls.Add(close);

            var sub = new Label
            {
                Text = Lang.T("extreme.manage.sub"), ForeColor = Theme.Dim, BackColor = Theme.Bg,
                Font = Theme.UI(7.8f, false), UseCompatibleTextRendering = false
            };
            sub.SetBounds(Theme.S(22), Theme.S(46), Theme.S(DlgW - 44), Theme.S(34));
            Controls.Add(sub);

            scrollBody = new Panel { BackColor = Theme.Bg, AutoScroll = true };
            scrollBody.SetBounds(0, Theme.S(86), Theme.S(DlgW), Theme.S(DlgH - 86 - 54));
            Native.Dark(scrollBody);
            Controls.Add(scrollBody);
            FillBody();

            var reset = new PillButton(Lang.T("extreme.manage.reset"));
            reset.Bg = Theme.Card;
            reset.SetBounds(Theme.S(22), Theme.S(DlgH - 46), Theme.S(130), Theme.S(32));
            reset.Click += delegate { ResetAll(); };
            Controls.Add(reset);

            var ok = new PillButton(Lang.T("extreme.manage.close"), BtnKind.Primary);
            ok.SetBounds(Theme.S(DlgW - 130), Theme.S(DlgH - 46), Theme.S(108), Theme.S(32));
            ok.Click += delegate { Close(); };
            Controls.Add(ok);
        }

        private void BuildRows()
        {
            foreach (string key in ExtremeMode.SessionPolicyKeys)
                rows.Add(new Row { Id = key, Label = Lang.T(PolicyCatalog.ItemOf(key).LangKey),
                    Group = GpuKeys.Contains(key) ? "gpu" : "session" });
            rows.Add(new Row { Id = PolicyCatalog.KeyNvLowLat, Group = "gpu",
                Label = Lang.T(PolicyCatalog.ItemOf(PolicyCatalog.KeyNvLowLat).LangKey) });
            rows.Add(new Row { Id = "g:dwmboost", Label = Lang.T("gm.dwmboost"), Group = "session" });
            rows.Add(new Row { Id = "g:rsssteer", Label = Lang.T("gm.rsssteer"), Group = "session" });
            rows.Add(new Row { Id = "g:gpupower", Label = Lang.T("t.gamemodeenv.6"), Group = "gpu" });
            rows.Add(new Row { Id = "g:gpuprefstage", Label = Lang.T("set.gpupref"), Group = "gpu" });
            rows.Add(new Row { Id = "g:autoecogpu", Label = Lang.T("set.autogpu"), Group = "gpu" });
            foreach (ExtremeItem item in ExtremeMode.EnvItems())
            {
                // nicim 只留在 EnvItems 承担旧极限账本的恢复，不再是极限档可跟随项。
                if (item.Token == "nicim") continue;
                rows.Add(new Row { Id = item.Token, Label = Lang.T(item.LangKey), Group = "env",
                    Env = item });
            }
        }

        private void FillBody()
        {
            scrollBody.SuspendLayout();
            scrollBody.Controls.Clear();
            int y = Theme.S(2);
            y = AddGroup("session", Lang.T("extreme.group.session"), y);
            y = AddGroup("gpu", Lang.T("extreme.group.gpu"), y);
            AddGroup("env", Lang.T("extreme.group.env"), y);
            scrollBody.ResumeLayout();
        }

        private int AddGroup(string group, string caption, int y)
        {
            var header = new Label
            {
                Text = caption, ForeColor = Theme.Dim, BackColor = Theme.Bg,
                Font = Theme.UI(7.8f, true), UseCompatibleTextRendering = false
            };
            header.SetBounds(Theme.S(22), y, Theme.S(DlgW - 60), Theme.S(18));
            scrollBody.Controls.Add(header);
            y += Theme.S(22);
            foreach (Row row in rows)
            {
                if (row.Group != group) continue;
                y = AddRow(row, y);
            }
            return y + Theme.S(10);
        }

        private int AddRow(Row row, int y)
        {
            var card = new RoundPanel
            {
                Fill = Theme.Card, Border = Theme.Stroke, BackColor = Theme.Bg, Radius = Theme.S(8)
            };
            card.SetBounds(Theme.S(20), y, Theme.S(DlgW - 60), Theme.S(34));

            var name = new Label
            {
                Text = row.Label, ForeColor = Theme.Fg, BackColor = Theme.Card,
                Font = Theme.UI(8.2f, false), AutoEllipsis = true, UseCompatibleTextRendering = false
            };
            name.SetBounds(Theme.S(12), Theme.S(8), card.Width - Theme.S(80), Theme.S(18));
            card.Controls.Add(name);

            var sw = new Toggle();
            sw.SetBounds(card.Width - Theme.S(58), Theme.S(5), Theme.S(44), Theme.S(24));
            sw.SetSilently(!ExtremeMode.OptedOut(row.Id));
            Row captured = row;
            sw.CheckedChanged += delegate { OnRowToggle(captured, sw.Checked); };
            card.Controls.Add(sw);

            scrollBody.Controls.Add(card);
            return y + Theme.S(38);
        }

        // 开关朝右是跟随极限 关掉即停用 停用持久项立即按账本还原
        private void OnRowToggle(Row row, bool follow)
        {
            ExtremeMode.SetOptedOut(row.Id, !follow);
            Logger.Log(Lang.T(follow ? "log.extreme.4" : "log.extreme.3") + row.Label);
            if (row.Env == null) return;
            if (follow) IrqMutationBoundary.Run(delegate { ExtremeMode.ApplySingle(row.Env); });
            else IrqMutationBoundary.Run(delegate { ExtremeMode.RevertSingle(row.Env); });
        }

        private void ResetAll()
        {
            foreach (Row row in rows)
            {
                if (!ExtremeMode.OptedOut(row.Id)) continue;
                ExtremeMode.SetOptedOut(row.Id, false);
                if (row.Env != null)
                    IrqMutationBoundary.Run(delegate { ExtremeMode.ApplySingle(row.Env); });
            }
            FillBody();
        }
    }
}
