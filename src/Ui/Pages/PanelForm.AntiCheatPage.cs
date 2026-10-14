// 作者：bdth 2074055628@qq.com
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
        private TierPicker acModePicker;
        private Label lblAcModeDesc, lblAcDisclaimer;
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

            acRosterBar = new RoundPanel();
            acRosterBar.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(84));
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
                (Theme.S(44) - swAcMaster.Height) / 2);
            acRosterBar.Controls.Add(swAcMaster);

            var lblMaster = new Label();
            lblMaster.AutoSize = false;
            lblMaster.BackColor = Color.Transparent;
            lblMaster.Font = Theme.UI(8.6f, true);
            lblMaster.ForeColor = Theme.Fg;
            lblMaster.TextAlign = ContentAlignment.MiddleLeft;
            lblMaster.Text = Lang.T("tame.toggle");
            lblMaster.Name = "acMasterLabel";
            int masterWidth = TextRenderer.MeasureText(lblMaster.Text, lblMaster.Font,
                Size.Empty, TextFormatFlags.SingleLine).Width + Theme.S(8);
            lblMaster.SetBounds(Theme.S(68), 0, masterWidth, Theme.S(44));
            acRosterBar.Controls.Add(lblMaster);

            var lblAcMode = new Label();
            lblAcMode.AutoSize = false;
            lblAcMode.BackColor = Color.Transparent;
            lblAcMode.Font = Theme.UI(8.6f, true);
            lblAcMode.ForeColor = Theme.Fg;
            lblAcMode.TextAlign = ContentAlignment.MiddleLeft;
            lblAcMode.Text = Lang.T("ac.mode");
            lblAcMode.Name = "acStrengthLabel";
            int strengthWidth = TextRenderer.MeasureText(lblAcMode.Text, lblAcMode.Font,
                Size.Empty, TextFormatFlags.SingleLine).Width + Theme.S(8);
            lblAcMode.SetBounds(lblMaster.Right + Theme.S(24), 0, strengthWidth, Theme.S(44));
            acRosterBar.Controls.Add(lblAcMode);

            acModePicker = new TierPicker();
            acModePicker.Labels = new[] { Lang.T("ac.mode.gentle"),
                Lang.T("ac.mode.balanced"), Lang.T("ac.mode.isolated") };
            acModePicker.Index = (int)tamer.Mode;
            acModePicker.Size = new Size(Theme.S(234), Theme.S(28));
            acModePicker.Location = new Point(lblAcMode.Right + Theme.S(10), Theme.S(8));
            acModePicker.IndexChanged = delegate(int index)
            {
                tamer.Mode = (AntiCheatMode)index;
                RefreshAcGroupStates();
            };
            acRosterBar.Controls.Add(acModePicker);

            var btnRoster = new PillButton(Lang.T("btn.roster.clear"));
            btnRoster.Size = new Size(Theme.S(84), Theme.S(26));
            btnRoster.Location = new Point(
                acRosterBar.Width - Theme.S(12) - btnRoster.Width,
                Theme.S(50));
            btnRoster.Click += delegate { OnClearRoster(); };
            acRosterBar.Controls.Add(btnRoster);

            lblAcRoster = new Label();
            lblAcRoster.AutoSize = false;
            lblAcRoster.BackColor = Color.Transparent;
            lblAcRoster.Font = Theme.UI(8.2f, false);
            lblAcRoster.TextAlign = ContentAlignment.MiddleLeft;
            lblAcRoster.AutoEllipsis = true;
            lblAcRoster.SetBounds(Theme.S(12), Theme.S(46),
                btnRoster.Left - Theme.S(24), Theme.S(34));
            acRosterBar.Controls.Add(lblAcRoster);
            var rosterDivider = new Panel { BackColor = Theme.Stroke };
            rosterDivider.SetBounds(Theme.S(12), Theme.S(44), acRosterBar.Width - Theme.S(24), Math.Max(1, Theme.S(1)));
            acRosterBar.Controls.Add(rosterDivider);
            y += 90;

            lblAcModeDesc = new Label();
            lblAcModeDesc.AutoSize = false;
            lblAcModeDesc.BackColor = Color.Transparent;
            lblAcModeDesc.Font = Theme.UI(8.2f, false);
            lblAcModeDesc.ForeColor = Theme.Dim;
            lblAcModeDesc.TextAlign = ContentAlignment.MiddleLeft;
            lblAcModeDesc.UseCompatibleTextRendering = false;
            lblAcModeDesc.AutoEllipsis = true;
            int modeDescHeight = 20;
            foreach (string tier in new[] { "gentle", "balanced", "isolated" })
                modeDescHeight = Math.Max(modeDescHeight, AcExplanationHeight(Lang.T("ac.mode." + tier + ".d")));
            lblAcModeDesc.SetBounds(Theme.S(ContentX + 2), Theme.S(y), Theme.S(ContentW - 4), Theme.S(modeDescHeight));
            pageAntiCheat.Controls.Add(lblAcModeDesc);
            y += modeDescHeight + 2;

            lblAcDisclaimer = new Label();
            lblAcDisclaimer.AutoSize = false;
            lblAcDisclaimer.BackColor = Color.Transparent;
            lblAcDisclaimer.Font = Theme.UI(8.2f, false);
            lblAcDisclaimer.ForeColor = Theme.LightMode
                ? Color.FromArgb(205, 122, 16) : Color.FromArgb(255, 174, 52);
            lblAcDisclaimer.TextAlign = ContentAlignment.MiddleLeft;
            lblAcDisclaimer.UseCompatibleTextRendering = false;
            lblAcDisclaimer.AutoEllipsis = true;
            lblAcDisclaimer.Text = Lang.T("ac.mode.disclaimer");
            int disclaimerHeight = AcExplanationHeight(lblAcDisclaimer.Text);
            lblAcDisclaimer.SetBounds(Theme.S(ContentX + 2), Theme.S(y), Theme.S(ContentW - 4), Theme.S(disclaimerHeight));
            pageAntiCheat.Controls.Add(lblAcDisclaimer);
            y += disclaimerHeight + 6;
            SyncAcRoster();

            acList = new DBPanel();
            acList.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            acList.BackColor = Theme.Bg; acList.AutoScroll = true; Native.Dark(acList); pageAntiCheat.Controls.Add(acList);
            RefreshAcList();
        }

        private static int AcExplanationHeight(string text)
        {
            int pixels = TextRenderer.MeasureText(text, Theme.UI(8.2f, false),
                new Size(Theme.S(ContentW - 4), int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(4);
            return Math.Max(20, (int)Math.Ceiling(pixels * 100.0 / Theme.S(100)));
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
            if (acModePicker != null && acModePicker.Index != (int)tamer.Mode)
                acModePicker.Index = (int)tamer.Mode;
            if (lblAcModeDesc != null)
            {
                string desc = Lang.T("ac.mode." + AntiCheatModes.Token(tamer.Mode) + ".d");
                if (!string.Equals(lblAcModeDesc.Text, desc, StringComparison.Ordinal))
                    lblAcModeDesc.Text = desc;
            }
            if (acBanner != null)
            {
                acBanner.State = tamer.Paused ? "SUPPRESSION PAUSED" : "SUPPRESSION ENABLED";
                acBanner.StateColor = tamer.Paused ? Theme.Dim : Theme.Green;
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
            {
                if (!g.Suppressible) continue;
                sy += AddAcCard(g.Key, Lang.T("ac." + g.Key + ".n"), Lang.T("ac." + g.Key + ".d"), g.Procs, sy) + 8;
            }
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                if (g.Suppressible) continue;
                SettingCard card = MakeCard(acList, 6, sy, ScrollContentW, AcCardH,
                    Lang.T("ac." + g.Key + ".n"), Lang.T("ac." + g.Key + ".d"), null);
                card.Meta = string.Join(" · ", g.Procs);
                card.SetStatus(Lang.T("ac.protectiononly"), Theme.Green);
                sy += AcCardH + 8;
            }
            foreach (AcProtectionGroup g in AntiCheatCatalog.ProtectionOnlyGroups)
            {
                SettingCard card = MakeCard(acList, 6, sy, ScrollContentW, AcCardH,
                    g.Name, Lang.T("ac." + g.Key + ".d"), null);
                card.Meta = string.Join(" · ", g.PatternsForDisplay());
                card.SetStatus(Lang.T("ac.protectiononly"), Theme.Green);
                sy += AcCardH + 8;
            }
            RefreshAcGroupStates();
        }

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
