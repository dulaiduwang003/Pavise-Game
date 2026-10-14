// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private GameLibraryList lstGames;
        private Toggle swAutoAdd;
        private Label lblLibraryHint;
        private Label lblLibraryCount;
        private bool familyChangeBusy;
        private bool rebarChangeBusy;
        private bool fullscreenOptimizationsChangeBusy;
        private EmptyStatePanel gameListPanel;
        private readonly Dictionary<string, Bitmap> gameIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private int runningBusy, runningPending, runningVersion;
        private long nextRunningProbeTicks;
        private static readonly long RunningProbeIntervalTicks = TimeSpan.FromSeconds(5).Ticks;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal Func<List<string>, Dictionary<string, bool>> RunningQueryForTest;
#endif

        private Dictionary<string, bool> QueryLibraryRunningState(List<string> paths)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (RunningQueryForTest == null) throw new InvalidOperationException("Library running state was not mocked");
            return RunningQueryForTest(paths);
#else
            return ProbeRunning(paths);
#endif
        }

        private void BuildLibraryPage()
        {
            int y = PageHeader(pageLibrary, Lang.T("nav.library"), Lang.T("v15.library.sub"), 2);
            int listH = PageH - y - 16;
            int listW = ContentW - 238;
            var listWrap = new EmptyStatePanel();
            gameListPanel = listWrap;
            listWrap.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(listW), Theme.S(listH));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(14);
            listWrap.EmptyTitle = "PAVISE LIBRARY";
            listWrap.EmptyDetail = Lang.T("v15.library.empty");
            listWrap.Padding = new Padding(Theme.S(10), Theme.S(44), Theme.S(10), Theme.S(10));
            lblLibraryCount = new Label();
            lblLibraryCount.Font = Theme.Mono(7.3f); lblLibraryCount.ForeColor = Theme.Dim;
            lblLibraryCount.BackColor = Color.Transparent;
            lblLibraryCount.SetBounds(Theme.S(16), Theme.S(13), Theme.S(220), Theme.S(19));
            var listHint = new Label();
            listHint.Text = Lang.T("lib.family.list.hint"); listHint.Font = Theme.UI(7.8f, false);
            listHint.ForeColor = Theme.Dim; listHint.BackColor = Color.Transparent;
            listHint.TextAlign = ContentAlignment.MiddleRight;
            listHint.SetBounds(Theme.S(listW - 282), Theme.S(10), Theme.S(264), Theme.S(23));
            lstGames = new GameLibraryList(); lstGames.Dock = DockStyle.Fill; lstGames.IconProvider = GameIcon;
            lstGames.KeyDown += delegate(object s, KeyEventArgs e)
            {
                GameLibraryItem item = lstGames.SelectedItem as GameLibraryItem;
                if (e.KeyCode == Keys.Delete && item != null)
                {
                    e.Handled = true; e.SuppressKeyPress = true;
                    gameMode.RemoveProfile(item.Profile.Id); RefreshGames();
                }
            };
            lstGames.CoreToggleRequested += delegate(object sender, GameLibraryEventArgs e) { ToggleGameCoreScheduling(e.Item); };
            lstGames.RebarToggleRequested += delegate(object sender, GameLibraryEventArgs e) { ToggleGameRebar(e.Item); };
            lstGames.FullscreenOptimizationsToggleRequested += delegate(object sender, GameLibraryEventArgs e) { ToggleGameFullscreenOptimizations(e.Item); };
            lstGames.CoreSchedulingRequested += delegate(object sender, GameLibraryEventArgs e) { OpenGameCoreScheduling(e.Item.Profile.Id); };
            lstGames.FamilyToggleRequested += delegate(object sender, GameLibraryEventArgs e) { ToggleGameFamilySuppression(e.Item); };
            lstGames.MoreRequested += delegate(object sender, GameLibraryEventArgs e) { ShowLibraryMore(e.Item, e.Anchor); };
            listWrap.Controls.AddRange(new Control[] { lstGames, lblLibraryCount, listHint });
            int bx = ContentX + listW + 16, sideW = ContentW - listW - 16;
            int scrollSpace = Math.Max(20, (int)Math.Ceiling((SystemInformation.VerticalScrollBarWidth + 2) / Dpi.Scale));
            int bw = sideW - scrollSpace, bh = 40;
            var libraryActions = new WorkspacePanel { Name = "libraryActions", AutoScroll = true, BackColor = Theme.Bg };
            libraryActions.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(sideW), Theme.S(listH));
            Native.Dark(libraryActions);
            var add = new PillButton(Lang.T("v15.library.add"), BtnKind.Primary) { TabIndex = 0 };
            add.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(bw), Theme.S(bh));
            add.Click += delegate { ShowAddGameDialog(); };
            var protect = new RogLinkButton(Lang.T("nav.white"), "GAME // WHITELIST", "white")
                { Name = "libraryWhitelist", External = false, TabIndex = 1 };
            protect.SetBounds(Theme.S(bx), Theme.S(y + 54), Theme.S(bw), Theme.S(48));
            protect.Click += delegate { nav.Select((int)PageId.Whitelist); };
            var lblAutoAdd = new Label();
            lblAutoAdd.Name = "libraryAutoAddTitle";
            lblAutoAdd.Text = Lang.T("v15.library.autoadd");
            lblAutoAdd.BackColor = Theme.Bg; lblAutoAdd.ForeColor = Theme.Fg;
            lblAutoAdd.Font = Theme.UI(9f, true);
            lblAutoAdd.AutoEllipsis = true;
            int autoAddTitleWidth = Math.Min(Theme.S(bw - 100),
                TextRenderer.MeasureText(lblAutoAdd.Text, lblAutoAdd.Font).Width);
            lblAutoAdd.SetBounds(Theme.S(bx + 4), Theme.S(y + 116), autoAddTitleWidth, Theme.S(22));
            var autoAddSettings = new SettingsButton(Lang.T("v15.library.autoadd.settings"), Lang.T("autoignore.title"))
                { Name = "libraryAutoIgnore", TabIndex = 3 };
            autoAddSettings.SetBounds(Theme.S(bx + bw - 32), Theme.S(y + 113), Theme.S(32), Theme.S(28));
            autoAddSettings.Click += delegate { ShowAutoIgnoreDialog(); };
            swAutoAdd = MakeSwitch(gameMode.AutoAddFullscreen,
                delegate { gameMode.AutoAddFullscreen = swAutoAdd.Checked; });
            swAutoAdd.Name = "libraryAutoAdd";
            swAutoAdd.TabIndex = 2;
            swAutoAdd.AccessibleName = lblAutoAdd.Text;
            swAutoAdd.AccessibleDescription = Lang.T("v15.library.autoadd.desc");
            swAutoAdd.Bg = Theme.Bg;
            swAutoAdd.Location = new Point(autoAddSettings.Left - Theme.S(8) - swAutoAdd.Width, Theme.S(y + 114));
            var lblAutoAddDesc = new Label();
            lblAutoAddDesc.Name = "libraryAutoAddDescription";
            lblAutoAddDesc.Text = Lang.T("v15.library.autoadd.desc");
            lblAutoAddDesc.BackColor = Theme.Bg; lblAutoAddDesc.ForeColor = Theme.Dim;
            lblAutoAddDesc.Font = Theme.UI(8.2f, false);
            int autoAddDescHeight = TextRenderer.MeasureText(lblAutoAddDesc.Text, lblAutoAddDesc.Font,
                new Size(Theme.S(bw - 8), int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(4);
            lblAutoAddDesc.SetBounds(Theme.S(bx + 4), Theme.S(y + 144), Theme.S(bw - 8), autoAddDescHeight);

            var familyGuide = new RoundPanel();
            familyGuide.SetBounds(Theme.S(bx), lblAutoAddDesc.Bottom + Theme.S(14), Theme.S(bw), Theme.S(122));
            familyGuide.Fill = Theme.Card; familyGuide.Border = Theme.Stroke; familyGuide.AccentEdge = true;
            var familyGuideTitle = new Label();
            familyGuideTitle.Text = Lang.T("lib.family.guide.title"); familyGuideTitle.Font = Theme.UI(8.8f, true);
            familyGuideTitle.BackColor = Color.Transparent; familyGuideTitle.ForeColor = Theme.Fg;
            int guideTitleHeight = TextRenderer.MeasureText(familyGuideTitle.Text, familyGuideTitle.Font,
                new Size(Theme.S(bw - 28), int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(4);
            familyGuideTitle.SetBounds(Theme.S(14), Theme.S(13), Theme.S(bw - 28), guideTitleHeight);
            var familyGuideBody = new Label();
            familyGuideBody.Text = Lang.T("lib.family.guide.body"); familyGuideBody.Font = Theme.UI(8.0f, false);
            familyGuideBody.BackColor = Color.Transparent; familyGuideBody.ForeColor = Theme.Dim;
            int guideBodyHeight = TextRenderer.MeasureText(familyGuideBody.Text, familyGuideBody.Font,
                new Size(Theme.S(bw - 28), int.MaxValue), TextFormatFlags.WordBreak).Height + Theme.S(4);
            familyGuideBody.SetBounds(Theme.S(14), familyGuideTitle.Bottom + Theme.S(6), Theme.S(bw - 28), guideBodyHeight);
            familyGuide.Height = familyGuideBody.Bottom + Theme.S(14);
            familyGuide.Controls.AddRange(new Control[] { familyGuideTitle, familyGuideBody });
            lblLibraryHint = new Label(); lblLibraryHint.BackColor = Theme.Bg;
            lblLibraryHint.Font = Theme.UI(7.9f, false); lblLibraryHint.AutoEllipsis = true;
            lblLibraryHint.SetBounds(Theme.S(bx + 4), familyGuide.Bottom + Theme.S(14), Theme.S(bw - 8), Theme.S(60));
            SyncLibraryHint();
            Control[] actions = { add, protect, lblAutoAdd, swAutoAdd, autoAddSettings,
                lblAutoAddDesc, familyGuide, lblLibraryHint };
            foreach (Control action in actions)
            {
                action.Location = new Point(action.Left - Theme.S(bx), action.Top - Theme.S(y));
                libraryActions.Controls.Add(action);
            }
            pageLibrary.Controls.AddRange(new Control[] { listWrap, libraryActions });
            RefreshGames();
        }

        public void NotifyLibraryChanged()
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && lstGames != null) RefreshGames();
                });
            }
            catch { }
        }

        private void ToggleGameCoreScheduling(GameLibraryItem item)
        {
            if (item == null || item.Profile == null) return;
            string id = item.Profile.Id;
            GameProfile current = FindLibraryProfile(id);
            if (current == null) { RefreshGames(); return; }
            if (PolicyResolver.For(current).ManualCores == null) { OpenGameCoreScheduling(id); return; }
            bool enable = !PolicyResolver.For(current).AutoCoresAllowed;
            if (!gameMode.SetProfileOverride(id, PolicyCatalog.KeyAutoCores, enable ? "1" : "0")
                && !gameMode.ProfileStoreSaveFailed)
                PaviseDialog.Warn(this, Lang.T("nav.corescheduling"), Lang.T("lib.family.save.failed"));
            RefreshGames(); SelectProfile(id);
        }

        private void ToggleGameFullscreenOptimizations(GameLibraryItem item)
        {
            if (fullscreenOptimizationsChangeBusy || item == null || item.Profile == null) return;
            fullscreenOptimizationsChangeBusy = true;
            string id = item.Profile.Id;
            try
            {
                GameProfile current = FindLibraryProfile(id);
                if (current == null) return;
                if (!gameMode.SetProfileFullscreenOptimizations(id, !current.DisableFullscreenOptimizations)
                    && !gameMode.ProfileStoreSaveFailed)
                    PaviseDialog.Warn(this, Lang.T("lib.fso"), Lang.T("lib.fso.failed"));
            }
            finally
            {
                fullscreenOptimizationsChangeBusy = false;
                if (!IsDisposed && !Disposing) { RefreshGames(); SelectProfile(id); }
            }
        }

        private async void ToggleGameRebar(GameLibraryItem item)
        {
            if (rebarChangeBusy || item == null || item.Profile == null) return;
            string id = item.Profile.Id;
            GameProfile current = FindLibraryProfile(id);
            if (current == null) { RefreshGames(); return; }
            bool enable = !PolicyResolver.For(current).NvRebar;
            string targetPath = current.ExecutablePath;
            rebarChangeBusy = true;
            try
            {
                if (enable)
                {
                    string blocked = await System.Threading.Tasks.Task.Run(
                        (Func<string>)RebarProbe.NvidiaEnableBlockReason);
                    if (IsDisposed || Disposing) return;
                    current = FindLibraryProfile(id);
                    if (current == null || !string.Equals(current.ExecutablePath, targetPath,
                        StringComparison.OrdinalIgnoreCase)) return;
                    if (blocked != null)
                    {
                        PaviseDialog.Warn(this, Lang.T("lib.rebar"), Lang.T(blocked));
                        return;
                    }
                    if (!PaviseDialog.Confirm(this, Lang.T("lib.rebar"),
                        Lang.T("lib.rebar.warn"), DlgKind.Warn)) return;
                    // 弹窗期间配置可能变更，确认只适用于原选定的可执行文件。
                    if (IsDisposed || Disposing) return;
                    current = FindLibraryProfile(id);
                    if (current == null || !string.Equals(current.ExecutablePath, targetPath,
                        StringComparison.OrdinalIgnoreCase)) return;
                }
                if (!gameMode.SetProfileOverride(id, PolicyCatalog.KeyGameRebar, enable ? "1" : "0")
                    && !gameMode.ProfileStoreSaveFailed)
                    PaviseDialog.Warn(this, Lang.T("lib.rebar"), Lang.T("lib.family.save.failed"));
            }
            finally
            {
                rebarChangeBusy = false;
                if (!IsDisposed && !Disposing) { RefreshGames(); SelectProfile(id); }
            }
        }

        private void ToggleGameFamilySuppression(GameLibraryItem item)
        {
            if (familyChangeBusy || item == null || item.Profile == null) return;
            familyChangeBusy = true;
            string keepId = item.Profile.Id;
            try
            {
                GameProfile current = FindLibraryProfile(keepId);
                if (current == null) return;
                bool turningOn = !current.SuppressFamilyBackground;
                string targetPath = current.ExecutablePath;
                if (turningOn && !item.RendererObserved)
                {
                    PaviseDialog.Warn(this, Lang.T("lib.family.suppress"), Lang.T("lib.family.gated.body"));
                    return;
                }
                if (turningOn)
                {
                    using (var warning = new FamilySuppressionDialog(current.Name, targetPath))
                        if (ShowDim(warning) != DialogResult.OK) return;
                    GameProfile after = FindLibraryProfile(keepId);
                    if (after == null) return;
                    if (!string.Equals(after.ExecutablePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        PaviseDialog.Warn(this, Lang.T("lib.family.suppress"), Lang.T("lib.family.changed"));
                        return;
                    }
                }
                if (!gameMode.SetProfileFamilySuppression(keepId, turningOn, targetPath) && !gameMode.ProfileStoreSaveFailed)
                    PaviseDialog.Warn(this, Lang.T("lib.family.suppress"), Lang.T("lib.family.save.failed"));
            }
            finally
            {
                familyChangeBusy = false;
                // 取消或保存失败后回显真实配置。
                RefreshGames(); SelectProfile(keepId);
            }
        }

        private GameProfile FindLibraryProfile(string id)
        {
            foreach (GameProfile profile in gameMode.GetProfiles())
                if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) return profile;
            return null;
        }

        private void ShowLibraryMore(GameLibraryItem selected, Control anchor)
        {
            var button = anchor as GameCardMenuButton;
            if (selected == null || selected.Profile == null || button == null || button.IsDisposed) return;
            button.ShowMenu(BuildLibraryMoreMenu(selected));
        }

        private ContextMenuStrip BuildLibraryMoreMenu(GameLibraryItem selected)
        {
            string id = selected.Profile.Id;
            var menu = new ContextMenuStrip { Renderer = new TechMenuRenderer(),
                BackColor = Theme.Card, ForeColor = Theme.Fg, Font = Theme.UI(9f,false),
                ShowImageMargin = false, ShowCheckMargin = false, AutoSize = false,
                Padding = new Padding(Theme.S(6)), DropShadowEnabled = false };
            Action<Action> invoke = delegate(Action action) {
                if (FindLibraryProfile(id) == null) return;
                action(); };
            menu.Items.Add(Lang.T("lib.rename"),null,delegate { invoke(delegate { RenameGame(id); }); });
            var force = new ToolStripMenuItem(Lang.T(selected.Profile.ForceTrigger ? "v15.library.force.on" : "v15.library.force"));
            force.Checked = selected.Profile.ForceTrigger;
            if (force.Checked) force.Tag = new MenuBadge(AutomaticPolicy.Text("已启用", "Enabled"), true);
            force.Click += delegate { invoke(delegate { ToggleForceTrigger(id); }); };
            menu.Items.Add(force);
            menu.Items.Add(new ToolStripSeparator());
            var remove = new ToolStripMenuItem(Lang.T("btn.remove")) { ForeColor = Theme.Danger };
            remove.Click += delegate { invoke(delegate { gameMode.RemoveProfile(id); RefreshGames(); }); };
            menu.Items.Add(remove);
            int height = menu.Padding.Vertical;
            int width = Theme.S(230);
            foreach (ToolStripItem item in menu.Items)
            {
                item.AutoSize = false;
                item.Margin = Padding.Empty;
                item.Size = new Size(width - menu.Padding.Horizontal,
                    Theme.S(item is ToolStripSeparator ? 9 : 36));
                height += item.Height;
            }
            menu.Size = new Size(width, height);
            menu.PerformLayout();
            using (var frame = Theme.TechPath(new Rectangle(0, 0, width, height), Theme.S(8)))
                menu.Region = new Region(frame);
            return menu;
        }

