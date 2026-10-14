// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    // 组件样例面板：枚举代表性组件与状态，供文档截图与回归检查；不接入产品启动流程。
    internal sealed class DsShowcase : WorkspacePanel
    {
        internal sealed class Entry
        {
            public string Id;
            public string Caption;
            public Control Control;
            public Label Label;
        }

        private const int OuterMargin = 16, CaptionWidth = 190, ControlLeft = 206, ContentWidth = 420;
        private const int RowGap = 10, SectionHeight = 24, MinRow = 24;

        private readonly List<Entry> entries = new List<Entry>();
        private int cursor;

        public IList<Entry> Entries { get { return entries; } }

        public DsShowcase()
        {
            Width = Theme.S(ControlLeft + ContentWidth + OuterMargin);
            cursor = Theme.S(OuterMargin);
            Build();
            Height = cursor + Theme.S(OuterMargin);
        }

        public Entry Find(string id)
        {
            foreach (Entry entry in entries) if (entry.Id == id) return entry;
            return null;
        }

        private void Section(string zh, string en)
        {
            var label = new Label { AutoSize = false, Text = DsLabels.Pick(zh, en), UseCompatibleTextRendering = false,
                Font = DsType.Font(DsText.Title, true), ForeColor = Theme.Fg, BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleLeft };
            label.SetBounds(Theme.S(OuterMargin), cursor, Theme.S(ControlLeft + ContentWidth - OuterMargin), Theme.S(SectionHeight));
            Controls.Add(label);
            cursor = label.Bottom + Theme.S(DsSpace.M);
        }

        private Entry Add(string id, string caption, Control control, int width, int height)
        {
            int h = Theme.S(height);
            int row = Math.Max(Theme.S(MinRow), h);
            control.Name = "showcase_" + id.Replace('.', '_');
            control.SetBounds(Theme.S(ControlLeft), cursor + (row - h) / 2, Theme.S(width), h);
            var label = new Label { AutoSize = false, Text = caption, UseCompatibleTextRendering = false,
                Font = DsType.Font(DsText.Label), ForeColor = Theme.Dim, BackColor = Theme.Bg,
                TextAlign = ContentAlignment.MiddleLeft };
            label.SetBounds(Theme.S(OuterMargin), cursor, Theme.S(CaptionWidth), row);
            Controls.Add(label);
            Controls.Add(control);
            var entry = new Entry();
            entry.Id = id; entry.Caption = caption; entry.Control = control; entry.Label = label;
            entries.Add(entry);
            cursor += row + Theme.S(RowGap);
            return entry;
        }

        private void Build()
        {
            Section("按钮", "Buttons");
            Add("button.normal", "PillButton · Normal", new PillButton("Normal"), 128, DsMetrics.Button.Height);
            Add("button.primary", "PillButton · Primary", new PillButton("Primary", BtnKind.Primary), 128, DsMetrics.Button.Height);
            Add("button.danger", "PillButton · Danger", new PillButton("Danger", BtnKind.Danger), 128, DsMetrics.Button.Height);
            var disabledButton = new PillButton("Disabled"); disabledButton.Enabled = false;
            Add("button.disabled", "PillButton · Disabled", disabledButton, 128, DsMetrics.Button.Height);
            var busyButton = new PillButton("Busy", BtnKind.Primary); busyButton.Busy = true;
            Add("button.busy", "PillButton · Busy", busyButton, 128, DsMetrics.Button.Height);
            Add("button.settings", "SettingsButton", new SettingsButton("Settings", "Open settings"), 32, 28);

            Section("开关", "Toggles");
            Add("toggle.off", "Toggle · Off", new Toggle(), DsMetrics.Toggle.Width, DsMetrics.Toggle.Height);
            var toggleOn = new Toggle(); toggleOn.SetSilently(true);
            Add("toggle.on", "Toggle · On", toggleOn, DsMetrics.Toggle.Width, DsMetrics.Toggle.Height);
            var toggleText = new Toggle(); toggleText.Text = DsLabels.Pick("带文字", "With label"); toggleText.SetSilently(true);
            Add("toggle.text", "Toggle · Label", toggleText, 200, DsMetrics.Toggle.Height);
            var toggleDisabled = new Toggle(); toggleDisabled.SetSilently(true); toggleDisabled.Enabled = false;
            Add("toggle.disabled", "Toggle · Disabled", toggleDisabled, DsMetrics.Toggle.Width, DsMetrics.Toggle.Height);
            var toggleBusy = new Toggle(); toggleBusy.Busy = true;
            Add("toggle.busy", "Toggle · Busy", toggleBusy, DsMetrics.Toggle.Width, DsMetrics.Toggle.Height);

            Section("分段选择", "Tier picker");
            var tierThree = new TierPicker(); tierThree.Labels = new[] { "Off", "On", "Ultra" };
            Add("tier.three", "TierPicker · 3", tierThree, 300, 32);
            var tierFour = new TierPicker(); tierFour.Labels = new[] { "Off", "Latest", "J", "K" }; tierFour.Index = 1;
            Add("tier.four", "TierPicker · 4", tierFour, 300, 32);
            var tierDisabled = new TierPicker(); tierDisabled.Labels = new[] { "Off", "On", "Ultra" }; tierDisabled.Enabled = false;
            Add("tier.disabled", "TierPicker · Disabled", tierDisabled, 300, 32);
            var tierBusy = new TierPicker(); tierBusy.Labels = new[] { "Off", "On", "Ultra" }; tierBusy.Busy = true;
            Add("tier.busy", "TierPicker · Busy", tierBusy, 300, 32);

            Section("页签", "Tabs");
            var tabs = new TechTabs();
            tabs.SetTabs(new[] { "Alpha", "Beta", "Gamma" }, new[] { DsLabels.Pick("第一页提示", "first hint"), "", DsLabels.Pick("第三页提示", "third hint") });
            tabs.SetHot(new[] { false, true, false });
            Add("tabs.default", "TechTabs", tabs, ContentWidth, 40);
            var tabsBusy = new TechTabs(); tabsBusy.SetTabs(new[] { "One", "Two" }, null); tabsBusy.Busy = true;
            Add("tabs.busy", "TechTabs · Busy", tabsBusy, ContentWidth, 40);

            Section("下拉选择", "Dropdown select");
            var select = new TechSelect();
            select.SetItems(new[] { "Balanced", "Performance", "Quiet" });
            select.CurrentBadge = DsLabels.Pick("当前", "Current");
            select.SetSilently(1);
            Add("select.default", "TechSelect", select, DsMetrics.Select.Width, DsMetrics.Select.Height);
            var selectDisabled = new TechSelect(); selectDisabled.SetItems(new[] { "Balanced" }); selectDisabled.Enabled = false;
            Add("select.disabled", "TechSelect · Disabled", selectDisabled, DsMetrics.Select.Width, DsMetrics.Select.Height);

            Section("设置卡", "Setting cards");
            var hostedCard = new SettingCard();
            hostedCard.Title = DsLabels.Pick("宿主开关", "Hosted toggle");
            hostedCard.Desc = DsLabels.Pick("说明文字会按卡片宽度换行，超出行数时省略。", "Description text wraps to the card width and is truncated after the available lines.");
            hostedCard.Channel = 1;
            var cardToggle = new Toggle(); cardToggle.Size = new Size(Theme.S(DsMetrics.Toggle.Width), Theme.S(24)); cardToggle.Bg = Theme.Card;
            hostedCard.Host(cardToggle);
            Add("card.toggle", "SettingCard · Toggle", hostedCard, ContentWidth, 84);
            var statusCard = new SettingCard();
            statusCard.Title = DsLabels.Pick("状态与徽章", "Status and lock");
            statusCard.Channel = 2;
            statusCard.SetStatus(DsLabels.Pick("已生效", "Applied"), Theme.Green);
            statusCard.SetLock("LOCK", true);
            statusCard.SetValue("42%", Theme.Accent);
            Add("card.status", "SettingCard · Status", statusCard, ContentWidth, 60);
            var collapsibleCard = new SettingCard();
            collapsibleCard.Title = DsLabels.Pick("可折叠", "Collapsible");
            collapsibleCard.Desc = DsLabels.Pick("展开后显示的说明。", "Content shown while expanded.");
            collapsibleCard.Meta = "META // 03";
            collapsibleCard.Channel = 3;
            collapsibleCard.Collapsible = true;
            collapsibleCard.CollapsedHeight = Theme.S(48);
            collapsibleCard.ExpandedHeight = Theme.S(84);
            Add("card.collapsible", "SettingCard · Collapsible", collapsibleCard, ContentWidth, 84);

            Section("链接模块", "Link modules");
            Add("link.external", "RogLinkButton · External", new RogLinkButton(DsLabels.Pick("官网", "Website"), "LINK // EXTERNAL", "info"), 220, 48);
            var forward = new RogLinkButton(DsLabels.Pick("白名单", "Whitelist"), "GAME // WHITELIST", "white");
            forward.External = false; forward.Dot = true;
            Add("link.forward", "RogLinkButton · Forward", forward, 220, 48);
            var tinted = new RogLinkButton(DsLabels.Pick("支持", "Donate"), "SUPPORT // DONATE", "heart");
            tinted.Tint = Theme.Danger;
            Add("link.tinted", "RogLinkButton · Tint", tinted, 220, 48);
            var linkDisabled = new RogLinkButton(DsLabels.Pick("禁用", "Disabled"), "LINK // OFF", "info");
            linkDisabled.Enabled = false;
            Add("link.disabled", "RogLinkButton · Disabled", linkDisabled, 220, 48);

            Section("徽章", "Badges");
            Add("badge.sample", "DsPaint.Badge", new BadgeSample(), 220, 24);
        }

        // 徽章样例：强调态与普通态并排。
        private sealed class BadgeSample : Control
        {
            public BadgeSample()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                TabStop = false;
                BackColor = Theme.Bg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                DsPaint.FillBackground(g, this, BackColor);
                if (Width < 2 || Height < 2) return;
                Font font = DsType.Mono(DsMono.Badge);
                int bh = Theme.S(DsMetrics.Badge.Height);
                int x = 0;
                string[] texts = { "ACTIVE", "IDLE" };
                for (int i = 0; i < texts.Length; i++)
                {
                    int bw = DsPaint.BadgeWidth(g, texts[i], font);
                    DsPaint.Badge(g, new Rectangle(x, (Height - bh) / 2, bw, bh), texts[i], font, i == 0);
                    x += bw + Theme.S(DsSpace.M);
                }
            }
        }
    }
}
