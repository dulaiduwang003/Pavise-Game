// @author bdth 2074055628@qq.com
// 文件用途 构建反作弊专项页 逐分组的压制档位与开关
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private DBPanel acList;
        private Toggle swAcMaster;
        private RoundPanel acRosterBar;
        private Label lblAcRoster;
        private ModuleBanner acBanner;
        private readonly List<AcGroup> acGroups = new List<AcGroup>();
        private readonly List<SettingCard> acCards = new List<SettingCard>();
        private readonly List<Toggle> acToggles = new List<Toggle>();

        private void BuildAntiCheatPage()
        {
            int y = PageHeader(pageAntiCheat, Lang.T("v14.anticheat"), Lang.T("v15.anticheat.sub"), 2);
            acBanner = new ModuleBanner();
            acBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            acBanner.Code = "DEFENSE BOUNDARY // 02";
            acBanner.TitleText = Lang.T("v14.anticheat.boundary");
            acBanner.Detail = Lang.T("v14.anticheat.master.sub");
            acBanner.Glyph = "acshield";
            pageAntiCheat.Controls.Add(acBanner);
            y += 84;

            // 总开关是整页的闸 相容名单是运行期记录 两者都不是"一个反作弊分组"
            //   做成和分组同款的卡片会串层级 编号还会和下面的列表各自从 01 重来
            //   收成一条 44 高的工具条 明显矮于 104 高的分组卡片 一眼分得开
            acRosterBar = new RoundPanel();
            acRosterBar.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(44));
            acRosterBar.BackColor = Theme.Bg;
            acRosterBar.Fill = Theme.Inset;
            acRosterBar.Border = Theme.Stroke;
            acRosterBar.Radius = Theme.S(10);
            pageAntiCheat.Controls.Add(acRosterBar);

            swAcMaster = MakeSwitch(!tamer.Paused, delegate
            {
                tamer.Paused = !swAcMaster.Checked;
                Settings.Save("TameOn", swAcMaster.Checked);
                RefreshAcGroupStates();
            });
            swAcMaster.Bg = Theme.Inset;
            swAcMaster.Location = new Point(Theme.S(12),
                (acRosterBar.Height - swAcMaster.Height) / 2);
            acRosterBar.Controls.Add(swAcMaster);

            var lblMaster = new Label();
            lblMaster.AutoSize = false;
            lblMaster.BackColor = Color.Transparent;
            lblMaster.Font = Theme.UI(8.6f, true);
            lblMaster.ForeColor = Theme.Fg;
            lblMaster.TextAlign = ContentAlignment.MiddleLeft;
            lblMaster.SetBounds(Theme.S(68), 0, Theme.S(150), acRosterBar.Height);
            lblMaster.Text = Lang.T("tame.toggle");
            acRosterBar.Controls.Add(lblMaster);

            var btnRoster = new PillButton(Lang.T("btn.roster.clear"));
            btnRoster.Size = new Size(Theme.S(84), Theme.S(26));
            btnRoster.Location = new Point(
                acRosterBar.Width - Theme.S(12) - btnRoster.Width,
                (acRosterBar.Height - btnRoster.Height) / 2);
            btnRoster.Click += delegate { OnClearRoster(); };
            acRosterBar.Controls.Add(btnRoster);

            lblAcRoster = new Label();
            lblAcRoster.AutoSize = false;
            lblAcRoster.BackColor = Color.Transparent;
            lblAcRoster.Font = Theme.UI(8.2f, false);
            lblAcRoster.TextAlign = ContentAlignment.MiddleRight;
            lblAcRoster.SetBounds(Theme.S(224), 0,
                acRosterBar.Width - Theme.S(224) - Theme.S(104), acRosterBar.Height);
            acRosterBar.Controls.Add(lblAcRoster);
            y += 54;
            SyncAcRoster();
            acList = new DBPanel();
            acList.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            acList.BackColor = Theme.Bg; acList.AutoScroll = true; Native.Dark(acList); pageAntiCheat.Controls.Add(acList);
            RefreshAcList();
        }

        private void SyncAcRoster()
        {
            if (lblAcRoster == null) return;
            string[] names = ProtectedGameRoster.Names();
            bool empty = names.Length == 0;
            lblAcRoster.Text = Lang.T("ac.roster") + "  ·  "
                + (empty ? Lang.T("ac.roster.empty") : string.Join(" · ", names));
            lblAcRoster.ForeColor = empty ? Theme.Faint : Theme.Accent;
        }

        private void OnClearRoster()
        {
            if (ProtectedGameRoster.Names().Length == 0)
            {
                PaviseDialog.Info(this, Lang.T("ac.roster"), Lang.T("ac.roster.empty"));
                return;
            }
            if (!PaviseDialog.Confirm(this, Lang.T("ac.roster"), Lang.T("ac.roster.confirm"), DlgKind.Warn)) return;
            ProtectedGameRoster.Clear();
            SyncAcRoster();
        }

        private void RefreshAcGroupStates()
        {
            if (acBanner != null)
            {
                acBanner.State = tamer.Paused ? "BOUNDARY PAUSED" : "BOUNDARY ONLINE";
                acBanner.StateColor = tamer.Paused ? Theme.Danger : Theme.Green;
            }
            for (int i = 0; i < acGroups.Count && i < acCards.Count; i++)
            {
                string key = acGroups[i].Key;
                int state = tamer.GroupState(key);
                acCards[i].SetStatus(tamer.GroupStatus(key),
                    state == 1 ? Theme.Green : state == 0 ? Theme.Dim : Theme.Accent);
            }
        }

        private void RefreshAcList()
        {
            while (acList.Controls.Count > 0) acList.Controls[0].Dispose();
            acGroups.Clear();
            acCards.Clear();
            acToggles.Clear();
            int sy = 0;
            foreach (AcGroup g in AntiCheatCatalog.Groups)
                sy += AddAcCard(g.Key, Lang.T("ac." + g.Key + ".n"), Lang.T("ac." + g.Key + ".d"), g.Procs, sy) + 8;
            RefreshAcGroupStates();
        }

        // 档位选择器已移除 压制构成固定为扫描安全 见 SuppressionCore.Apply 的说明
        //   高度不能跟着选择器减 它浮在右下不占垂直流 标题+状态+说明+进程名四层就要这么高
        private const int AcCardH = 104;

        private int AddAcCard(string key, string title, string note, string[] procs, int y)
        {
            var sw = MakeSwitch(tamer.IsGroupEnabled(key), null);
            sw.CheckedChanged += (s, e) => { tamer.SetGroupEnabled(key, sw.Checked); RefreshAcGroupStates(); };

            SettingCard card = MakeCard(acList, 6, y, ScrollContentW, AcCardH, title, note, sw);
            card.HostTop = true;
            card.Meta = string.Join(" · ", procs);

            acGroups.Add(new AcGroup(key, title, false, new string[0]));
            acCards.Add(card);
            acToggles.Add(sw);
            return AcCardH;
        }

    }
}
