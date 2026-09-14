// @author bdth 2074055628@qq.com
// File purpose Build the game library page and keep entry running state and network policy in sync
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
        private PillButton btnGameConfig;
        private PillButton btnGameMore;
        private Label lblLibrarySelection;
        private Toggle swAutoAdd;
        private Label lblLibraryHint;
        private Label lblLibraryCount;
        private bool familyChangeBusy;
        private EmptyStatePanel gameListPanel;
        private readonly Dictionary<string, Bitmap> gameIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private int runningBusy;
        private long nextRunningProbeTicks;
        private static readonly long RunningProbeIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

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
            lstGames.FamilyToggleRequested += delegate(object sender, GameLibraryEventArgs e) { ToggleGameFamilySuppression(e.Item); };
            listWrap.Controls.AddRange(new Control[] { lstGames, lblLibraryCount, listHint });
            int bx = ContentX + listW + 16, bw = ContentW - listW - 16, bh = 40;
            var add = new PillButton(Lang.T("v15.library.add"), BtnKind.Primary);
            add.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(bw), Theme.S(bh));
            add.Click += delegate { ShowAddGameDialog(); };
            btnGameConfig = new PillButton(Lang.T("workflow.library.settings"));
            btnGameConfig.Name = "libraryGameSettings";
            btnGameConfig.SetBounds(Theme.S(bx), Theme.S(y + 50), Theme.S(bw), Theme.S(bh));
            btnGameConfig.Click += delegate { OpenSelectedGameConfig(); };
            btnGameMore = new PillButton(Lang.T("workflow.more")) { Name = "libraryMore" };
            btnGameMore.SetBounds(Theme.S(bx), Theme.S(y + 100), Theme.S(bw), Theme.S(bh));
            btnGameMore.Click += delegate { ShowLibraryMore(); };
            lblLibrarySelection = CardLabel(pageLibrary,"",bx + 4,y + 150,bw - 8,44,9f,false,Theme.Dim);
            lblLibrarySelection.Name = "librarySelection";
            lblLibrarySelection.AutoEllipsis = true;
            lstGames.SelectedIndexChanged += delegate { UpdateForceButton(); };
            lstGames.ItemActivated += delegate { OpenSelectedGameConfig(); };
            var lblAutoAdd = new Label();
            lblAutoAdd.Text = Lang.T("v15.library.autoadd");
            lblAutoAdd.BackColor = Theme.Bg; lblAutoAdd.ForeColor = Theme.Fg;
            lblAutoAdd.Font = Theme.UI(9f, true);
            lblAutoAdd.SetBounds(Theme.S(bx + 4), Theme.S(y + 206), Theme.S(bw - 62), Theme.S(22));
            swAutoAdd = MakeSwitch(gameMode.AutoAddFullscreen,
                delegate { gameMode.AutoAddFullscreen = swAutoAdd.Checked; });
            swAutoAdd.Bg = Theme.Bg;
            swAutoAdd.Location = new Point(Theme.S(bx + bw - 50), Theme.S(y + 204));
            var lblAutoAddDesc = new Label();
            lblAutoAddDesc.Text = Lang.T("v15.library.autoadd.desc");
            lblAutoAddDesc.BackColor = Theme.Bg; lblAutoAddDesc.ForeColor = Theme.Dim;
            lblAutoAddDesc.Font = Theme.UI(8.2f, false);
            lblAutoAddDesc.SetBounds(Theme.S(bx + 4), Theme.S(y + 234), Theme.S(bw - 8), Theme.S(70));
            var familyGuide = new RoundPanel();
            familyGuide.SetBounds(Theme.S(bx), Theme.S(y + 310), Theme.S(bw), Theme.S(122));
            familyGuide.Fill = Theme.Card; familyGuide.Border = Theme.Stroke; familyGuide.AccentEdge = true;
            var familyGuideTitle = new Label();
            familyGuideTitle.Text = Lang.T("lib.family.guide.title"); familyGuideTitle.Font = Theme.UI(8.8f, true);
            familyGuideTitle.BackColor = Color.Transparent; familyGuideTitle.ForeColor = Theme.Fg;
            familyGuideTitle.SetBounds(Theme.S(14), Theme.S(13), Theme.S(bw - 28), Theme.S(22));
            var familyGuideBody = new Label();
            familyGuideBody.Text = Lang.T("lib.family.guide.body"); familyGuideBody.Font = Theme.UI(8.0f, false);
            familyGuideBody.BackColor = Color.Transparent; familyGuideBody.ForeColor = Theme.Dim;
            familyGuideBody.SetBounds(Theme.S(14), Theme.S(42), Theme.S(bw - 28), Theme.S(70));
            familyGuide.Controls.AddRange(new Control[] { familyGuideTitle, familyGuideBody });
            lblLibraryHint = new Label(); lblLibraryHint.BackColor = Theme.Bg;
            lblLibraryHint.Font = Theme.UI(7.9f, false); lblLibraryHint.AutoEllipsis = true;
            lblLibraryHint.SetBounds(Theme.S(bx + 4), Theme.S(y + 446), Theme.S(bw - 8), Theme.S(60));
            SyncLibraryHint();
            pageLibrary.Controls.AddRange(new Control[] { listWrap, add, btnGameConfig, btnGameMore,
                lblAutoAdd, swAutoAdd, lblAutoAddDesc, familyGuide, lblLibraryHint });
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
                // An entry with no renderer observed may not even be the game itself; block only turning on, never turning off
                if (turningOn && !item.RendererObserved)
                {
                    PaviseDialog.Warn(this, Lang.T("lib.family.suppress"), Lang.T("lib.family.gated.body"));
                    return;
                }
                // Renderer observed does not mean the associated background can be suppressed safely; every enable must confirm the risk
                if (turningOn)
                {
                    using (var warning = new FamilySuppressionDialog(current.Name, targetPath))
                        if (ShowDim(warning) != DialogResult.OK) return;
                    // A modal window still dispatches library updates; the risk confirmation for the old EXE must not be applied to a corrected new target
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
                // AutoCheck=false: on cancel or save failure echo the real model, leaving no fake on state
                RefreshGames(); SelectProfile(keepId);
            }
        }

        private GameProfile FindLibraryProfile(string id)
        {
            foreach (GameProfile profile in gameMode.GetProfiles())
                if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) return profile;
            return null;
        }

        private void UpdateForceButton()
        {
            GameLibraryItem item = lstGames == null ? null : lstGames.SelectedItem as GameLibraryItem;
            if (btnGameConfig != null) btnGameConfig.Enabled = item != null;
            if (btnGameMore != null) btnGameMore.Enabled = item != null;
            if (lblLibrarySelection != null) lblLibrarySelection.Text = item == null
                ? Lang.T("workflow.library.pick") : Lang.F("workflow.library.selected",item.Profile.Name);
        }

        private void ShowLibraryMore()
        {
            var selected = lstGames.SelectedItem as GameLibraryItem;
            if (selected == null || selected.Profile == null) return;
            string id = selected.Profile.Id;
            var menu = new ContextMenuStrip { Renderer = new TechMenuRenderer(),
                BackColor = Theme.Card, ForeColor = Theme.Fg, Font = Theme.UI(9f,false), ShowImageMargin = false };
            Action<Action> invoke = delegate(Action action) {
                if (FindLibraryProfile(id) == null) return;
                SelectProfile(id); action(); };
            menu.Items.Add(Lang.T("lib.rename"),null,delegate { invoke(RenameSelectedGame); });
            var force = new ToolStripMenuItem(Lang.T(selected.Profile.ForceTrigger ? "v15.library.force.on" : "v15.library.force"));
            force.Checked = selected.Profile.ForceTrigger;
            force.Click += delegate { invoke(ToggleForceTrigger); };
            menu.Items.Add(force);
            menu.Items.Add(new ToolStripSeparator());
            var remove = new ToolStripMenuItem(Lang.T("btn.remove")) { ForeColor = Theme.Danger };
            remove.Click += delegate { invoke(delegate { gameMode.RemoveProfile(id); RefreshGames(); }); };
            menu.Items.Add(remove);
            menu.Closed += delegate {
                try { BeginInvoke((MethodInvoker)delegate { menu.Dispose(); }); }
                catch { menu.Dispose(); } };
            menu.Show(btnGameMore,new Point(0,btnGameMore.Height));
        }

        private void OpenSelectedGameConfig()
        {
            GameLibraryItem item = lstGames == null ? null : lstGames.SelectedItem as GameLibraryItem;
            if (item == null || item.Profile == null) return;
            ShowGameConfigPage(item.Profile.Id);
        }

        private void RenameSelectedGame()
        {
            GameLibraryItem item = lstGames == null ? null : lstGames.SelectedItem as GameLibraryItem;
            if (item == null || item.Profile == null) return;
            string keepId = item.Profile.Id;
            string name = PaviseDialog.Prompt(this, Lang.T("lib.rename"),
                Lang.F("lib.rename.sub", item.Profile.Name), item.Profile.Name);
            if (string.IsNullOrEmpty(name) || name == item.Profile.Name) return;
            gameMode.RenameProfile(keepId, name);
            RefreshGames();
            SelectProfile(keepId);
        }

        private void ToggleForceTrigger()
        {
            GameLibraryItem item = lstGames == null ? null : lstGames.SelectedItem as GameLibraryItem;
            if (item == null || item.Profile == null) return;
            bool turningOn = !item.Profile.ForceTrigger;
            if (turningOn && !PaviseDialog.Confirm(this, Lang.T("v15.library.force"),
                    Lang.F("v15.library.force.confirm", item.Profile.Name), DlgKind.Warn))
                return;
            string keepId = item.Profile.Id;
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
            if (lstGames == null) return;
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
                // The tag reads only the already-observed cache; no EXE lookup or GPU sampling on the UI thread
                fresh.Add(new GameLibraryItem(profile, RunningIn(runningByPath, profile.ExecutablePath),
                    gameMode.HasRendererObservation(profile)));
            lstGames.SetItems(fresh);
            if (keepId != null) SelectProfile(keepId);
            bool empty = lstGames.Items.Count == 0;
            lstGames.Visible = !empty;
            if (gameListPanel != null) { gameListPanel.ShowEmpty = empty; gameListPanel.Invalidate(); }
            if (lblLibraryCount != null) lblLibraryCount.Text = "GAME PROFILES  /  " + profiles.Count.ToString("00");
            SyncLibraryHint();
            UpdateForceButton();
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
            if (!UiActive || lstGames == null) return;
            long now = DateTime.UtcNow.Ticks;
            if (!force && now < Interlocked.Read(ref nextRunningProbeTicks)) return;
            if (Interlocked.Exchange(ref runningBusy, 1) == 1)
            {
                // The previous probe is still in flight and holds the path snapshot from when it started; a game just added is not in it
                //   Open the gate so the next page heartbeat runs a catch-up probe; otherwise the new entry waits a full probe cycle before lighting up
                if (force) Interlocked.Exchange(ref nextRunningProbeTicks, 0);
                return;
            }
            Interlocked.Exchange(ref nextRunningProbeTicks, now + RunningProbeIntervalTicks);
            var paths = new List<string>();
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item != null && item.Profile != null) paths.Add(item.Profile.ExecutablePath);
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, bool> states = null;
                try { states = ProbeRunning(paths); }
                catch { }
                Interlocked.Exchange(ref runningBusy, 0);
                if (states == null || !UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive || lstGames == null) return;
                        ApplyRunningStates(states);
                    }));
                }
                catch { }
            });
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
            // Folders are not added directly; open the add window listing the candidate programs inside; a single hit is pre-checked
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
