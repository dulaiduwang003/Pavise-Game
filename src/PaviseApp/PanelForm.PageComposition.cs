using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
	internal partial class PanelForm : Form
	{
		private void BuildAboutPage()
		{
			int aboutPageValue = (aboutContentTop = PageHeader(pageAbout, Lang.T("nav.about"), Lang.T("site.about.originalSub"), 1));
			aboutViewport = Size.Empty;
			pageAbout.AutoScroll = false;
			Native.Dark(pageAbout);
			AboutHeroPanel aboutHeroPanel = (this.aboutHeroPanel = new AboutHeroPanel());
			aboutHeroPanel.SetBounds(Theme.S(30), Theme.S(aboutPageValue), Theme.S(ContentW), Theme.S(130));
			pageAbout.Controls.Add(aboutHeroPanel);
			PictureBox pictureBox = new PictureBox();
			pictureBox.SetBounds(Theme.S(24), Theme.S(23), Theme.S(84), Theme.S(84));
			pictureBox.BackColor = Color.Transparent;
			pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
			aboutIcon = pictureBox;
			OwnedImage(pictureBox, IconArt.Render(Theme.S(108), Theme.CurrentMode, true));
			aboutHeroLabels = new Label[4]
			{
				CardLabel(aboutHeroPanel, Lang.T("v20.about.system"), 132, 12, 360, 20, 7.1f, true, Theme.Faint),
				CardLabel(aboutHeroPanel, "PAVISE AUTO", 130, 31, 330, 40, 24f, true, Theme.Fg),
				AccentLabel(aboutHeroPanel, "AUTO GUARD  //  " + App.VersionTag, 132, 71, 360, 22, 8f, true),
				CardLabel(aboutHeroPanel, Lang.T("about.desc").Replace("\r\n", " "), 132, 96, 390, 27, 9f, false, Theme.Dim)
			};
			aboutHeroPanel.Controls.Add(pictureBox);
			aboutMetricControls = new Control[3][]
			{
				AddAboutMetric(aboutHeroPanel, Lang.T("v20.about.build"), App.VersionTag, 582, 27, 116, false),
				AddAboutMetric(aboutHeroPanel, Lang.T("v20.about.platform"), "WINDOWS x64", 706, 27, 124, false),
				AddAboutMetric(aboutHeroPanel, Lang.T("v20.about.state"), Lang.T("v20.about.ready"), 838, 27, 108, true)
			};
			int aboutPageValue2 = aboutPageValue + 130 + 12;
			int aboutPageValue3 = 454;
			int aboutPageValue4 = 14;
			int remainingAboutPageValue4 = ContentW - aboutPageValue3 - aboutPageValue4;
			int aboutPageValue5 = 366;
			RoundPanel roundPanel = (aboutIdentityPanel = MakeConsolePanel(pageAbout, 30, aboutPageValue2, aboutPageValue3, aboutPageValue5, false));
			Label label = CardLabel(roundPanel, "PROJECT // IDENTITY", 20, 15, aboutPageValue3 - 126, 20, 7.6f, true, Theme.Faint);
			label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Label label2 = CardLabel(roundPanel, "NODE 01", aboutPageValue3 - 88, 15, 68, 18, 6.5f, false, Theme.Faint);
			label2.TextAlign = ContentAlignment.MiddleRight;
			label2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			string[] textEntries = new string[4] { "about.author", "about.douyin", "about.repo", "about.lic" };
			string[] textEntries2 = new string[4]
			{
				"bdth 2074055628@qq.com",
				"44601770838",
				App.RepoUrl.Replace("https://", ""),
				Lang.T("about.lic.value")
			};
			for (int i = 0; i < 4; i++)
			{
				int aboutPageValue6 = 47 + i * 44;
				Label label3 = AddAboutRow(roundPanel, Lang.T(textEntries[i]).ToUpperInvariant(), textEntries2[i], aboutPageValue6, aboutPageValue3, i == 2 || i == 3);
				if (i == 2 || i == 3)
				{
					string targetUrl = ((i == 2) ? App.RepoUrl : App.RepoUrl + "/blob/dev/LICENSE");
					label3.Cursor = Cursors.Hand;
					label3.Click += delegate
					{
						OpenExternal(targetUrl);
					};
				}
			}
			Label freeLabel = CardLabel(roundPanel, Lang.T("about.free"), 20, 231, aboutPageValue3 - 40, 22, 9.5f, true, Theme.Green);
			freeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Label refundHint = CardLabel(roundPanel, Lang.T("about.refund.hint"), 20, 258, aboutPageValue3 - 40, 44, 7.8f, false, Theme.Fg);
			refundHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Label label4 = CardLabel(roundPanel, Lang.T("about.contact.hint"), 20, 312, aboutPageValue3 - 40, 40, 7.8f, false, Theme.Dim);
			label4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			RoundPanel roundPanel2 = (aboutReleasePanel = MakeConsolePanel(pageAbout, 30 + aboutPageValue3 + aboutPageValue4, aboutPageValue2, remainingAboutPageValue4, aboutPageValue5, true));
			Label label5 = CardLabel(roundPanel2, "RELEASE // " + Lang.T("v20.about.versionstatus").ToUpperInvariant(), 20, 15, remainingAboutPageValue4 - 126, 20, 7.6f, true, Theme.Faint);
			label5.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			StatusDot statusDot = new StatusDot();
			statusDot.SetBounds(Theme.S(remainingAboutPageValue4 - 126), Theme.S(12), Theme.S(22), Theme.S(22));
			statusDot.Bg = Theme.Card;
			statusDot.Color = Theme.Green;
			statusDot.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			roundPanel2.Controls.Add(statusDot);
			Label label6 = CardLabel(roundPanel2, Lang.T("v20.about.online"), remainingAboutPageValue4 - 102, 12, 82, 24, 6.7f, true, Theme.Green);
			label6.TextAlign = ContentAlignment.MiddleRight;
			label6.Anchor = AnchorStyles.Top | AnchorStyles.Right;
			AccentLabel(roundPanel2, App.VersionTag, 20, 45, 254, 43, 23f, true);
			Label label7 = CardLabel(roundPanel2, Lang.T("v15.about.identity"), 22, 87, remainingAboutPageValue4 - 44, 22, 8.2f, false, Theme.Dim);
			label7.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			Panel panel = new Panel();
			panel.BackColor = Theme.StrokeHi;
			panel.SetBounds(Theme.S(20), Theme.S(119), Theme.S(remainingAboutPageValue4 - 40), 1);
			panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			roundPanel2.Controls.Add(panel);
			CardLabel(roundPanel2, Lang.T("site.about.source"), 20, 132, remainingAboutPageValue4 - 40, 18, 7f, true, Theme.Faint);
			RoundPanel roundPanel3 = MakeConsolePanel(roundPanel2, 20, 153, remainingAboutPageValue4 - 40, 54, false);
			roundPanel3.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			CardLabel(roundPanel3, "PAVISE // OFFICIAL WEBSITE", 14, 6, remainingAboutPageValue4 - 68, 18, 7.3f, true, Theme.Faint).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			AccentLabel(roundPanel3, "pavise.club", 14, 25, remainingAboutPageValue4 - 68, 25, 10f, true).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			RoundPanel roundPanel4 = MakeConsolePanel(roundPanel2, 20, 218, remainingAboutPageValue4 - 40, 60, false);
			roundPanel4.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			StatusDot statusDot2 = new StatusDot();
			statusDot2.SetBounds(Theme.S(14), Theme.S(18), Theme.S(22), Theme.S(22));
			statusDot2.Bg = Theme.Card;
			statusDot2.FollowAccent = true;
			roundPanel4.Controls.Add(statusDot2);
			CardLabel(roundPanel4, Lang.T("v20.about.privacy"), 44, 8, remainingAboutPageValue4 - 116, 19, 7f, true, Theme.Faint).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			CardLabel(roundPanel4, Lang.T("v20.about.privacy.value"), 44, 27, remainingAboutPageValue4 - 116, 25, 9.4f, true, Theme.Fg).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			lblAboutUpdate = CardLabel(roundPanel2, "", 20, aboutPageValue5 - 76, remainingAboutPageValue4 - 40, 18, 7.7f, false, Theme.Faint);
			lblAboutUpdate.Name = "aboutUpdateStatus";
			lblAboutUpdate.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
			int ratio = (remainingAboutPageValue4 - 52) / 2;
			PillButton pillButton = new PillButton(Lang.T("notes.open"), BtnKind.Primary);
			pillButton.Name = "aboutReleaseNotes";
			pillButton.Bg = Theme.Card;
			PillButton pillButton2 = pillButton;
			pillButton2.SetBounds(Theme.S(20), Theme.S(aboutPageValue5 - 50), Theme.S(ratio), Theme.S(36));
			pillButton2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			pillButton2.Click += delegate
			{
				ShowReleaseNotes();
			};
			roundPanel2.Controls.Add(pillButton2);
			PillButton pillButton3 = new PillButton(PerformancePolicy.Text("检查与更新", "Check for updates"));
			pillButton3.Name = "aboutWebsiteDownload";
			pillButton3.Bg = Theme.Card;
			PillButton pillButton4 = pillButton3;
			pillButton4.SetBounds(Theme.S(32 + ratio), Theme.S(aboutPageValue5 - 50), Theme.S(ratio), Theme.S(36));
			pillButton4.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
			pillButton4.Click += delegate
			{
				OpenSoftwareUpdate();
			};
			roundPanel2.Controls.Add(pillButton4);
			RefreshAboutUpdate();
			pageAbout.SizeChanged += delegate
			{
				aboutViewport = Size.Empty;
				LayoutAboutViewport();
			};
			LayoutAboutViewport();
		}
		private void BuildAntiCheatPage()
		{
			acArranging = false;
			acOtherOpen = false;
			acDrawerKind = "";
			acPresentedRoster = new string[0];
			foreach (Control acRosterRow in acRosterRows)
			{
				acRosterRow.Dispose();
			}
			acRosterRows.Clear();
			pageAntiCheat.AutoScroll = false;
			acViewport = new DBPanel
			{
				Name = "AntiCheatWorkspace",
				BackColor = Theme.Bg,
				AutoScroll = true
			};
			Native.Dark(acViewport);
			pageAntiCheat.Controls.Add(acViewport);
			acRosterBar = new AcConsoleSurface
			{
				Name = "AntiCheatConsole"
			};
			acViewport.Controls.Add(acRosterBar);
			acControlBay = new DBPanel
			{
				Name = "AntiCheatControlBay",
				BackColor = Theme.Card
			};
			acRosterBar.Controls.Add(acControlBay);
			acTitleIcon = new AcGlyph
			{
				Key = "master"
			};
			acControlBay.Controls.Add(acTitleIcon);
			acTitleLabel = CardLabel(acControlBay, AutoText("专项压制", "Suppression"), 0, 0, 120, 28, 11f, true, Theme.Fg);
			acTitleLabel.Name = "acTitleLabel";
			acHelp = new PerformanceInfoButton
			{
				Name = "acInfo",
				AccessibleName = AutoText("专项压制 · 说明", "Suppression · Info"),
				Bg = Theme.Card
			};
			acHelp.Click += delegate
			{
				ShowAcHelp();
			};
			acControlBay.Controls.Add(acHelp);
			acStrengthLabel = CardLabel(acControlBay, Lang.T("ac.mode"), 0, 0, 90, 22, 9f, false, Theme.Dim);
			acStrengthLabel.Name = "acStrengthLabel";
			acModePicker = new AntiCheatStrengthPicker
			{
				Name = "acStrength",
				Labels = new string[3]
				{
					Lang.T("ac.mode.gentle"),
					Lang.T("ac.mode.balanced"),
					Lang.T("ac.mode.isolated")
				},
				Index = (int)tamer.Mode,
				AccessibleName = Lang.T("ac.mode"),
				Bg = Theme.Card
			};
			acModePicker.IndexChanged = delegate(int index)
			{
				tamer.Mode = (AntiCheatMode)index;
				RefreshAcGroupStates();
			};
			acControlBay.Controls.Add(acModePicker);
			acStrengthHelp = new PerformanceActionButton(AutoText("档位说明  ⓘ", "Mode details  ⓘ"))
			{
				Name = "AntiCheatStrengthInfo",
				Quiet = true,
				Bg = Theme.Card
			};
			acStrengthHelp.Click += delegate
			{
				ShowAcHelp();
			};
			acControlBay.Controls.Add(acStrengthHelp);
			acList = new AcModuleSurface
			{
				Name = "AntiCheatModules",
				BackColor = Theme.Card
			};
			acRosterBar.Controls.Add(acList);
			acUtility = new DBPanel
			{
				Name = "AntiCheatUtilities",
				BackColor = Theme.Card
			};
			acRosterBar.Controls.Add(acUtility);
			acRosterEntry = new AcDrawerButton(AutoText("兼容名单", "Compatibility list"), "roster")
			{
				Name = "AntiCheatRoster"
			};
			acRosterEntry.Click += delegate
			{
				OpenAcDrawer("roster");
			};
			acUtility.Controls.Add(acRosterEntry);
			acOtherToggle = new AcDrawerButton(AutoText("兼容与记录", "Compatibility & records"), "records")
			{
				Name = "AntiCheatOtherGroups"
			};
			acOtherToggle.Click += delegate
			{
				if (acDrawerKind == "records")
				{
					CloseAcDrawer();
				}
				else
				{
					OpenAcDrawer("records");
				}
			};
			acUtility.Controls.Add(acOtherToggle);
			lblAcRoster = new Label
			{
				Name = "AntiCheatRosterCount",
				AutoSize = false,
				BackColor = Theme.Card,
				ForeColor = Theme.Dim,
				Font = Theme.UI(9f, false)
			};
			acDrawer = new DBPanel
			{
				Name = "AntiCheatDrawer",
				BackColor = Theme.Card,
				Visible = false
			};
			acRosterBar.Controls.Add(acDrawer);
			acDrawerTitle = new Label
			{
				Name = "AntiCheatDrawerTitle",
				BackColor = Theme.Card,
				ForeColor = Theme.Fg,
				Font = Theme.UI(11f, true)
			};
			acDrawer.Controls.Add(acDrawerTitle);
			acDrawer.Controls.Add(lblAcRoster);
			acDrawerClose = new PerformanceActionButton(AutoText("返回模块", "Back to modules"))
			{
				Name = "AntiCheatDrawerClose",
				Bg = Theme.Card
			};
			acDrawerClose.Click += delegate
			{
				CloseAcDrawer();
			};
			acDrawer.Controls.Add(acDrawerClose);
			acClear = new PerformanceActionButton(Lang.T("btn.roster.clear"))
			{
				Name = "acClearRoster",
				Bg = Theme.Card,
				ForeColor = Theme.Danger
			};
			acClear.Click += delegate
			{
				OnClearRoster();
			};
			acDrawer.Controls.Add(acClear);
			acDrawerBody = new DBPanel
			{
				Name = "AntiCheatDrawerBody",
				BackColor = Theme.Card,
				AutoScroll = true
			};
			Native.Dark(acDrawerBody);
			acDrawer.Controls.Add(acDrawerBody);
			RefreshAcList();
			SyncAcRoster();
			pageAntiCheat.SizeChanged += delegate
			{
				LayoutAntiCheatViewport();
			};
			LayoutAntiCheatViewport();
		}
		private void BuildAutomationPage()
		{
			automationSync.Clear();
			ResetEnvironmentObservation();
			performancePrivacyPanels.Clear();
			environmentPrivacy = null;
			int automationPageValue = 16;
			string[] textEntries = new string[5]
			{
				AutoText("游戏性能", "Game"),
				AutoText("后台管理", "Background"),
				AutoText("图形与体验", "Graphics"),
				AutoText("延迟与系统", "System"),
				AutoText("实验室", "Laboratory")
			};
			performanceTabs = new TechTabs
			{
				Name = "performanceTabs",
				Visible = false,
				TabStop = false
			};
			performanceTabs.SetTabs(textEntries, new string[5]
			{
				AutoText("调度与加载", "Scheduling and loading"),
				AutoText("管理竞争游戏资源的后台任务", "Manage competing background tasks"),
				AutoText("显卡偏好与使用体验", "GPU preferences and experience"),
				AutoText("低延迟、计时器与系统环境", "Latency, timers and system environment"),
				AutoText("手动选择实验策略，按条件执行", "Explicit experimental strategies")
			});
			pagePolicy.Controls.Add(performanceTabs);
			performanceCategories = new PerformanceCategoryBar(textEntries)
			{
				Name = "PerformanceCategories"
			};
			performanceCategories.SetBounds(Theme.S(30), Theme.S(automationPageValue), Theme.S(ContentW), Theme.S(44));
			pagePolicy.Controls.Add(performanceCategories);
			BuildConfigurationPresetControls();
			automationPageValue += 58;
			performanceViewportTop = Theme.S(automationPageValue);
			performanceTabPanels = new DBPanel[5];
			for (int i = 0; i < performanceTabPanels.Length; i++)
			{
				PerformanceFeatureGrid performanceFeatureGrid = new PerformanceFeatureGrid();
				performanceFeatureGrid.Name = "PerformanceCategory" + i;
				performanceFeatureGrid.Visible = i == 0;
				PerformanceFeatureGrid performanceFeatureGrid2 = performanceFeatureGrid;
				performanceFeatureGrid2.SetBounds(Theme.S(20), performanceViewportTop, Theme.S(PageW - 40), Theme.S(PageH - automationPageValue - 8));
				pagePolicy.Controls.Add(performanceFeatureGrid2);
				performanceTabPanels[i] = performanceFeatureGrid2;
			}
			pageTabPanels[pagePolicy] = performanceTabPanels;
			performanceCategories.SelectionChanged = delegate(int index)
			{
				performanceTabs.Index = index;
			};
			performanceTabs.IndexChanged = delegate(int index)
			{
				for (int n = 0; n < performanceTabPanels.Length; n++)
				{
					DBPanel dBPanel2 = performanceTabPanels[n];
					if (dBPanel2.Visible)
					{
						tabScrollPositions[dBPanel2] = new Point(-dBPanel2.AutoScrollPosition.X, -dBPanel2.AutoScrollPosition.Y);
					}
					dBPanel2.Visible = n == index;
				}
				performanceCategories.Select(index, false);
				Point value;
				if (tabScrollPositions.TryGetValue(performanceTabPanels[index], out value))
				{
					performanceTabPanels[index].AutoScrollPosition = value;
				}
				((PerformanceFeatureGrid)performanceTabPanels[index]).Reflow();
				if (index == 2 || index == 3)
				{
					RefreshEnvironmentStateAsync();
				}
			};
			for (int j = 0; j < 3; j++)
			{
				int sy = 2;
				Control scroll = performanceTabPanels[j];
				List<PolicyItem> list = new List<PolicyItem>();
				foreach (PolicyItem item in PolicyCatalog.All)
				{
					if (!PerformancePolicy.IsIntent(item.Key) && !PerformancePolicy.IsRetired(item.Key) && !PerformancePolicy.IsGamePriorityOption(item.Key) && !(item.Key == "PowerPlanOn") && !(item.Key == "GmEnglishInputV1") && !(item.Key == "NvLowLat") && !(item.Key == "AutoTimerTick") && !(item.Key == "AutoGlobalTimer") && !(item.Key == "GmCacheWarmV2") && !(item.Key == "GmStandbyCleanerV2") && ManualPolicyCategory(item) == j)
					{
						list.Add(item);
					}
				}
				PerformanceRestartScope[] performanceRestartScopeItems = new PerformanceRestartScope[3]
				{
					PerformanceRestartScope.None,
					PerformanceRestartScope.ProcessRestart,
					PerformanceRestartScope.SystemRestart
				};
				foreach (PerformanceRestartScope scope in performanceRestartScopeItems)
				{
					Predicate<PolicyItem> match = (PolicyItem item) => PerformanceRestartFor(item.Key) == scope;
					List<PolicyItem> list2 = list.FindAll(match);
					bool anyConditionMet = HasAutomationInventory(j, scope) || (j == 0 && scope == PerformanceRestartScope.None) || (j == 2 && HasPerformanceEnvironmentGraphics(scope));
					if (list2.Count == 0 && !anyConditionMet)
					{
						continue;
					}
					foreach (PolicyItem item2 in list2)
					{
						if (PrimaryPerformancePolicy(item2.Key))
						{
							AddManualPolicyControl(scroll, ref sy, item2);
						}
					}
					foreach (PolicyItem item3 in list2)
					{
						if (!PrimaryPerformancePolicy(item3.Key))
						{
							AddManualPolicyControl(scroll, ref sy, item3);
						}
					}
					AddAutomationInventory(scroll, ref sy, j, scope);
					if (j == 0 && scope == PerformanceRestartScope.None)
					{
						BuildEnvironmentPerformanceOptions(scroll, ref sy);
					}
					if (j == 2)
					{
						BuildPerformanceEnvironmentGraphics(scroll, ref sy, scope);
					}
				}
			}
			int sy2 = 2;
			BuildPerformanceSystemOptions(performanceTabPanels[3], ref sy2);
			PrivacyOptionsPanel backgroundPrivacy = BuildEnvironmentPrivacy(performanceTabPanels[1], PerformanceRestartScope.ProcessRestart, action => action.Key == PrivacyActionCatalog.BackgroundGlobal);
			backgroundPrivacy.Top = Theme.S(2);
			int labY = 2;
			BuildLaboratoryOptions(performanceTabPanels[4], ref labY);
			DBPanel[] performanceTabPanelsSnapshot = performanceTabPanels;
			foreach (DBPanel dBPanel in performanceTabPanelsSnapshot)
			{
				ConfigurePerformanceCards(dBPanel);
			}
			OrderPerformanceConsole();
			foreach (DBPanel dBPanel in performanceTabPanels)
			{
				GroupPerformanceLifetimes(dBPanel);
			}
			pagePolicy.SizeChanged += delegate
			{
				LayoutPerformanceViewport();
			};
			LayoutPerformanceViewport();
			DBPanel[] performanceTabPanelsSnapshot2 = performanceTabPanels;
			for (int m = 0; m < performanceTabPanelsSnapshot2.Length; m++)
			{
				PerformanceFeatureGrid performanceFeatureGrid3 = (PerformanceFeatureGrid)performanceTabPanelsSnapshot2[m];
				performanceFeatureGrid3.Complete();
			}
			RefreshAutomationPage();
		}
		private void BuildCriticalThreadEditor(Control parent, int width)
		{
			criticalThreadEditor = new CoreThreadSurface();
			parent.Controls.Add(criticalThreadEditor);
			criticalThreadProfile = (criticalThreadExpected = (criticalThreadError = null));
			criticalThreadEditor.Name = "CriticalThreadEditor";
			criticalThreadTitle = CardLabel(criticalThreadEditor, ThreadUi("渲染线程亲和性", "Render thread affinity"), 18, 12, width - 104, 30, 11f, true, Theme.Fg);
			criticalThreadEnabled = new FamilySuppressionSwitch(true)
			{
				Name = "libraryThreadAffinityToggle",
				DetailsWidth = 62,
				Text = ThreadUi("渲染线程亲和性", "Render thread affinity"),
				IconKey = "threads"
			};
			criticalThreadEnabled.Click += delegate
			{
				if (!criticalThreadSyncing)
				{
					SaveCriticalThreadSwitch(!criticalThreadEnabled.Checked);
				}
			};
			criticalThreadEditor.Controls.Add(criticalThreadEnabled);
			criticalThreadHelp = CardLabel(criticalThreadEditor, ThreadUi("逐游戏，默认关闭。通过独立弹窗选择自动或手动落核；手动只指定一个逻辑 CPU，仍自动识别关键线程。候选跟随核心调度范围，未开核心调度则显示全核；范围冲突在保存前处理。开启、换核与范围修改下局生效，关闭立即撤回。至少选中三个不同物理核，每核可只选一个逻辑线程，无需补全超线程。打开后：游戏在前台时，把一个持续占用至少 30% 单核的关键线程硬绑定到独立物理核心（渲染线程优先，需 DirectX 画面提交事件；识别不到时取持续 30 秒最忙且明显领先的线程，几个线程负载接近时视为工作线程池，交给系统调度）。其他持续接近跑满（至少 70%）的线程只设为优先核，核心忙时系统仍可把它挪走。各线程用不同物理核，数量按处理器拓扑自动定（最多 4 个；大小核只用性能核，优先优选核与 X3D 大缓存 CCD，尽量避开 CPU 0 所在核，至少给其他线程留两个物理核）；自动模式先让当前其他游戏线程避开该整颗物理核（含超线程同胞），全部回读通过后绑定关键线程并隔离普通外部进程；至少给工作线程保留两个物理核。新线程在下一次核验后接管；权限不足或线程已有自定义分配时撤回，不强制覆盖。旧手动规则仍按保存策略执行。切出游戏保留，退局时还原；游戏主动改回分配时让出，连续无法核验或候选反复更换时恢复并停止本局重绑。同一线程不叠加额外线程提优。失败后本局停止重新绑定。不隔离中断/DPC；外部程序自行硬绑定仍可能进入。新线程接管有间隔，不保证帧率收益。", "Per game, off by default. Use the separate dialog for Automatic or Manual CPU selection. Manual chooses one logical CPU while thread identification remains automatic. Candidates follow core scheduling, or all CPUs when it is off; conflicts must be resolved before saving. Enabling, CPU and range edits apply next session; disabling withdraws immediately. Select at least three distinct physical cores; one logical processor per core is enough, without adding SMT siblings. When on: while the game is in front, one critical thread that keeps at least 30% of one core busy gets hard affinity on its own physical core (the render thread first, from DirectX present events; without them, the busiest thread after 30 seconds when it clearly leads; several threads with similar loads are treated as a worker pool and left to the scheduler). Other threads that stay near saturation (at least 70%) only receive a preferred core, and Windows may still move them while that core is busy. Each thread uses a different physical core; the count follows the CPU topology (up to 4; hybrid CPUs use performance cores only; favored cores and the large-cache X3D CCD come first, the core of CPU 0 is avoided where possible, and two physical cores stay free for other threads). Automatic mode first moves the current game workers off the entire selected physical core, including its SMT sibling. Once every worker is verified, it binds the critical thread and reserves the core against ordinary external processes. At least two physical cores remain for workers. New threads are admitted at the next audit; missing permissions or custom placement withdraw the operation without forcing it. Older manual rules retain their saved policy. Switching away keeps the placement; it is restored when the session ends. Placement yields when the game changes it; repeated verification failures or candidate changes restore the original state and stop rebinding for the session. Extra priority boosts are not layered onto the same thread. Failed placement stops rebinding for the session. Interrupts/DPCs are not isolated, and an external program with explicit hard affinity may still enter. New thread admission has a delay; FPS gains are not guaranteed."), 18, 50, width - 36, 80, 9f, false, Theme.Dim);
			criticalThreadHelp.AutoEllipsis = false;
			criticalThreadCompanion = CardLabel(criticalThreadEditor, "", 18, 0, width - 36, 40, 9f, false, Theme.Dim);
			criticalThreadCompanion.Name = "CriticalThreadCompanion";
			criticalThreadCompanion.AutoEllipsis = false;
			criticalThreadResult = CardLabel(criticalThreadEditor, "", 18, 0, width - 36, 40, 9f, false, Theme.Dim);
			criticalThreadResult.Name = "CriticalThreadSaveResult";
			criticalThreadResult.AutoEllipsis = false;
			criticalThreadRuntime = CardLabel(criticalThreadEditor, "", 18, 0, width - 36, 60, 9f, false, Theme.Dim);
			criticalThreadRuntime.Name = "CriticalThreadRuntime";
			criticalThreadRuntime.AutoEllipsis = false;
			Label label = criticalThreadTitle;
			Label label2 = criticalThreadHelp;
			Label label3 = criticalThreadCompanion;
			bool visibleValue = (criticalThreadRuntime.Visible = false);
			bool visibleValue2 = (label3.Visible = visibleValue);
			bool visible = (label2.Visible = visibleValue2);
			label.Visible = visible;
			criticalThreadInfo = new PerformanceInfoButton
			{
				Name = "LibraryThreadAffinityInfo",
				AccessibleName = ThreadUi("渲染线程亲和性说明与状态", "Render thread affinity details and status")
			};
			criticalThreadInfo.Click += delegate
			{
				string body = criticalThreadHelp.Text + "\r\n\r\n" + criticalThreadResult.Text + "\r\n\r\n" + criticalThreadCompanion.Text + "\r\n\r\n" + criticalThreadRuntime.Text;
				PaviseDialog.Info(LibraryDialogOwner, criticalThreadEnabled.Text, body);
			};
			criticalThreadEditor.Controls.Add(criticalThreadInfo);
			criticalThreadMode = new GameCardDetailsButton
			{
				Name = "RenderAffinitySettings",
				ShowAdjustLabel = true,
				AccessibleName = ThreadUi("设置渲染亲和性自动或手动选核", "Set automatic or manual render CPU placement")
			};
			criticalThreadMode.Click += delegate { OpenRenderAffinityEditor(); };
			criticalThreadEnabled.Controls.Add(criticalThreadMode);
			criticalThreadEditor.SizeChanged += delegate
			{
				LayoutCriticalThreadEditor();
			};
		}
		private void BuildLogPage()
		{
			pageLog.SetBounds(0, Theme.S(TopH), Theme.S(WinW), Theme.S(PageH));
			int contentWOffset = ContentW + RailW;
			int pageHeaderResult = PageHeader(pageLog, Lang.T("nav.log"), Lang.T("v20.log.sub"), 1, true, 0, contentWOffset);
			RoundPanel roundPanel = MakeConsolePanel(pageLog, 30, pageHeaderResult, contentWOffset, 64, true);
			roundPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			logActionsButton = new PillButton(Lang.T("log.category.actions"));
			logActionsButton.Name = "showActionLog";
			logActionsButton.SetBounds(Theme.S(18), Theme.S(14), Theme.S(128), Theme.S(36));
			logActionsButton.Click += delegate { SelectLogCategory(false); };
			logDiagnosticsButton = new PillButton(Lang.T("log.category.diagnostics"));
			logDiagnosticsButton.Name = "showDiagnosticLog";
			logDiagnosticsButton.SetBounds(Theme.S(154), Theme.S(14), Theme.S(144), Theme.S(36));
			logDiagnosticsButton.Click += delegate { SelectLogCategory(true); };
			roundPanel.Controls.AddRange(new Control[] { logActionsButton, logDiagnosticsButton });
			CardLabel(roundPanel, Lang.T("log.latest.session"), 320, 7, contentWOffset - 338, 24, 10.5f, true, Theme.Fg).Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			lblLogStreamHint = new Label
			{
				Name = "logSessionHint",
				ForeColor = Theme.Faint,
				BackColor = Color.Transparent,
				Font = Theme.UI(7.8f, false),
				TextAlign = ContentAlignment.MiddleLeft,
				AutoEllipsis = true
			};
			lblLogStreamHint.SetBounds(Theme.S(320), Theme.S(31), Theme.S(contentWOffset - 338), Theme.S(24));
			lblLogStreamHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
			roundPanel.Controls.Add(lblLogStreamHint);
			int pageHeaderResultOffset = pageHeaderResult + 76;
			int lowerBound = Math.Max(PageH - 48, pageHeaderResultOffset + 190);
			logWrap = new RoundPanel();
			logWrap.SetBounds(Theme.S(30), Theme.S(pageHeaderResultOffset), Theme.S(contentWOffset), Theme.S(lowerBound - pageHeaderResultOffset - 10));
			logWrap.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
			logWrap.BackColor = Theme.Bg;
			logWrap.Fill = Theme.Inset;
			logWrap.Border = Theme.StrokeHi;
			logWrap.Radius = Theme.S(14);
			logWrap.AccentEdge = true;
			logWrap.Padding = new Padding(Theme.S(5));
			logStream = new LogStreamView();
			ConfigureLogView();
			logStream.Dock = DockStyle.Fill;
			Native.Dark(logStream);
			logWrap.Controls.Add(logStream);
			PillButton pillButton = new PillButton(Lang.T("v20.log.refresh"));
			pillButton.Name = "refreshSessionLog";
			PillButton pillButton2 = pillButton;
			pillButton2.SetBounds(Theme.S(30), Theme.S(lowerBound), Theme.S(96), Theme.S(36));
			pillButton2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			pillButton2.Click += delegate
			{
				RefreshLog(true);
			};
			PillButton exportLog = new PillButton(Lang.T("log.export.file"));
			logExportButton = exportLog;
			exportLog.Name = "exportLogFile";
			exportLog.AccessibleName = Lang.T("log.export.file");
			exportLog.SetBounds(Theme.S(136), Theme.S(lowerBound), Theme.S(112), Theme.S(36));
			exportLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			exportLog.Click += ExportLogFile;
			PillButton pillButton3 = new PillButton(Lang.T("rep.clear.log"), BtnKind.Danger);
			pillButton3.SetBounds(Theme.S(258), Theme.S(lowerBound), Theme.S(112), Theme.S(36));
			pillButton3.Name = "clearSessionLog";
			pillButton3.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
			pillButton3.Click += delegate
			{
				if (PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T("rep.clear.ask"), DlgKind.Warn))
				{
					Logger.Clear();
					Logger.Log(Lang.T("log.panelformlogpage.1"));
					Logger.RuntimeIdentity();
					RefreshLog(true);
				}
			};
			swLogWrites = MakeSwitch(Settings.Load("LogWritesEnabled", true), OnLogWritesToggle);
			swLogWrites.Location = new Point(Theme.S(30 + contentWOffset) - swLogWrites.Width, Theme.S(lowerBound + 6));
			swLogWrites.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
			Label label = new Label();
			label.Text = Lang.T("set.logwrites");
			label.ForeColor = Theme.Dim;
			label.BackColor = Theme.Bg;
			label.Font = Theme.UI(8.6f, false);
			label.TextAlign = ContentAlignment.MiddleRight;
			Label label2 = label;
			label2.SetBounds(Theme.S(30 + contentWOffset - 162) - swLogWrites.Width, Theme.S(lowerBound), Theme.S(152), Theme.S(36));
			label2.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
			pageLog.Controls.AddRange(new Control[6] { logWrap, pillButton2, exportLog, pillButton3, label2, swLogWrites });
			RefreshLog(true);
		}
		private void BuildManualMemoryCleaner(OverviewUsageItem memory)
		{
			overviewMemoryCleanup = new OverviewMemoryCleanupControl(elevated)
			{
				Name = "overviewMemoryCleanup"
			};
			memory.AttachMemoryAction(overviewMemoryCleanup);
			overviewMemoryCleanup.SetGamePolicy(gameMode.IsActive);
			if (manualMemoryReport != null)
			{
				overviewMemoryCleanup.ShowReport(manualMemoryReport);
			}
			else if (Volatile.Read(ref manualMemoryPending) != 0)
			{
				overviewMemoryCleanup.ShowProgress(manualMemoryProgressNumber, manualMemoryProgressStep);
			}
			overviewMemoryCleanup.GeometryChanged += delegate
			{
				overviewViewport = Size.Empty;
				LayoutOverviewPage();
			};
			overviewMemoryCleanup.CleanRequested += delegate
			{
				StartManualMemoryCleanup();
			};
			if (!manualMemoryDisposeRegistered)
			{
				manualMemoryDisposeRegistered = true;
				base.Disposed += delegate
				{
					manualMemoryDisposed = true;
				};
			}
		}
		private void BuildAppearanceSection(Control scroll, ref int sy)
		{
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel = new SettingsAppearanceEditorPanel();
			settingsAppearanceEditorPanel.Name = "SettingsAppearanceEditor";
			settingsAppearanceEditorPanel.BackColor = Theme.Card;
			settingsAppearanceEditorPanel.AccessibleRole = AccessibleRole.Grouping;
			settingsAppearanceEditorPanel.AccessibleName = Lang.T("sec.appearance");
			SettingsAppearanceEditorPanel settingsAppearanceEditorPanel2 = settingsAppearanceEditorPanel;
			settingsAppearanceEditorPanel2.SetBounds(0, Theme.S(sy), Theme.S(ScrollContentW), Theme.S(200));
			scroll.Controls.Add(settingsAppearanceEditorPanel2);
			backdropPreview = new BackdropPreview();
			backdropPreview.Name = "SettingsBackdropPreview";
			backdropPreview.EmptyText = Lang.T("backdrop.preview.empty");
			settingsAppearanceEditorPanel2.Controls.Add(backdropPreview);
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("backdrop.title"), 0, 0, 280, 24, 10.5f, true, Theme.Fg).Name = "SettingsBackdropTitle";
			lblBackdropState = CardLabel(settingsAppearanceEditorPanel2, "", 0, 0, 280, 40, 8.5f, false, Theme.Dim);
			lblBackdropState.Name = "SettingsBackdropState";
			btnBackdrop = new PillButton(Lang.T(Backdrop.Active ? "btn.backdrop.change" : "btn.backdrop.set"));
			btnBackdrop.Name = "SettingsBackdropChoose";
			btnBackdrop.Size = new Size(Theme.S(116), Theme.S(30));
			btnBackdrop.Click += delegate
			{
				OnBackdropPick();
			};
			settingsAppearanceEditorPanel2.Controls.Add(btnBackdrop);
			btnBackdropReset = new PillButton(Lang.T("btn.backdrop.reset"));
			btnBackdropReset.Name = "SettingsBackdropReset";
			btnBackdropReset.Size = new Size(Theme.S(104), Theme.S(30));
			btnBackdropReset.Click += delegate
			{
				OnBackdropReset();
			};
			settingsAppearanceEditorPanel2.Controls.Add(btnBackdropReset);
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("backdrop.dim"), 0, 0, 160, 24, 8.6f, true, Theme.Fg).Name = "SettingsBackdropDimTitle";
			pickBackdropDim = new TierPicker
			{
				Name = "SettingsBackdropDimPicker"
			};
			pickBackdropDim.Labels = new string[4]
			{
				Lang.T("backdrop.dim.off"),
				Lang.T("backdrop.dim.1"),
				Lang.T("backdrop.dim.2"),
				Lang.T("backdrop.dim.3")
			};
			pickBackdropDim.Index = (Backdrop.Active ? (Backdrop.Dim + 1) : 0);
			pickBackdropDim.Size = new Size(Theme.S(276), Theme.S(30));
			pickBackdropDim.IndexChanged = OnBackdropDim;
			settingsAppearanceEditorPanel2.Controls.Add(pickBackdropDim);
			Panel panel = new Panel();
			panel.Name = "SettingsAppearanceSeparator";
			panel.BackColor = Theme.Stroke;
			settingsAppearanceEditorPanel2.Controls.Add(panel);
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("sec.theme"), 0, 0, 280, 24, 10.5f, true, Theme.Fg).Name = "SettingsAccentTitle";
			CardLabel(settingsAppearanceEditorPanel2, Lang.T("theme.hint"), 0, 0, 280, 18, 8f, false, Theme.Dim).Name = "SettingsAccentHint";
			BuildThemeColorRows(settingsAppearanceEditorPanel2, 0);
			SyncBackdropAppearance();
			BuildMotionAppearance(scroll);
			sy += 208;
		}
		private void BuildThemeColorRows(Control parent, int startY)
		{
			PerformancePreset[] visibleOrderResult = PresetValue.VisibleOrder();
			int processedCount = 0;
			PerformancePreset[] performancePresetItems = visibleOrderResult;
			foreach (PerformancePreset performancePreset in performancePresetItems)
			{
				PerformancePreset capMode = performancePreset;
				modeSwatches[(int)performancePreset].Clear();
				int v = startY + processedCount * 34;
				bool matchesPerformancePreset = performancePreset == gameMode.ActivePreset;
				Label label = new Label();
				label.Text = ModeName(performancePreset);
				label.ForeColor = (matchesPerformancePreset ? Theme.ModeColor(performancePreset) : Theme.Fg);
				label.BackColor = Color.Transparent;
				label.Font = Theme.UI(8.6f, matchesPerformancePreset);
				label.TextAlign = ContentAlignment.MiddleLeft;
				label.SetBounds(Theme.S(18), Theme.S(v), Theme.S(82), Theme.S(26));
				parent.Controls.Add(label);
				modeAccentLabels[(int)performancePreset] = label;
				int themeColorRowsValue = 108;
				bool hasModeColorOverride = Theme.HasModeColorOverride(performancePreset);
				ColorSwatch colorSwatch = new ColorSwatch(Theme.ModeColorBuiltin(performancePreset));
				colorSwatch.IsDefault = true;
				colorSwatch.Selected = !hasModeColorOverride;
				colorSwatch.SetBounds(Theme.S(themeColorRowsValue), Theme.S(v), Theme.S(26), Theme.S(26));
				colorSwatch.Picked = delegate
				{
					OnModeColorPick(capMode, Color.Empty);
				};
				parent.Controls.Add(colorSwatch);
				modeSwatches[(int)performancePreset].Add(colorSwatch);
				themeColorRowsValue += 32;
				Color a = Theme.ModeColor(performancePreset);
				Color[] accentPalette = AccentPalette;
				foreach (Color color in accentPalette)
				{
					Color cc = color;
					ColorSwatch colorSwatch2 = new ColorSwatch(cc);
					colorSwatch2.Selected = hasModeColorOverride && SameRgb(a, cc);
					colorSwatch2.SetBounds(Theme.S(themeColorRowsValue), Theme.S(v), Theme.S(26), Theme.S(26));
					colorSwatch2.Picked = delegate
					{
						OnModeColorPick(capMode, cc);
					};
					parent.Controls.Add(colorSwatch2);
					modeSwatches[(int)performancePreset].Add(colorSwatch2);
					themeColorRowsValue += 32;
				}
				processedCount++;
			}
		}
		private void BuildUi(Icon appIcon)
		{
			modeVisualInitialized = false;
			visualPolicySource = null;
			builtLang = Lang.Cur;
			Text = "PAVISE AUTO";
			base.Icon = appIcon;
			base.AutoScaleMode = AutoScaleMode.None;
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterScreen;
			base.MaximizeBox = false;
			if (!fitting)
			{
				if (Dpi.SetDesignSize(WinW, WinH)) Theme.DropFontCache();
				base.ClientSize = new Size(Theme.S(WinW), Theme.S(WinH));
			}
			BackColor = Theme.Bg;
			Font = Theme.UI(9.5f, false);
			AttachFormFrame();
			BuildNavigation();
			pageTabPanels.Clear();
			tabScrollPositions.Clear();
			DBPanel topBar = new DBPanel
			{
				Name = "WorkspaceTopBar"
			};
			topBar.SetBounds(Theme.S(RailW), 0, Theme.S(WinW - RailW), Theme.S(TopH));
			topBar.BackColor = Theme.Bg;
			topBar.MouseDown += DragMove;
			topBar.Paint += delegate(object sender, PaintEventArgs e)
			{
				using (Pen pen = new Pen(Theme.Stroke))
				{
					e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
				}
			};
			lblSub = new Label();
			lblSub.Text = (elevated ? (Lang.T("title.admin") + " " + Lang.T("v20.admin.ready")) : Lang.T("title.noelev"));
			lblSub.ForeColor = (elevated ? Theme.Faint : Theme.Danger);
			lblSub.BackColor = Color.Transparent;
			lblSub.Font = Theme.UI(9f, false);
			lblSub.UseCompatibleTextRendering = false;
			lblSub.AutoEllipsis = true;
			lblSub.Name = "PrivilegeStatus";
			lblSub.Padding = new Padding(Theme.S(24), 0, 0, 0);
			lblSub.TextAlign = ContentAlignment.MiddleLeft;
			lblSub.SetBounds(0, 0, 1, 1);
			lblSub.Visible = false;
			lblSub.Paint += delegate(object sender, PaintEventArgs e)
			{
				Glyphs.Draw(e.Graphics, "shield", new Rectangle(0, Theme.S(4), Theme.S(16), Theme.S(16)), lblSub.ForeColor);
			};
			WorkspaceBrand workspaceBrand = new WorkspaceBrand();
			workspaceBrand.Name = "WorkspaceBrand";
			WorkspaceBrand workspaceBrand2 = workspaceBrand;
			workspaceBrand2.SetState(gameMode.ActivePreset, gameMode.Enabled);
			workspaceBrand2.SetBounds(Theme.S(24), Theme.S(4), Theme.S(270), Theme.S(52));
			workspaceBrand2.MouseDown += DragMove;
			modeButton = new ModeButton();
			modeButton.Compact = true;
			modeButton.Name = "StrategyButton";
			modeButton.SetBounds(Theme.S(PageW - 342), Theme.S(12), Theme.S(232), Theme.S(46));
			modeButton.Clicked = ToggleModeFlyout;
			modeButton.SetMode(gameMode.ActivePreset);
			modeButton.SetSource(ModeSourceText(gameMode.SessionPolicySourceName, true));
			themeSwitch = new ThemeSwitch(Theme.LightMode);
			themeSwitch.Compact = false;
			themeSwitch.Name = "WorkspaceThemeSwitch";
			themeSwitch.SetBounds(Theme.S(PageW - 450), Theme.S(12), Theme.S(94), Theme.S(46));
			themeSwitch.Toggled = OnThemeToggled;
			BuildHeaderLinks();
			BuildStrategyControls();
			int scaledDimension = Theme.S(WinW - RailW);
			CaptionButton captionButton = new CaptionButton(false);
			captionButton.Name = "WindowMinimize";
			captionButton.AccessibleName = AutoText("最小化", "Minimize");
			captionButton.SetBounds(scaledDimension - Theme.S(92), 0, Theme.S(44), Theme.S(64));
			captionButton.Click += delegate
			{
				base.WindowState = FormWindowState.Minimized;
			};
			CaptionButton captionButton4 = new CaptionButton(true);
			captionButton4.Name = "WindowClose";
			captionButton4.AccessibleName = AutoText("关闭窗口", "Close window");
			captionButton4.SetBounds(scaledDimension - Theme.S(48), 0, Theme.S(44), Theme.S(64));
			captionButton4.Click += delegate
			{
				BeginOutro();
			};
			topBar.Controls.AddRange(new Control[8] { workspaceBrand2, lblSub, headerWebsite, btnDonate, themeSwitch, modeButton, captionButton, captionButton4 });
			pages = new DBPanel[(int)PageId.Count];
			pages[0] = (pageOverview = MakePage());
			pages[1] = (pageLibrary = MakePage());
			pages[10] = (pageWhitelist = MakePage());
			pages[2] = (pagePolicy = MakePage());
			pages[12] = (pageCoreScheduling = MakePage());
			pages[3] = (pageAntiCheat = MakePage());
			pages[4] = (pageGraphics = MakePage());
			pages[5] = (pageEnvironment = MakePage());
			pages[6] = (pageAudit = MakePage());
			pages[11] = (pageIrq = MakePage());
			pages[7] = (pageLog = MakePage());
			pages[8] = (pageSettings = MakePage());
			pages[9] = (pageAbout = MakePage());
			BuildOverviewPage();
			BuildLibraryPage();
			BuildWhitelistPage();
			BuildAutomationPage();
			BuildCoreSchedulingPage();
			BuildAntiCheatPage();
			BuildAuditPage();
			BuildLogPage();
			BuildSettingsPage();
			BuildAboutPage();
			RegisterPages();
			root = new WorkspacePanel();
			root.SetBounds(0, 0, Theme.S(WinW), Theme.S(WinH));
			root.BackColor = Theme.Bg;
			root.Controls.Add(topBar);
			DBPanel[] pagesSnapshot = pages;
			foreach (DBPanel value in pagesSnapshot)
			{
				root.Controls.Add(value);
			}
			root.Controls.Add(nav);
			coreBackBar = new AdvancedBackBar(Lang.T("cfg.back"), "RETURN // LIBRARY");
			coreBackBar.Name = "coreBackToLibrary";
			coreBackBar.SetBounds(Theme.S(24), 0, Theme.S(230), Theme.S(TopH));
			coreBackBar.BackRequested = delegate
			{
				nav.Select(1);
			};
			coreBackBar.Visible = false;
			root.Controls.Add(coreBackBar);
			overviewBackBar = new AdvancedBackBar(AutoText("返回设置", "Back to settings"), "RETURN // SETTINGS");
			overviewBackBar.Name = "detailsBackToOverview";
			overviewBackBar.SetBounds(Theme.S(24), 0, Theme.S(230), Theme.S(TopH));
			overviewBackBar.BackRequested = delegate
			{
				nav.Select(8);
			};
			overviewBackBar.Visible = false;
			root.Controls.Add(overviewBackBar);
			root.Controls.Add(modeFlyout);
			modeFlyout.BringToFront();
			base.Controls.Add(root);
			LayoutWorkspaceViewport();
			CenterRoot();
			base.KeyPreview = true;
			base.KeyDown -= OnEscHide;
			base.KeyDown += OnEscHide;
			nav.Select(0);
			if (Backdrop.Active)
			{
				ApplyBackdropLabels(this);
			}
			uiTimer = new System.Windows.Forms.Timer();
			uiTimer.Interval = 1200;
			uiTimer.Tick += OnUiTick;
			uiActivityKnown = false;
			SyncUiActivity();
		}
		private void BuildStrategyControls()
		{
			powerPlanChoices.Clear();
			modeFlyout = new StrategyFlyout
			{
				Visible = false,
				Trigger = modeButton
			};
			modeFlyout.Dismiss = delegate
			{
				SetModeFlyout(false);
			};
			powerButton = modeFlyout.Power;
			ConfigureLaptopPerformanceToggle();
			ConfigureBackgroundGovernanceControl();
			modeFlyout.Scene.AccessibleName = AutoText("游戏场景", "Game scene");
			modeFlyout.Scene.IndexChanged = delegate(int index)
			{
				ChooseGlobalMode((index == 1) ? PerformancePreset.Competitive : PerformancePreset.Standard);
			};
			powerButton.SelectionChanged += delegate(int index)
			{
				if (index >= 0 && index < powerPlanChoices.Count)
				{
					ChoosePowerPlan(powerPlanChoices[index]);
				}
			};
		}
		private void BuildHeaderLinks()
		{
			headerWebsite = new RogLinkButton(Lang.T("site.entry"), "", "globe")
			{
				Name = "WorkspaceWebsite"
			};
			headerWebsite.Click += delegate
			{
				OpenExternal("https://pavise.club/");
			};
			RogLinkButton donate = (btnDonate = new RogLinkButton(Lang.T("donate.entry"), "", "heart")
			{
				Name = "WorkspaceDonate",
				External = false
			});
			donate.Click += delegate
			{
				ShowDonate();
			};
			donate.TextChanged += delegate
			{
				if (donate.Parent != null)
				{
					LayoutWorkspaceHeader(donate.Parent);
				}
			};
			RefreshDonateButton();
		}
		private void BuildNavigation()
		{
			navigationScale = Dpi.Scale;
			string[] textEntries = new string[(int)PageId.Count]
			{
				Lang.T("nav.overview"),
				Lang.T("nav.library"),
				Lang.T("v20.advanced.nav.policy"),
				Lang.T("v20.advanced.nav.anticheat"),
				Lang.T("nav.graphics"),
				Lang.T("v20.advanced.nav.system"),
				Lang.T("v20.nav.report"),
				Lang.T("nav.log"),
				Lang.T("nav.set"),
				Lang.T("nav.about"),
				Lang.T("nav.white"),
				Lang.T("v20.advanced.nav.irq"),
				Lang.T("nav.corescheduling")
			};
			string[] icons = new string[(int)PageId.Count]
			{
				"game", "tiles", "settings", "acshield", "gpu", "chip", "chart", "log", "gear", "info",
				"white", "chip", "chip"
			};
			string[] cloneResult = (string[])textEntries.Clone();
			cloneResult[1] = PerformancePolicy.Text("游戏库", "Library");
			cloneResult[2] = PerformancePolicy.Text("性能", "Performance");
			cloneResult[10] = Lang.T("nav.white");
			nav = new NavRail(cloneResult, icons, new int[8] { 0, 1, 10, 2, 3, 6, 8, 9 }, new int[1] { 7 }, new string[1] { "" }, 0);
			nav.Name = "MainNavigation";
			nav.ShowBranding = false;
			nav.SelectionChanged = ShowPage;
			nav.ItemInvoked = delegate(int page)
			{
				nav.Select(page);
			};
			tuningNav = new NavRail(textEntries, icons, new int[4] { 2, 4, 5, 11 }, null, null, 0);
			tuningNav.Name = "TuningNavigation";
			tuningNav.ShowBranding = false;
			tuningNav.SectionTitle = Lang.T("v20.advanced.title");
			tuningNav.SelectionChanged = delegate(int page)
			{
				if (IsAdvancedPage(page))
				{
					nav.Select(page);
				}
			};
			NavRail[] navRailItems = new NavRail[2] { nav, tuningNav };
			foreach (NavRail navRail in navRailItems)
			{
				AssertNavMatchesPageIds(navRail);
				navRail.SetBounds(Theme.S((WinW - 920) / 2), Theme.S(WinH - 88), Theme.S(920), Theme.S(72));
				navRail.SetMode(visualMode, visualEnabled);
			}
			tuningNav.Visible = false;
			advBackBar = new AdvancedBackBar();
			advBackBar.SetBounds(0, 0, Theme.S(230), Theme.S(TopH));
			advBackBar.BackRequested = ReturnFromDeepTuning;
			tuningNav.Controls.Add(advBackBar);
		}
	}
}
