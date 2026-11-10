using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class DsShowcase : WorkspacePanel
    {
        internal sealed class Entry
        {
            public string Id;

            public string Caption;

            public Control Control;

            public Label Label;
        }

        private sealed class BadgeSample : Control
        {
            public BadgeSample()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                base.TabStop = false;
                BackColor = Theme.Bg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                DsPaint.FillBackground(graphics, this, BackColor);
                if (base.Width >= 2 && base.Height >= 2)
                {
                    Font font = DsType.Mono(DsMono.Badge);
                    int scaledDimension = Theme.S(17);
                    int badgeLeft = 0;
                    string[] stateLabels = new string[2] { "ACTIVE", "IDLE" };
                    for (int i = 0; i < stateLabels.Length; i++)
                    {
                        int badgeWidthResult = DsPaint.BadgeWidth(graphics, stateLabels[i], font);
                        DsPaint.Badge(graphics, new Rectangle(badgeLeft, (base.Height - scaledDimension) / 2, badgeWidthResult, scaledDimension), stateLabels[i], font, i == 0);
                        badgeLeft += badgeWidthResult + Theme.S(8);
                    }
                }
            }
        }

        private readonly List<Entry> entries = new List<Entry>();

        private int cursor;

        public IList<Entry> Entries
        {
            get
            {
                return entries;
            }
        }

        public DsShowcase()
        {
            base.Width = Theme.S(642);
            cursor = Theme.S(16);
            Build();
            base.Height = cursor + Theme.S(16);
        }

        public Entry Find(string id)
        {
            foreach (Entry entry in entries)
            {
                if (entry.Id == id)
                {
                    return entry;
                }
            }
            return null;
        }

        private void Section(string zh, string en)
        {
            Label currentLabel = new Label();
            currentLabel.AutoSize = false;
            currentLabel.Text = DsLabels.Pick(zh, en);
            currentLabel.UseCompatibleTextRendering = false;
            currentLabel.Font = DsType.Font(DsText.Title, true);
            currentLabel.ForeColor = Theme.Fg;
            currentLabel.BackColor = Theme.Bg;
            currentLabel.TextAlign = ContentAlignment.MiddleLeft;
            currentLabel.SetBounds(Theme.S(16), cursor, Theme.S(610), Theme.S(24));
            base.Controls.Add(currentLabel);
            cursor = currentLabel.Bottom + Theme.S(8);
        }

        private Entry Add(string id, string caption, Control control, int width, int height)
        {
            int height2 = Theme.S(height);
            int height3 = Math.Max(Theme.S(24), height2);
            control.Name = "showcase_" + id.Replace('.', '_');
            control.SetBounds(Theme.S(206), cursor + (height3 - height2) / 2, Theme.S(width), height2);
            Label currentLabel = new Label();
            currentLabel.AutoSize = false;
            currentLabel.Text = caption;
            currentLabel.UseCompatibleTextRendering = false;
            currentLabel.Font = DsType.Font(DsText.Label);
            currentLabel.ForeColor = Theme.Dim;
            currentLabel.BackColor = Theme.Bg;
            currentLabel.TextAlign = ContentAlignment.MiddleLeft;
            currentLabel.SetBounds(Theme.S(16), cursor, Theme.S(190), height3);
            base.Controls.Add(currentLabel);
            base.Controls.Add(control);
            Entry entry = new Entry();
            entry.Id = id;
            entry.Caption = caption;
            entry.Control = control;
            entry.Label = currentLabel;
            entries.Add(entry);
            cursor += height3 + Theme.S(10);
            return entry;
        }

        private void Build()
        {
            Section("按钮", "Buttons");
            Add("button.normal", "PillButton · Normal", new PillButton("Normal"), 128, 34);
            Add("button.primary", "PillButton · Primary", new PillButton("Primary", BtnKind.Primary), 128, 34);
            Add("button.danger", "PillButton · Danger", new PillButton("Danger", BtnKind.Danger), 128, 34);
            PillButton pillButton = new PillButton("Disabled");
            pillButton.Enabled = false;
            Add("button.disabled", "PillButton · Disabled", pillButton, 128, 34);
            PillButton currentPillButton = new PillButton("Busy", BtnKind.Primary);
            currentPillButton.Busy = true;
            Add("button.busy", "PillButton · Busy", currentPillButton, 128, 34);
            Add("button.settings", "SettingsButton", new SettingsButton("Settings", "Open settings"), 32, 28);
            Section("开关", "Toggles");
            Add("toggle.off", "Toggle · Off", new Toggle(), 46, 30);
            Toggle toggle = new Toggle();
            toggle.SetSilently(true);
            Add("toggle.on", "Toggle · On", toggle, 46, 30);
            Toggle currentToggle = new Toggle();
            currentToggle.Text = DsLabels.Pick("带文字", "With label");
            currentToggle.SetSilently(true);
            Add("toggle.text", "Toggle · Label", currentToggle, 200, 30);
            Toggle candidateToggle = new Toggle();
            candidateToggle.SetSilently(true);
            candidateToggle.Enabled = false;
            Add("toggle.disabled", "Toggle · Disabled", candidateToggle, 46, 30);
            Toggle resolvedToggle = new Toggle();
            resolvedToggle.Busy = true;
            Add("toggle.busy", "Toggle · Busy", resolvedToggle, 46, 30);
            Section("分段选择", "Tier picker");
            TierPicker tierPicker = new TierPicker();
            tierPicker.Labels = new string[3] { "Off", "On", "Ultra" };
            Add("tier.three", "TierPicker · 3", tierPicker, 300, 32);
            TierPicker currentTierPicker = new TierPicker();
            currentTierPicker.Labels = new string[4] { "Off", "Latest", "J", "K" };
            currentTierPicker.Index = 1;
            Add("tier.four", "TierPicker · 4", currentTierPicker, 300, 32);
            TierPicker candidateTierPicker = new TierPicker();
            candidateTierPicker.Labels = new string[3] { "Off", "On", "Ultra" };
            candidateTierPicker.Enabled = false;
            Add("tier.disabled", "TierPicker · Disabled", candidateTierPicker, 300, 32);
            TierPicker resolvedTierPicker = new TierPicker();
            resolvedTierPicker.Labels = new string[3] { "Off", "On", "Ultra" };
            resolvedTierPicker.Busy = true;
            Add("tier.busy", "TierPicker · Busy", resolvedTierPicker, 300, 32);
            Section("页签", "Tabs");
            TechTabs techTabs = new TechTabs();
            techTabs.SetTabs(new string[3] { "Alpha", "Beta", "Gamma" }, new string[3]
            {
                DsLabels.Pick("第一页提示", "first hint"),
                "",
                DsLabels.Pick("第三页提示", "third hint")
            });
            techTabs.SetHot(new bool[3] { false, true, false });
            Add("tabs.default", "TechTabs", techTabs, 420, 40);
            TechTabs currentTechTabs = new TechTabs();
            currentTechTabs.SetTabs(new string[2] { "One", "Two" }, null);
            currentTechTabs.Busy = true;
            Add("tabs.busy", "TechTabs · Busy", currentTechTabs, 420, 40);
            Section("下拉选择", "Dropdown select");
            TechSelect techSelect = new TechSelect();
            techSelect.SetItems(new string[3] { "Balanced", "Performance", "Quiet" });
            techSelect.CurrentBadge = DsLabels.Pick("当前", "Current");
            techSelect.SetSilently(1);
            Add("select.default", "TechSelect", techSelect, 220, 32);
            TechSelect currentTechSelect = new TechSelect();
            currentTechSelect.SetItems(new string[1] { "Balanced" });
            currentTechSelect.Enabled = false;
            Add("select.disabled", "TechSelect · Disabled", currentTechSelect, 220, 32);
            Section("设置卡", "Setting cards");
            SettingCard settingCard = new SettingCard();
            settingCard.Title = DsLabels.Pick("宿主开关", "Hosted toggle");
            settingCard.Desc = DsLabels.Pick("说明文字会按卡片宽度换行，超出行数时省略。", "Description text wraps to the card width and is truncated after the available lines.");
            settingCard.Channel = 1;
            Toggle selectedToggle = new Toggle();
            selectedToggle.Size = new Size(Theme.S(46), Theme.S(24));
            selectedToggle.Bg = Theme.Card;
            settingCard.Host(selectedToggle);
            Add("card.toggle", "SettingCard · Toggle", settingCard, 420, 84);
            SettingCard currentSettingCard = new SettingCard();
            currentSettingCard.Title = DsLabels.Pick("状态与徽章", "Status and lock");
            currentSettingCard.Channel = 2;
            currentSettingCard.SetStatus(DsLabels.Pick("已生效", "Applied"), Theme.Green);
            currentSettingCard.SetLock("LOCK", true);
            currentSettingCard.SetValue("42%", Theme.Accent);
            Add("card.status", "SettingCard · Status", currentSettingCard, 420, 60);
            SettingCard candidateSettingCard = new SettingCard();
            candidateSettingCard.Title = DsLabels.Pick("可折叠", "Collapsible");
            candidateSettingCard.Desc = DsLabels.Pick("展开后显示的说明。", "Content shown while expanded.");
            candidateSettingCard.Meta = "META // 03";
            candidateSettingCard.Channel = 3;
            candidateSettingCard.Collapsible = true;
            candidateSettingCard.CollapsedHeight = Theme.S(48);
            candidateSettingCard.ExpandedHeight = Theme.S(84);
            Add("card.collapsible", "SettingCard · Collapsible", candidateSettingCard, 420, 84);
            Section("链接模块", "Link modules");
            Add("link.external", "RogLinkButton · External", new RogLinkButton(DsLabels.Pick("官网", "Website"), "LINK // EXTERNAL", "info"), 220, 48);
            RogLinkButton rogLinkButton = new RogLinkButton(DsLabels.Pick("白名单", "Whitelist"), "GAME // WHITELIST", "white");
            rogLinkButton.External = false;
            rogLinkButton.Dot = true;
            Add("link.forward", "RogLinkButton · Forward", rogLinkButton, 220, 48);
            RogLinkButton currentRogLinkButton = new RogLinkButton(DsLabels.Pick("支持", "Donate"), "SUPPORT // DONATE", "heart");
            currentRogLinkButton.Tint = Theme.Danger;
            Add("link.tinted", "RogLinkButton · Tint", currentRogLinkButton, 220, 48);
            RogLinkButton candidateRogLinkButton = new RogLinkButton(DsLabels.Pick("禁用", "Disabled"), "LINK // OFF", "info");
            candidateRogLinkButton.Enabled = false;
            Add("link.disabled", "RogLinkButton · Disabled", candidateRogLinkButton, 220, 48);
            Section("徽章", "Badges");
            Add("badge.sample", "DsPaint.Badge", new BadgeSample(), 220, 24);
        }
    }
}
