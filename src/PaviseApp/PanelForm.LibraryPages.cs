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
		private string PerGameAutomationLabel()
		{
			return AutoText("按游戏方案", "Per-game plan");
		}
		private GameProfile CorePageProfile()
		{
			if (string.IsNullOrEmpty(coreProfileId))
			{
				return null;
			}
			foreach (GameProfile profile in gameMode.GetProfiles())
			{
				if (string.Equals(profile.Id, coreProfileId, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}
		private string EnvironmentGameDvrStatus()
		{
			string automaticExecutionStateResult = gameMode.AutomaticExecutionState("GameDvrOff");
			switch (automaticExecutionStateResult)
			{
			case "failed":
			case "restore-failed":
			case "stopped":
				return GameMode.AutomationStateLabel(automaticExecutionStateResult);
			default:
				if (!gameMode.KillGameDvr)
				{
					return Lang.T((automaticExecutionStateResult == "restored") ? "env.dvr.restored" : "env.performance.off");
				}
				if (!gameMode.Enabled)
				{
					return Lang.T("env.dvr.waiting");
				}
				return Lang.T((automaticExecutionStateResult == "applied") ? "env.dvr.active" : "env.performance.pending");
			}
		}
		private Dictionary<string, bool> QueryLibraryRunningState(List<string> paths)
		{
			return ProbeRunning(paths);
		}
		public void NotifyLibraryChanged()
		{
			try
			{
				if (!base.IsHandleCreated || base.IsDisposed)
				{
					return;
				}
				BeginInvoke((MethodInvoker)delegate
				{
					if (!base.IsDisposed && lstGames != null)
					{
						RefreshGames();
					}
				});
			}
			catch
			{
			}
		}
		private void ToggleGameCoreScheduling(GameLibraryItem item)
		{
			if (item == null || item.Profile == null)
			{
				return;
			}
			string id = item.Profile.Id;
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile == null)
			{
				RefreshGames();
				return;
			}
			if (PolicyResolver.For(gameProfile).ManualCores == null)
			{
				OpenLibraryCoreScheduling(id);
				return;
			}
			bool isNotAutoCoresAllowed = !PolicyResolver.For(gameProfile).AutoCoresAllowed;
			string error = SaveCoreConfigurationWithRender(gameProfile, ManualCorePlan.Raw(gameProfile), PolicyResolver.For(gameProfile).ManualCores, isNotAutoCoresAllowed);
			if (error != null && !gameMode.ProfileStoreSaveFailed)
			{
				PaviseDialog.Warn(LibraryDialogOwner, Lang.T("nav.corescheduling"), Lang.T(error));
			}
			RefreshGames();
			SelectProfile(id);
		}
		private void ToggleGameFullscreenOptimizations(GameLibraryItem item)
		{
			if (fullscreenOptimizationsChangeBusy || item == null || item.Profile == null)
			{
				return;
			}
			fullscreenOptimizationsChangeBusy = true;
			string id = item.Profile.Id;
			try
			{
				GameProfile gameProfile = FindLibraryProfile(id);
				if (gameProfile != null && !gameMode.SetProfileFullscreenOptimizations(id, !gameProfile.DisableFullscreenOptimizations) && !gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.fso"), Lang.T("lib.fso.failed"));
				}
			}
			finally
			{
				fullscreenOptimizationsChangeBusy = false;
				if (!base.IsDisposed && !base.Disposing)
				{
					RefreshGames();
					SelectProfile(id);
				}
			}
		}
		private async void ToggleGameRebar(GameLibraryItem item)
		{
			if (rebarChangeBusy || item == null || item.Profile == null)
			{
				return;
			}
			string id = item.Profile.Id;
			GameProfile current = FindLibraryProfile(id);
			if (current == null)
			{
				RefreshGames();
				return;
			}
			bool enable = !PolicyResolver.For(current).NvRebar;
			string targetPath = current.ExecutablePath;
			GameConfigurationDialog initiator = libraryConfigurationDialog;
			rebarChangeBusy = true;
			try
			{
				if (enable)
				{
					string blocked = await Task.Run((Func<string>)RebarProbe.NvidiaEnableBlockReason);
					if (base.IsDisposed || base.Disposing || (initiator != null && (initiator.IsDisposed || libraryConfigurationDialog != initiator || !initiator.Visible)))
					{
						return;
					}
					current = FindLibraryProfile(id);
					if (current == null || !string.Equals(current.ExecutablePath, targetPath, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
					if (blocked != null)
					{
						PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.rebar"), Lang.T(blocked));
						return;
					}
					if (!PaviseDialog.Confirm(LibraryDialogOwner, Lang.T("lib.rebar"), Lang.T("lib.rebar.warn"), DlgKind.Warn) || base.IsDisposed || base.Disposing)
					{
						return;
					}
					current = FindLibraryProfile(id);
					if (current == null || !string.Equals(current.ExecutablePath, targetPath, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
				}
				if (!gameMode.SetProfileOverride(id, "GameNvRebarV1", enable ? "1" : "0") && !gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.rebar"), Lang.T("lib.family.save.failed"));
				}
			}
			finally
			{
				rebarChangeBusy = false;
				if (!base.IsDisposed && !base.Disposing)
				{
					RefreshGames();
					SelectProfile(id);
				}
			}
		}
		private void ToggleGameFamilySuppression(GameLibraryItem item)
		{
			if (familyChangeBusy || item == null || item.Profile == null)
			{
				return;
			}
			familyChangeBusy = true;
			string id = item.Profile.Id;
			try
			{
				GameProfile gameProfile = FindLibraryProfile(id);
				if (gameProfile == null)
				{
					return;
				}
				bool isNotSuppressFamilyBackground = !gameProfile.SuppressFamilyBackground;
				string executablePath = gameProfile.ExecutablePath;
				if (isNotSuppressFamilyBackground && !item.RendererObserved)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.family.suppress"), Lang.T("lib.family.gated.body"));
					return;
				}
				if (isNotSuppressFamilyBackground)
				{
					using (FamilySuppressionDialog familySuppressionDialog = new FamilySuppressionDialog(gameProfile.Name, executablePath))
					{
						if (familySuppressionDialog.ShowDialog(LibraryDialogOwner) != DialogResult.OK)
						{
							return;
						}
					}
					GameProfile gameProfile2 = FindLibraryProfile(id);
					if (gameProfile2 == null)
					{
						return;
					}
					if (!string.Equals(gameProfile2.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
					{
						PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.family.suppress"), Lang.T("lib.family.changed"));
						return;
					}
				}
				if (!gameMode.SetProfileFamilySuppression(id, isNotSuppressFamilyBackground, executablePath) && !gameMode.ProfileStoreSaveFailed)
				{
					PaviseDialog.Warn(LibraryDialogOwner, Lang.T("lib.family.suppress"), Lang.T("lib.family.save.failed"));
				}
			}
			finally
			{
				familyChangeBusy = false;
				RefreshGames();
				SelectProfile(id);
			}
		}
		private GameProfile FindLibraryProfile(string id)
		{
			foreach (GameProfile profile in gameMode.GetProfiles())
			{
				if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}
		private ContextMenuStrip BuildLibraryMoreMenu(GameLibraryItem selected)
		{
			string id = selected.Profile.Id;
			ContextMenuStrip contextMenuStrip = new ContextMenuStrip();
			contextMenuStrip.Renderer = new TechMenuRenderer();
			contextMenuStrip.BackColor = Theme.Card;
			contextMenuStrip.ForeColor = Theme.Fg;
			contextMenuStrip.Font = Theme.UI(9f, false);
			contextMenuStrip.ShowImageMargin = false;
			contextMenuStrip.ShowCheckMargin = false;
			contextMenuStrip.AutoSize = false;
			contextMenuStrip.Padding = new Padding(Theme.S(6));
			contextMenuStrip.DropShadowEnabled = false;
			ContextMenuStrip contextMenuStrip2 = contextMenuStrip;
			Action<Action> invoke = delegate(Action action)
			{
				if (FindLibraryProfile(id) != null)
				{
					action();
				}
			};
			contextMenuStrip2.Items.Add(Lang.T("lib.rename"), null, delegate
			{
				invoke(delegate
				{
					RenameGame(id);
				});
			});
			contextMenuStrip2.Items.Add(AutoText("更换封面…", "Change cover…"), null, delegate
			{
				invoke(delegate
				{
					ChooseLibraryCover(id);
				});
			});
			ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem(AutoText("恢复默认封面", "Restore default cover"));
			toolStripMenuItem.Enabled = GameCoverStore.HasCustom(selected.Profile);
			ToolStripMenuItem toolStripMenuItem2 = toolStripMenuItem;
			toolStripMenuItem2.Click += delegate
			{
				invoke(delegate
				{
					ResetLibraryCover(id);
				});
			};
			contextMenuStrip2.Items.Add(toolStripMenuItem2);
			ToolStripMenuItem toolStripMenuItem3 = new ToolStripMenuItem(Lang.T(selected.Profile.ForceTrigger ? "v15.library.force.on" : "v15.library.force"));
			toolStripMenuItem3.Checked = selected.Profile.ForceTrigger;
			if (toolStripMenuItem3.Checked)
			{
				toolStripMenuItem3.Tag = new MenuBadge(PerformancePolicy.Text("已启用", "Enabled"), true);
			}
			toolStripMenuItem3.Click += delegate
			{
				invoke(delegate
				{
					ToggleForceTrigger(id);
				});
			};
			contextMenuStrip2.Items.Add(toolStripMenuItem3);
			contextMenuStrip2.Items.Add(new ToolStripSeparator());
			ToolStripMenuItem toolStripMenuItem4 = new ToolStripMenuItem(Lang.T("btn.remove"));
			toolStripMenuItem4.ForeColor = Theme.Danger;
			ToolStripMenuItem toolStripMenuItem5 = toolStripMenuItem4;
			toolStripMenuItem5.Click += delegate
			{
				invoke(delegate
				{
					RemoveLibraryGame(id);
				});
			};
			contextMenuStrip2.Items.Add(toolStripMenuItem5);
			int vertical = contextMenuStrip2.Padding.Vertical;
			int scaledDimension = Theme.S(230);
			foreach (ToolStripItem item in contextMenuStrip2.Items)
			{
				item.AutoSize = false;
				item.Margin = Padding.Empty;
				item.Size = new Size(scaledDimension - contextMenuStrip2.Padding.Horizontal, Theme.S((item is ToolStripSeparator) ? 9 : 36));
				vertical += item.Height;
			}
			contextMenuStrip2.Size = new Size(scaledDimension, vertical);
			contextMenuStrip2.PerformLayout();
			using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, scaledDimension, vertical), Theme.S(8)))
			{
				contextMenuStrip2.Region = new Region(path);
				return contextMenuStrip2;
			}
		}
		private void RenameGame(string keepId)
		{
			GameProfile gameProfile = FindLibraryProfile(keepId);
			if (gameProfile != null)
			{
				string promptResult = PaviseDialog.Prompt(this, Lang.T("lib.rename"), Lang.F("lib.rename.sub", gameProfile.Name), gameProfile.Name);
				if (!string.IsNullOrEmpty(promptResult) && !(promptResult == gameProfile.Name))
				{
					gameMode.RenameProfile(keepId, promptResult);
					RefreshGames();
					SelectProfile(keepId);
				}
			}
		}
		private void SelectProfile(string profileId)
		{
			if (lstGames == null || string.IsNullOrEmpty(profileId))
			{
				return;
			}
			for (int i = 0; i < lstGames.Items.Count; i++)
			{
				GameLibraryItem gameLibraryItem = lstGames.Items[i];
				if (gameLibraryItem != null && gameLibraryItem.Profile != null && string.Equals(gameLibraryItem.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
				{
					lstGames.SelectedIndex = i;
					break;
				}
			}
		}
		private void ReleaseGameRunningRefresh()
		{
			Interlocked.Exchange(ref nextRunningProbeTicks, 0L);
			Interlocked.Exchange(ref runningBusy, 0);
		}
		private void FinishGameRunningRefresh()
		{
			Interlocked.Exchange(ref runningBusy, 0);
			if (Interlocked.Exchange(ref runningPending, 0) != 0 && !base.IsDisposed && UiActive && curPage == pageLibrary)
			{
				RefreshGameRunningStates(true);
			}
		}
		private void ApplyRunningStates(Dictionary<string, bool> states)
		{
			bool applyRunningStatesReady = false;
			foreach (GameLibraryItem item in lstGames.Items)
			{
				GameLibraryItem gameLibraryItem = item as GameLibraryItem;
				if (gameLibraryItem != null && gameLibraryItem.Profile != null)
				{
					string executablePath = gameLibraryItem.Profile.ExecutablePath;
					bool value;
					if (!string.IsNullOrEmpty(executablePath) && states.TryGetValue(executablePath, out value) && value != gameLibraryItem.Running)
					{
						gameLibraryItem.Running = value;
						applyRunningStatesReady = true;
					}
				}
			}
			if (applyRunningStatesReady)
			{
				lstGames.RefreshRows();
				RefreshLibraryInspector();
			}
		}
		private void AddDroppedGames(string[] files)
		{
			if (files == null)
			{
				return;
			}
			List<string> list = new List<string>();
			string error = null;
			foreach (string filePath in files)
			{
				if (!string.IsNullOrEmpty(filePath))
				{
					if (Directory.Exists(filePath))
					{
						list.Add(filePath);
					}
					else if (!gameMode.AddGameFile(filePath, out error) && error != Lang.T("t.gamemodelibrary.1"))
					{
						break;
					}
				}
			}
			if (!string.IsNullOrEmpty(error) && error != Lang.T("t.gamemodelibrary.1"))
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", error);
			}
			RefreshGames();
			if (list.Count > 0)
			{
				ShowAddGameDialog(list);
			}
		}
		private static bool RunningIn(Dictionary<string, bool> states, string executablePath)
		{
			bool value;
			if (!string.IsNullOrEmpty(executablePath) && states.TryGetValue(executablePath, out value))
			{
				return value;
			}
			return false;
		}
		private static Dictionary<string, bool> ProbeRunning(List<string> paths)
		{
			Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, List<string>> dictionary2 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (string path in paths)
			{
				if (string.IsNullOrEmpty(path) || dictionary.ContainsKey(path))
				{
					continue;
				}
				dictionary[path] = false;
				string fileNameWithoutExtension;
				try
				{
					fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
				}
				catch
				{
					continue;
				}
				if (!string.IsNullOrEmpty(fileNameWithoutExtension))
				{
					List<string> value;
					if (!dictionary2.TryGetValue(fileNameWithoutExtension, out value))
					{
						value = (dictionary2[fileNameWithoutExtension] = new List<string>());
					}
					value.Add(path);
				}
			}
			if (dictionary2.Count == 0)
			{
				return dictionary;
			}
			Process[] runningProcesses = null;
			try
			{
				runningProcesses = Process.GetProcesses();
				Process[] processItems = runningProcesses;
				foreach (Process process in processItems)
				{
					List<string> value2 = null;
					int probeRunningValue = 0;
					try
					{
						dictionary2.TryGetValue(process.ProcessName, out value2);
						probeRunningValue = process.Id;
					}
					catch
					{
						continue;
					}
					if (value2 == null)
					{
						continue;
					}
					IntPtr intPtr = Native.OpenProcess(4096, false, probeRunningValue);
					if (intPtr == IntPtr.Zero)
					{
						continue;
					}
					string imagePathResult;
					try
					{
						imagePathResult = Native.ImagePath(intPtr);
					}
					finally
					{
						Native.CloseHandle(intPtr);
					}
					if (string.IsNullOrEmpty(imagePathResult))
					{
						continue;
					}
					foreach (string item in value2)
					{
						if (string.Equals(item, imagePathResult, StringComparison.OrdinalIgnoreCase))
						{
							dictionary[item] = true;
						}
					}
				}
			}
			catch
			{
			}
			finally
			{
				if (runningProcesses != null)
				{
					Process[] processItems2 = runningProcesses;
					foreach (Process process2 in processItems2)
					{
						process2.Dispose();
					}
				}
			}
			return dictionary;
		}
		private Bitmap GameIcon(string executablePath)
		{
			string key = executablePath ?? "";
			Bitmap value;
			if (gameIconCache.TryGetValue(key, out value))
			{
				return value;
			}
			try
			{
				using (Icon icon = Icon.ExtractAssociatedIcon(executablePath))
				{
					value = icon.ToBitmap();
				}
			}
			catch
			{
				value = null;
			}
			gameIconCache[key] = value;
			return value;
		}
		private void BuildLibraryPage()
		{
			libraryViewport = Size.Empty;
			gameListPanel = null;
			lblLibraryHint = null;
			pageLibrary.AutoScroll = false;
			Native.Dark(pageLibrary);
			librarySearchPanel = new LibrarySearchSurface
			{
				Name = "librarySearchPanel",
				Fill = Theme.Card,
				Border = Theme.StrokeHi,
				Radius = Theme.S(10),
				BackColor = Theme.Bg
			};
			librarySearch = Theme.MakeTextBox(0, 0, Theme.S(220));
			librarySearch.Name = "librarySearch";
			librarySearch.BorderStyle = BorderStyle.None;
			librarySearch.BackColor = Theme.Card;
			librarySearch.Text = librarySearchText;
			librarySearch.AccessibleName = AutoText("搜索游戏", "Search games");
			librarySearch.HandleCreated += delegate
			{
				SendLibraryCue(librarySearch.Handle, 5377, new IntPtr(1), librarySearch.AccessibleName);
			};
			librarySearch.TextChanged += delegate
			{
				librarySearchText = librarySearch.Text;
				if (lstGames != null)
				{
					lstGames.FilterText = librarySearchText;
					AdjustLibraryCardDensity();
				}
			};
			librarySearchPanel.Controls.Add(librarySearch);
			pageLibrary.Controls.Add(librarySearchPanel);
			libraryPlatforms = new TierPicker
			{
				Name = "libraryPlatformFilter",
				Labels = new string[3]
				{
					AutoText("全部", "All"),
					"Steam",
					"WeGame"
				},
				StrongSelection = true,
				Index = libraryPlatformIndex,
				AccessibleName = AutoText("游戏平台筛选", "Filter by platform")
			};
			libraryPlatforms.IndexChanged = delegate(int index)
			{
				libraryPlatformIndex = index;
				ApplyLibraryFilter();
			};
			libraryAdd = new PillButton(Lang.T("v15.library.add"), BtnKind.Primary)
			{
				Name = "libraryAdd"
			};
			libraryAdd.Click += delegate
			{
				ShowAddGameDialog();
			};
			swAutoAdd = new FamilySuppressionSwitch
			{
				Name = "libraryAutoAdd",
				Text = Lang.T("v15.library.autoadd"),
				Checked = gameMode.AutoAddFullscreen,
				AccessibleName = Lang.T("v15.library.autoadd"),
				AccessibleDescription = Lang.T("v15.library.autoadd.desc")
			};
			swAutoAdd.Click += delegate
			{
				gameMode.AutoAddFullscreen = !gameMode.AutoAddFullscreen;
				swAutoAdd.Checked = gameMode.AutoAddFullscreen;
			};
			libraryAutoSettings = new SettingsButton(Lang.T("v15.library.autoadd.settings"), Lang.T("autoignore.title"))
			{
				Name = "libraryAutoIgnore"
			};
			libraryAutoSettings.Click += delegate
			{
				ShowAutoIgnoreDialog();
			};
			lblLibraryCount = CardLabel(pageLibrary, "", 0, 0, 120, 28, 8.2f, false, Theme.Dim);
			pageLibrary.Controls.AddRange(new Control[4] { libraryPlatforms, libraryAdd, swAutoAdd, libraryAutoSettings });
			lstGames = new GameLibraryGallery
			{
				Name = "LibraryGallery",
				IconProvider = GameIcon,
				CoverProvider = GameCoverStore.Load,
				ConfigurationPresenter = OpenLibraryConfiguration
			};
			lstGames.FilterText = librarySearchText;
			lstGames.SelectedIndexChanged += delegate
			{
				GameLibraryItem selectedItem = lstGames.SelectedItem;
				librarySelectedId = ((selectedItem == null) ? null : selectedItem.Profile.Id);
			};
			lstGames.MoreRequested += delegate(object sender, GameLibraryEventArgs e)
			{
				ShowLibraryMore(e.Item, e.Anchor);
			};
			lstGames.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None && lstGames.SelectedItem != null)
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
					RemoveLibraryGame(lstGames.SelectedItem.Profile.Id);
				}
			};
			pageLibrary.Controls.Add(lstGames);
			pageLibrary.SizeChanged += delegate
			{
				libraryViewport = Size.Empty;
				LayoutLibraryViewport();
			};
			ApplyLibraryFilter();
			RefreshGames();
			if (!string.IsNullOrEmpty(librarySelectedId))
			{
				SelectProfile(librarySelectedId);
			}
			LayoutLibraryViewport();
		}
		private void ApplyLibraryFilter()
		{
			if (lstGames != null)
			{
				int platform = libraryPlatformIndex;
				lstGames.Filter = ((platform == 0) ? null : ((Predicate<GameLibraryItem>)((GameLibraryItem item) => item != null && string.Equals(GameLibraryGallery.SourceOf(item.Profile), (platform == 1) ? "Steam" : "WeGame", StringComparison.OrdinalIgnoreCase))));
				lstGames.FilterText = librarySearchText;
				AdjustLibraryCardDensity();
			}
		}
		private void AdjustLibraryCardDensity()
		{
			for (int i = 0; i < 2; i++)
			{
				int columnCountRatio = (lstGames.VisibleItemCount + lstGames.ColumnCount - 1) / lstGames.ColumnCount;
				int uResult = Dpi.U(lstGames.ClientSize.Height);
				int val = ((columnCountRatio > 0 && columnCountRatio <= 3) ? ((uResult - 4 - Math.Max(0, columnCountRatio - 1) * lstGames.CardGap) / columnCountRatio - 32) : 170);
				lstGames.PreferredCardHeight = Math.Max(128, Math.Min(170, val));
			}
		}
		private void RemoveLibraryGame(string id)
		{
			gameMode.RemoveProfile(id);
			RefreshGames();
		}
		private void ChooseLibraryCover(string id)
		{
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile == null)
			{
				return;
			}
			OpenFileDialog openFileDialog = new OpenFileDialog();
			openFileDialog.Title = AutoText("更换游戏封面", "Change game cover");
			openFileDialog.Filter = AutoText("图片", "Images") + "|*.jpg;*.jpeg;*.png;*.bmp";
			openFileDialog.CheckFileExists = true;
			openFileDialog.Multiselect = false;
			using (OpenFileDialog openFileDialog2 = openFileDialog)
			{
				if (openFileDialog2.ShowDialog(this) == DialogResult.OK)
				{
					string error;
					if (!GameCoverStore.TrySet(gameProfile, openFileDialog2.FileName, out error))
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", error);
					}
					else
					{
						lstGames.RefreshRows();
					}
				}
			}
		}
		private void ResetLibraryCover(string id)
		{
			GameProfile gameProfile = FindLibraryProfile(id);
			if (gameProfile != null)
			{
				string error;
				if (!GameCoverStore.TryReset(gameProfile, out error))
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", error);
				}
				else
				{
					lstGames.RefreshRows();
				}
			}
		}
		private void BuildWhitelistPage()
		{
			Interlocked.Increment(ref whiteVersion);
			pageWhitelist.AutoScroll = false;
			Native.Dark(pageWhitelist);
			whiteSearchPanel = new WhitelistSearchSurface
			{
				Name = "WhitelistSearchSurface",
				Fill = Theme.Card,
				Border = Theme.StrokeHi,
				Radius = Theme.S(10),
				BackColor = Theme.Bg
			};
			whiteSearch = Theme.MakeTextBox(0, 0, Theme.S(200));
			whiteSearch.Name = "WhitelistSearch";
			whiteSearch.BorderStyle = BorderStyle.None;
			whiteSearch.BackColor = Theme.Card;
			whiteSearch.Text = whiteSearchText;
			whiteSearch.AccessibleName = AutoText("搜索应用或路径", "Search apps or paths");
			whiteSearch.HandleCreated += delegate
			{
				SendWhitelistCue(whiteSearch.Handle, 5377, new IntPtr(1), AutoText("搜索应用或路径", "Search apps or paths"));
			};
			whiteSearch.TextChanged += delegate
			{
				whiteSearchText = whiteSearch.Text;
				FilterWhitelistRows();
			};
			whiteSearchPanel.Controls.Add(whiteSearch);
			whiteFilters = new TierPicker
			{
				Name = "WhitelistFilter",
				StrongSelection = true,
				Labels = new string[4]
				{
					AutoText("全部", "All"),
					AutoText("手动", "Manual"),
					AutoText("平台", "Platforms"),
					AutoText("内置", "Built-in")
				},
				Index = whiteFilterIndex,
				AccessibleName = AutoText("白名单来源筛选", "Filter whitelist source")
			};
			whiteFilters.IndexChanged = delegate(int index)
			{
				whiteFilterIndex = index;
				FilterWhitelistRows();
			};
			whiteCount = new Label
			{
				Name = "WhitelistCount",
				BackColor = Theme.Bg,
				ForeColor = Theme.Dim,
				Font = Theme.Mono(8f),
				TextAlign = ContentAlignment.MiddleLeft,
				AutoEllipsis = true
			};
			whitePick = new PillButton(AutoText("添加进程", "Add process"), BtnKind.Primary)
			{
				Name = "whitelistPick"
			};
			whitePick.Click += delegate
			{
				PickRunningForWhitelist();
			};
			whiteBrowse = new PillButton(AutoText("添加 EXE", "Add EXE"))
			{
				Name = "whitelistBrowse"
			};
			whiteBrowse.Click += delegate
			{
				BrowseForWhitelist();
			};
			whitePanel = new EmptyStatePanel
			{
				Name = "WhitelistSurface",
				BackColor = Theme.Bg,
				Fill = Theme.Card,
				Border = Theme.Stroke,
				Radius = Theme.S(12),
				Padding = new Padding(Theme.S(8)),
				EmptyGlyph = "white"
			};
			whitePanel.EmptyTitle = AutoText("没有匹配的应用", "No matching apps");
			whitePanel.EmptyDetail = AutoText("调整筛选，或添加需要保护的程序。", "Change the filter or add an app to protect.");
			lstWhite = new TechListBox
			{
				Name = "WhitelistRules",
				Dock = DockStyle.Fill
			};
			Theme.StyleList(lstWhite, false);
			lstWhite.AccessibleName = Lang.T("nav.white");
			lstWhite.ItemHeight = Math.Min(255, Theme.S(56));
			lstWhite.DrawItem += DrawWhitelistItem;
			lstWhite.SelectedIndexChanged += delegate
			{
				SyncWhitelistRemoveAction();
			};
			lstWhite.KeyDown += delegate(object sender, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None)
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
					RemoveSelectedWhitelist();
				}
				else if (e.KeyCode == Keys.Return && ShowSelectedAutomaticExemption())
				{
					e.Handled = true;
					e.SuppressKeyPress = true;
				}
			};
			lstWhite.MouseClick += delegate(object sender, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Left)
				{
					int selectedIndexValue = lstWhite.IndexFromPoint(e.Location);
					if (selectedIndexValue >= 0)
					{
						lstWhite.SelectedIndex = selectedIndexValue;
						ShowSelectedAutomaticExemption();
					}
				}
			};
			lstWhite.MouseDown += delegate(object sender, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Right)
				{
					lstWhite.SelectedIndex = lstWhite.IndexFromPoint(e.Location);
				}
			};
			whiteMenu = new ContextMenuStrip
			{
				Font = Theme.UI(9f, false),
				BackColor = Theme.Card,
				ForeColor = Theme.Fg,
				ShowImageMargin = false,
				DropShadowEnabled = false,
				Renderer = new TechMenuRenderer()
			};
			whiteMenu.Opening += OnWhiteMenuOpening;
			lstWhite.ContextMenuStrip = whiteMenu;
			ContextMenuStrip ownedMenu = whiteMenu;
			whitePanel.Disposed += delegate
			{
				ownedMenu.Dispose();
			};
			whitePanel.Controls.Add(lstWhite);
			Control[] controlItems = new Control[3] { pageWhitelist, whitePanel, lstWhite };
			foreach (Control control in controlItems)
			{
				control.AllowDrop = true;
				control.DragEnter += OnWhiteDragEnter;
				control.DragDrop += OnWhiteDragDrop;
			}
			Native.Dark(whitePanel);
			btnRemoveWhitelist = new PillButton(Lang.T("btn.remove"))
			{
				Name = "whitelistRemove",
				Enabled = false
			};
			btnRemoveWhitelist.Click += delegate
			{
				RemoveSelectedWhitelist();
			};
			btnRestorePlatformProtection = new PillButton(Lang.T("white.platform.restore"))
			{
				Name = "restorePlatformProtection"
			};
			btnRestorePlatformProtection.AccessibleName = btnRestorePlatformProtection.Text;
			btnRestorePlatformProtection.AccessibleDescription = Lang.T("white.platform.hint");
			btnRestorePlatformProtection.Click += delegate
			{
				RunWhitelistOp(() => (!gameMode.RestorePlatformProtection()) ? gameMode.WhitelistLastError : null);
			};
			whiteReset = new PillButton(AutoText("恢复预设", "Restore preset"))
			{
				Name = "whitelistRestorePreset"
			};
			whiteReset.Click += delegate
			{
				if (PaviseDialog.Confirm(this, "PAVISE AUTO", Lang.T("white.reset.confirm"), DlgKind.Warn))
				{
					RunWhitelistOp(() => (!gameMode.ResetWhitelist()) ? gameMode.WhitelistLastError : null);
				}
			};
			lblWhiteHint = new Label
			{
				Name = "WhitelistSelectionHint",
				BackColor = Theme.Bg,
				ForeColor = Theme.Faint,
				Font = Theme.UI(8f, false),
				AutoEllipsis = true,
				TextAlign = ContentAlignment.MiddleLeft
			};
			pageWhitelist.Controls.AddRange(new Control[10] { whiteSearchPanel, whiteFilters, whiteCount, whitePick, whiteBrowse, whitePanel, btnRemoveWhitelist, btnRestorePlatformProtection, whiteReset, lblWhiteHint });
			pageWhitelist.SizeChanged += delegate
			{
				LayoutWhitelistViewport();
			};
			RefreshWhitelist(false);
			SyncWhitelistPlatformHint();
			LayoutWhitelistViewport();
		}
	}
}