private void RenameGame(string keepId)
        {
            GameProfile profile = FindLibraryProfile(keepId);
            if (profile == null) return;
            string name = PaviseDialog.Prompt(this, Lang.T("lib.rename"),
                Lang.F("lib.rename.sub", profile.Name), profile.Name);
            if (string.IsNullOrEmpty(name) || name == profile.Name) return;
            gameMode.RenameProfile(keepId, name);
            RefreshGames();
            SelectProfile(keepId);
        }

        private void ToggleForceTrigger(string keepId)
        {
            GameProfile profile = FindLibraryProfile(keepId);
            if (profile == null) return;
            bool turningOn = !profile.ForceTrigger;
            if (turningOn && !PaviseDialog.Confirm(this, Lang.T("v15.library.force"),
                    Lang.F("v15.library.force.confirm", profile.Name), DlgKind.Warn))
                return;
            if (!gameMode.SetProfileForceTrigger(keepId, turningOn))
            {
                if (gameMode.ProfileStoreSaveFailed) return;
                PaviseDialog.Warn(this, Lang.T("v15.library.force"), Lang.T("v15.library.force.noexe"));
                return;
            }
            RefreshGames();
            SelectProfile(keepId);
        }

        private void SelectProfile(string profileId)
        {
            if (lstGames == null || string.IsNullOrEmpty(profileId)) return;
            for (int i = 0; i < lstGames.Items.Count; i++)
            {
                GameLibraryItem row = lstGames.Items[i] as GameLibraryItem;
                if (row != null && row.Profile != null
                    && string.Equals(row.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    lstGames.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ShowAddGameDialog()
        {
            ShowAddGameDialog(null);
        }

        private void ShowAddGameDialog(IEnumerable<string> seeds)
        {
            var known = new List<string>();
            foreach (GameProfile p in gameMode.GetProfiles())
            {
                if (!string.IsNullOrEmpty(p.ExecutablePath)) known.Add(p.ExecutablePath);
                if (!string.IsNullOrEmpty(p.LearnedExecutablePath)) known.Add(p.LearnedExecutablePath);
            }

            using (var dlg = new AddGameDialog(known, gameMode.ActiveGame == null, seeds))
            {
                if (ShowDim(dlg) != DialogResult.OK || dlg.Selected.Count == 0) return;
                string lastError;
                int added = gameMode.AddScannedGames(dlg.Selected, out lastError);
                if (gameMode.ProfileStoreSaveFailed) return;
                RefreshGames();
                if (added > 0) Logger.Log(Lang.F("scan.added", added));
                else if (lastError != null)
                    PaviseDialog.Warn(this, App.DisplayName, lastError);
            }
        }

        private void RefreshGames()
        {
            if (IsDisposed || lstGames == null || lstGames.IsDisposed) return;
            Interlocked.Increment(ref runningVersion);
            if (swAutoAdd != null) swAutoAdd.SetSilently(gameMode.AutoAddFullscreen);
            string keepId = null;
            GameLibraryItem sel = lstGames.SelectedItem as GameLibraryItem;
            if (sel != null && sel.Profile != null) keepId = sel.Profile.Id;
            List<GameProfile> profiles = gameMode.GetProfiles();
            var runningByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (GameLibraryItem row in lstGames.Items)
                if (row != null && row.Profile != null && !string.IsNullOrEmpty(row.Profile.ExecutablePath))
                    runningByPath[row.Profile.ExecutablePath] = row.Running;
            var fresh = new List<GameLibraryItem>();
            foreach (GameProfile profile in profiles)
                // 界面标签只读已有观察缓存，不启动采样。
                fresh.Add(new GameLibraryItem(profile, RunningIn(runningByPath, profile.ExecutablePath),
                    gameMode.HasRendererObservation(profile)));
            lstGames.SetItems(fresh);
            if (keepId != null) SelectProfile(keepId);
            bool empty = lstGames.Items.Count == 0;
            lstGames.Visible = !empty;
            if (gameListPanel != null) { gameListPanel.ShowEmpty = empty; gameListPanel.Invalidate(); }
            if (lblLibraryCount != null) lblLibraryCount.Text = "GAME PROFILES  /  " + profiles.Count.ToString("00");
            SyncLibraryHint();
            if (UiActive && curPage == pageLibrary) RefreshGameRunningStates(true);
        }

        private void SyncLibraryHint()
        {
            if (lblLibraryHint == null) return;
            lblLibraryHint.Text = Lang.T("v15.library.drop");
            lblLibraryHint.ForeColor = Theme.Dim;
        }

        private void RefreshGameRunningStates(bool force = false)
        {
            if (IsDisposed || !UiActive || lstGames == null || lstGames.IsDisposed) return;
            long now = DateTime.UtcNow.Ticks;
            if (!force && now < Interlocked.Read(ref nextRunningProbeTicks)) return;
            if (Interlocked.Exchange(ref runningBusy, 1) == 1)
            {
                if (force) { Interlocked.Exchange(ref runningPending, 1); Interlocked.Exchange(ref nextRunningProbeTicks, 0); }
                return;
            }
            Interlocked.Exchange(ref runningPending, 0);
            Interlocked.Exchange(ref nextRunningProbeTicks, now + RunningProbeIntervalTicks);
            int version = Volatile.Read(ref runningVersion);
            GameLibraryList list = lstGames;
            var paths = new List<string>();
            foreach (object value in list.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item != null && item.Profile != null) paths.Add(item.Profile.ExecutablePath);
            }
            try { QueueUiStateWork(delegate
            {
                Dictionary<string, bool> states = null;
                try { states = QueryLibraryRunningState(paths); }
                catch { }
                try
                {
                    PostUiStateResult(delegate
                    {
                        try
                        {
                            if (IsDisposed || !UiActive || states == null || !ReferenceEquals(list, lstGames)
                                || list.IsDisposed || version != Volatile.Read(ref runningVersion)) return;
                            ApplyRunningStates(states);
                        }
                        finally { FinishGameRunningRefresh(); }
                    });
                }
                catch { ReleaseGameRunningRefresh(); }
            }); }
            catch { ReleaseGameRunningRefresh(); }
        }

        private void ReleaseGameRunningRefresh()
        {
            Interlocked.Exchange(ref nextRunningProbeTicks, 0);
            Interlocked.Exchange(ref runningBusy, 0);
        }

        private void FinishGameRunningRefresh()
        {
            // 保留任务槽位直到界面消费结果，防止旧结果越过新扫描。
            Interlocked.Exchange(ref runningBusy, 0);
            if (Interlocked.Exchange(ref runningPending, 0) != 0 && !IsDisposed && UiActive
                && curPage == pageLibrary) RefreshGameRunningStates(true);
        }

        private void ApplyRunningStates(Dictionary<string, bool> states)
        {
            bool changed = false;
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item == null || item.Profile == null) continue;
                string path = item.Profile.ExecutablePath;
                bool running;
                if (string.IsNullOrEmpty(path) || !states.TryGetValue(path, out running)) continue;
                if (running != item.Running) { item.Running = running; changed = true; }
            }
            if (changed) lstGames.RefreshRows();
        }

        private void AddDroppedGames(string[] files)
        {
            if (files == null) return;
            var folders = new List<string>();
            string error = null;
            foreach (string file in files)
            {
                if (string.IsNullOrEmpty(file)) continue;
                if (Directory.Exists(file)) { folders.Add(file); continue; }
                if (!gameMode.AddGameFile(file, out error) && error != Lang.T("t.gamemodelibrary.1")) break;
            }
            if (!string.IsNullOrEmpty(error) && error != Lang.T("t.gamemodelibrary.1"))
                PaviseDialog.Warn(this, App.DisplayName, error);
            RefreshGames();
            if (folders.Count > 0) ShowAddGameDialog(folders);
        }

        private static bool RunningIn(Dictionary<string, bool> states, string executablePath)
        {
            bool running;
            return !string.IsNullOrEmpty(executablePath)
                && states.TryGetValue(executablePath, out running) && running;
        }

        private static Dictionary<string, bool> ProbeRunning(List<string> paths)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var wanted = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || result.ContainsKey(path)) continue;
                result[path] = false;
                string name;
                try { name = Path.GetFileNameWithoutExtension(path); }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                List<string> bucket;
                if (!wanted.TryGetValue(name, out bucket)) { bucket = new List<string>(); wanted[name] = bucket; }
                bucket.Add(path);
            }
            if (wanted.Count == 0) return result;
            Process[] all = null;
            try
            {
                all = Process.GetProcesses();
                foreach (Process process in all)
                {
                    List<string> bucket = null;
                    int pid = 0;
                    try { wanted.TryGetValue(process.ProcessName, out bucket); pid = process.Id; }
                    catch { continue; }
                    if (bucket == null) continue;
                    IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (handle == IntPtr.Zero) continue;
                    string image;
                    try { image = Native.ImagePath(handle); }
                    finally { Native.CloseHandle(handle); }
                    if (string.IsNullOrEmpty(image)) continue;
                    foreach (string path in bucket)
                        if (string.Equals(path, image, StringComparison.OrdinalIgnoreCase)) result[path] = true;
                }
            }
            catch { }
            finally { if (all != null) foreach (Process process in all) process.Dispose(); }
            return result;
        }

        private Bitmap GameIcon(string executablePath)
        {
            string key = executablePath ?? "";
            Bitmap bitmap;
            if (gameIconCache.TryGetValue(key, out bitmap)) return bitmap;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(executablePath)) bitmap = icon.ToBitmap();
            }
            catch { bitmap = appIcon == null ? new Bitmap(32, 32) : appIcon.ToBitmap(); }
            gameIconCache[key] = bitmap;
            return bitmap;
        }

    }
}
