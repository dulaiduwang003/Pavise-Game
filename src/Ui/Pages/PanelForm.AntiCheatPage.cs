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
        private readonly List<AcGroup> acGroups = new List<AcGroup>();
        private readonly List<SettingCard> acCards = new List<SettingCard>();
        private readonly List<Toggle> acToggles = new List<Toggle>();

        private void BuildAntiCheatPage()
        {
            int y = PageHeader(pageAntiCheat, Lang.T("v14.anticheat"), Lang.T("v15.anticheat.sub"), 2);
            Section(pageAntiCheat, Lang.T("v14.anticheat.boundary"), 26, y + 8); y += 46;
            swAcMaster = MakeSwitch(!tamer.Paused, delegate { tamer.Paused = !swAcMaster.Checked; Settings.Save("TameOn", swAcMaster.Checked); });
            int acCardH;
            MakeAutoCard(pageAntiCheat, ContentX, y, ContentW, 56, Lang.T("tame.toggle"),
                Lang.T("v14.anticheat.master.sub"), swAcMaster, out acCardH); y += acCardH + 10;
            acList = new DBPanel();
            acList.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            acList.BackColor = Theme.Bg; acList.AutoScroll = true; Native.Dark(acList); pageAntiCheat.Controls.Add(acList);
            RefreshAcList();
        }

        private void RefreshAcGroupStates()
        {
            for (int i = 0; i < acGroups.Count && i < acCards.Count; i++)
            {
                string key = acGroups[i].Key;
                int state = tamer.GroupState(key);
                acCards[i].SetValue(tamer.GroupStatus(key),
                    state == 1 ? Theme.Green : state == 0 ? Theme.Dim : Theme.Accent);
            }
        }

        private void RefreshAcList()
        {
            while (acList.Controls.Count > 0) acList.Controls[0].Dispose();
            acGroups.Clear();
            acCards.Clear();
            acToggles.Clear();
            int pitch = 90, idx = 0;
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                string note = Lang.T("ac." + g.Key + ".d") + " " + string.Join(" ", g.Procs);
                AddAcCard(g.Key, Lang.T("ac." + g.Key + ".n"), note, idx * pitch);
                idx++;
            }
        }

        private void AddAcCard(string key, string title, string note, int y)
        {
            var sw = MakeSwitch(tamer.IsGroupEnabled(key), null);
            sw.CheckedChanged += (s, e) => tamer.SetGroupEnabled(key, sw.Checked);

            var lvl = new TierPicker();
            lvl.Size = new Size(Theme.S(168), Theme.S(28));
            lvl.Value = tamer.GroupLevel(key);
            lvl.Changed = delegate(SuppressionLevel v) { tamer.SetGroupLevel(key, v); };

            var wrap = new DBPanel();
            wrap.Size = new Size(lvl.Width + Theme.S(12) + sw.Width, Theme.S(30));
            wrap.BackColor = Theme.Card;
            lvl.Location = new Point(0, (wrap.Height - lvl.Height) / 2);
            sw.Location = new Point(lvl.Width + Theme.S(12), (wrap.Height - sw.Height) / 2);
            wrap.Controls.Add(lvl);
            wrap.Controls.Add(sw);

            var card = new SettingCard();
            card.SetBounds(Theme.S(6), Theme.S(y), Theme.S(ScrollContentW), Theme.S(82));
            card.Title = title;
            card.Desc = note;
            card.Host(wrap);

            acList.Controls.Add(card);
            acGroups.Add(new AcGroup(key, title, "", false, new string[0]));
            acCards.Add(card);
            acToggles.Add(sw);
        }

    }
}
