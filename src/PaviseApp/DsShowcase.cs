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
					int onPaintValue = 0;
					string[] textEntries = new string[2] { "ACTIVE", "IDLE" };
					for (int i = 0; i < textEntries.Length; i++)
					{
						int badgeWidthResult = DsPaint.BadgeWidth(graphics, textEntries[i], font);
						DsPaint.Badge(graphics, new Rectangle(onPaintValue, (base.Height - scaledDimension) / 2, badgeWidthResult, scaledDimension), textEntries[i], font, i == 0);
						onPaintValue += badgeWidthResult + Theme.S(8);
					}
				}
			}
		}

		private const int OuterMargin = 16;

		private const int CaptionWidth = 190;

		private const int ControlLeft = 206;

		private const int ContentWidth = 420;

		private const int RowGap = 10;

		private const int SectionHeight = 24;

		private const int MinRow = 24;

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
			Label label = new Label();
			label.AutoSize = false;
			label.Text = DsLabels.Pick(zh, en);
			label.UseCompatibleTextRendering = false;
			label.Font = DsType.Font(DsText.Title, true);
			label.ForeColor = Theme.Fg;
			label.BackColor = Theme.Bg;
			label.TextAlign = ContentAlignment.MiddleLeft;
			Label label2 = label;
			label2.SetBounds(Theme.S(16), cursor, Theme.S(610), Theme.S(24));
			base.Controls.Add(label2);
			cursor = label2.Bottom + Theme.S(8);
		}

		private Entry Add(string id, string caption, Control control, int width, int height)
		{
			int height2 = Theme.S(height);
			int height3 = Math.Max(Theme.S(24), height2);
			control.Name = "showcase_" + id.Replace('.', '_');
			control.SetBounds(Theme.S(206), cursor + (height3 - height2) / 2, Theme.S(width), height2);
			Label label = new Label();
			label.AutoSize = false;
			label.Text = caption;
			label.UseCompatibleTextRendering = false;
			label.Font = DsType.Font(DsText.Label);
			label.ForeColor = Theme.Dim;
			label.BackColor = Theme.Bg;
			label.TextAlign = ContentAlignment.MiddleLeft;
			Label label2 = label;
			label2.SetBounds(Theme.S(16), cursor, Theme.S(190), height3);
			base.Controls.Add(label2);
			base.Controls.Add(control);
			Entry entry = new Entry();
			entry.Id = id;
			entry.Caption = caption;
			entry.Control = control;
			entry.Label = label2;
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
			PillButton pillButton2 = new PillButton("Busy", BtnKind.Primary);
			pillButton2.Busy = true;
			Add("button.busy", "PillButton · Busy", pillButton2, 128, 34);
			Add("button.settings", "SettingsButton", new SettingsButton("Settings", "Open settings"), 32, 28);
			Section("开关", "Toggles");
			Add("toggle.off", "Toggle · Off", new Toggle(), 46, 30);
			Toggle toggle = new Toggle();
			toggle.SetSilently(true);
			Add("toggle.on", "Toggle · On", toggle, 46, 30);
			Toggle toggle2 = new Toggle();
			toggle2.Text = DsLabels.Pick("带文字", "With label");
			toggle2.SetSilently(true);
			Add("toggle.text", "Toggle · Label", toggle2, 200, 30);
			Toggle toggle3 = new Toggle();
			toggle3.SetSilently(true);
			toggle3.Enabled = false;
			Add("toggle.disabled", "Toggle · Disabled", toggle3, 46, 30);
			Toggle toggle4 = new Toggle();
			toggle4.Busy = true;
			Add("toggle.busy", "Toggle · Busy", toggle4, 46, 30);
			Section("分段选择", "Tier picker");
			TierPicker tierPicker = new TierPicker();
			tierPicker.Labels = new string[3] { "Off", "On", "Ultra" };
			Add("tier.three", "TierPicker · 3", tierPicker, 300, 32);
			TierPicker tierPicker2 = new TierPicker();
			tierPicker2.Labels = new string[4] { "Off", "Latest", "J", "K" };
			tierPicker2.Index = 1;
			Add("tier.four", "TierPicker · 4", tierPicker2, 300, 32);
			TierPicker tierPicker3 = new TierPicker();
			tierPicker3.Labels = new string[3] { "Off", "On", "Ultra" };
			tierPicker3.Enabled = false;
			Add("tier.disabled", "TierPicker · Disabled", tierPicker3, 300, 32);
			TierPicker tierPicker4 = new TierPicker();
			tierPicker4.Labels = new string[3] { "Off", "On", "Ultra" };
			tierPicker4.Busy = true;
			Add("tier.busy", "TierPicker · Busy", tierPicker4, 300, 32);
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
			TechTabs techTabs2 = new TechTabs();
			techTabs2.SetTabs(new string[2] { "One", "Two" }, null);
			techTabs2.Busy = true;
			Add("tabs.busy", "TechTabs · Busy", techTabs2, 420, 40);
			Section("下拉选择", "Dropdown select");
			TechSelect techSelect = new TechSelect();
			techSelect.SetItems(new string[3] { "Balanced", "Performance", "Quiet" });
			techSelect.CurrentBadge = DsLabels.Pick("当前", "Current");
			techSelect.SetSilently(1);
			Add("select.default", "TechSelect", techSelect, 220, 32);
			TechSelect techSelect2 = new TechSelect();
			techSelect2.SetItems(new string[1] { "Balanced" });
			techSelect2.Enabled = false;
			Add("select.disabled", "TechSelect · Disabled", techSelect2, 220, 32);
			Section("设置卡", "Setting cards");
			SettingCard settingCard = new SettingCard();
			settingCard.Title = DsLabels.Pick("宿主开关", "Hosted toggle");
			settingCard.Desc = DsLabels.Pick("说明文字会按卡片宽度换行，超出行数时省略。", "Description text wraps to the card width and is truncated after the available lines.");
			settingCard.Channel = 1;
			Toggle toggle5 = new Toggle();
			toggle5.Size = new Size(Theme.S(46), Theme.S(24));
			toggle5.Bg = Theme.Card;
			settingCard.Host(toggle5);
			Add("card.toggle", "SettingCard · Toggle", settingCard, 420, 84);
			SettingCard settingCard2 = new SettingCard();
			settingCard2.Title = DsLabels.Pick("状态与徽章", "Status and lock");
			settingCard2.Channel = 2;
			settingCard2.SetStatus(DsLabels.Pick("已生效", "Applied"), Theme.Green);
			settingCard2.SetLock("LOCK", true);
			settingCard2.SetValue("42%", Theme.Accent);
			Add("card.status", "SettingCard · Status", settingCard2, 420, 60);
			SettingCard settingCard3 = new SettingCard();
			settingCard3.Title = DsLabels.Pick("可折叠", "Collapsible");
			settingCard3.Desc = DsLabels.Pick("展开后显示的说明。", "Content shown while expanded.");
			settingCard3.Meta = "META // 03";
			settingCard3.Channel = 3;
			settingCard3.Collapsible = true;
			settingCard3.CollapsedHeight = Theme.S(48);
			settingCard3.ExpandedHeight = Theme.S(84);
			Add("card.collapsible", "SettingCard · Collapsible", settingCard3, 420, 84);
			Section("链接模块", "Link modules");
			Add("link.external", "RogLinkButton · External", new RogLinkButton(DsLabels.Pick("官网", "Website"), "LINK // EXTERNAL", "info"), 220, 48);
			RogLinkButton rogLinkButton = new RogLinkButton(DsLabels.Pick("白名单", "Whitelist"), "GAME // WHITELIST", "white");
			rogLinkButton.External = false;
			rogLinkButton.Dot = true;
			Add("link.forward", "RogLinkButton · Forward", rogLinkButton, 220, 48);
			RogLinkButton rogLinkButton2 = new RogLinkButton(DsLabels.Pick("支持", "Donate"), "SUPPORT // DONATE", "heart");
			rogLinkButton2.Tint = Theme.Danger;
			Add("link.tinted", "RogLinkButton · Tint", rogLinkButton2, 220, 48);
			RogLinkButton rogLinkButton3 = new RogLinkButton(DsLabels.Pick("禁用", "Disabled"), "LINK // OFF", "info");
			rogLinkButton3.Enabled = false;
			Add("link.disabled", "RogLinkButton · Disabled", rogLinkButton3, 220, 48);
			Section("徽章", "Badges");
			Add("badge.sample", "DsPaint.Badge", new BadgeSample(), 220, 24);
		}
	}
}
