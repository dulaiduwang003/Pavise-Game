// @author bdth 2074055628@qq.com
// 文件用途 构建游戏专栏页 按游戏分标签 承载单个游戏的深度整合

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private DBPanel pageColumn;
        private TechTabs colTabs;
        private DBPanel[] colTabPanels;
        private LolOptimizationService lolService;

        private Toggle swLolMaster;
        private Toggle swLolCleanup;
        private Toggle swLolHeadless;
        private SettingCard cardLolCleanup;
        private SettingCard cardLolHeadless;
        private Label lblColState;
        private Label lblColFreed;
        private Label lblColKilled;
        private PillButton btnColCancelScan;
        private ColumnTelemetryCell cellLolInstall;
        private ColumnTelemetryCell cellLolLcu;
        private ColumnTelemetryCell cellLolPhase;
        private ColumnTelemetryCell cellLolWeGame;
        private ColumnTelemetryCell cellLolCross;
        private ColumnTelemetryCell cellLolUx;
        private PillButton btnLolLaunch;
        private PillButton btnLolClean;
        private PillButton btnLolRestore;
        private PillButton btnLolDelete;
        private Label lblLolAction;
        private Label lblLolAddonStatus;
        private volatile bool lolDiscoveringUi;
        private int lolUiBusy;
        private int lolInspectBusy;
        private int lolFileOpBusy;
        private DateTime lolInspectUtc;
        private string lolInspectRoot = "";
        private int lolInspectSignature = -1;
        private bool lolCanDelete;
        private bool lolInspectBlocked;
        private long lolCandidateBytes;
        private int lolCandidateCount;
        private string lolInspectError = "";

        private sealed class GameColumnDef
        {
            public string Title;
            public string Hint;
            public Action<DBPanel> Build;
        }

        private void BuildColumnPage()
        {
            int y = PageHeader(pageColumn, Lang.T("nav.column"), Lang.T("col.sub"), 2);

            var defs = new[]
            {
                new GameColumnDef
                {
                    Title = Lang.T("col.tab.lol"),
                    Hint = Lang.T("col.tab.lol.sub"),
                    Build = BuildLolColumn
                }
            };

            colTabs = new TechTabs();
            colTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            var titles = new string[defs.Length];
            var hints = new string[defs.Length];
            for (int i = 0; i < defs.Length; i++) { titles[i] = defs[i].Title; hints[i] = defs[i].Hint; }
            colTabs.SetTabs(titles, hints);
            pageColumn.Controls.Add(colTabs);
            y += 48;

            colTabPanels = MakeTabPanels(pageColumn, colTabs, defs.Length, y);

            for (int i = 0; i < defs.Length; i++) defs[i].Build(colTabPanels[i]);
        }

        private void BuildLolColumn(DBPanel scroll)
        {
            int x = 6;
            int w = ScrollContentW;
            int sy = 4;

            var hero = MakeConsolePanel(scroll, x, sy, w, 118, true);
            CardLabel(hero, Lang.T("col.hero.title"), 18, 12, w - 380, 24, 11.5f, true, Theme.Fg);
            var heroSub = CardLabel(hero, Lang.T("col.hero.sub"), 18, 40, w - 400, 34, 7.6f, false, Theme.Dim);
            heroSub.AutoEllipsis = false;
            lblColState = CardLabel(hero, "", 18, 86, 240, 20, 8.2f, true, Theme.Faint);
            btnColCancelScan = new PillButton(Lang.T("lol.scan.cancel"));
            btnColCancelScan.Size = new Size(Theme.S(110), Theme.S(26));
            btnColCancelScan.Location = new Point(Theme.S(268), Theme.S(83));
            btnColCancelScan.Visible = false;
            btnColCancelScan.Click += delegate { if (lolService != null) lolService.CancelDiscovery(); };
            hero.Controls.Add(btnColCancelScan);
            CardLabel(hero, Lang.T("col.hero.freed"), w - 300, 16, 170, 14, 7.0f, false, Theme.Faint);
            lblColFreed = CardLabel(hero, "0 B", w - 300, 34, 210, 30, 15f, true, Theme.Accent);
            lblColKilled = CardLabel(hero, "", w - 300, 70, 210, 16, 7.2f, false, Theme.Dim);
            swLolMaster = MakeSwitch(false, OnLolMasterChanged);
            swLolMaster.Bg = Theme.Card;
            swLolMaster.Location = new Point(Theme.S(w - 66), Theme.S(14));
            hero.Controls.Add(swLolMaster);
            sy += 132;

            Section(scroll, Lang.T("col.sec.link"), x, sy);
            sy += 24;
            int cellGap = 10;
            int cellW = (w - cellGap * 2) / 3;
            int cellH = 64;
            cellLolInstall = MakeLolCell(scroll, x, sy, cellW, cellH, Lang.T("col.cell.install"), "lol");
            cellLolLcu = MakeLolCell(scroll, x + cellW + cellGap, sy, cellW, cellH, Lang.T("col.cell.lcu"), "settings");
            cellLolPhase = MakeLolCell(scroll, x + (cellW + cellGap) * 2, sy, cellW, cellH, Lang.T("col.cell.phase"), "game");
            sy += cellH + 10;
            cellLolWeGame = MakeLolCell(scroll, x, sy, cellW, cellH, "WEGAME", "white");
            cellLolCross = MakeLolCell(scroll, x + cellW + cellGap, sy, cellW, cellH, "CROSS", "chart");
            cellLolUx = MakeLolCell(scroll, x + (cellW + cellGap) * 2, sy, cellW, cellH, "CEF / UX", "log");
            sy += cellH + 14;

            Section(scroll, Lang.T("col.sec.protocol"), x, sy);
            sy += 24;
            int protocolW = (w - 12) / 2;
            swLolCleanup = MakeSwitch(false, OnLolCleanupChanged);
            cardLolCleanup = MakeCard(scroll, x, sy, protocolW, 94,
                Lang.T("col.cleanup"), Lang.T("col.cleanup.sub"), swLolCleanup);
            swLolHeadless = MakeSwitch(false, OnLolHeadlessChanged);
            cardLolHeadless = MakeCard(scroll, x + protocolW + 12, sy, protocolW, 94,
                Lang.T("col.headless"), Lang.T("col.headless.sub"), swLolHeadless);
            sy += 108;

            Section(scroll, Lang.T("col.sec.cmd"), x, sy);
            sy += 24;
            int buttonW = (w - 20) / 3;
            btnLolLaunch = new ColumnActionButton(Lang.T("col.btn.launch"), BtnKind.Primary);
            btnLolLaunch.SetBounds(Theme.S(x), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolLaunch.Click += OnLolLaunchClick;
            btnLolClean = new ColumnActionButton(Lang.T("col.btn.clean"));
            btnLolClean.SetBounds(Theme.S(x + buttonW + 10), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolClean.Click += OnLolCleanClick;
            btnLolRestore = new ColumnActionButton(Lang.T("col.btn.restore"));
            btnLolRestore.SetBounds(Theme.S(x + (buttonW + 10) * 2), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolRestore.Click += OnLolRestoreClick;
            scroll.Controls.AddRange(new Control[] { btnLolLaunch, btnLolClean, btnLolRestore });
            sy += 44;
            lblLolAction = new Label();
            lblLolAction.ForeColor = Theme.Dim;
            lblLolAction.BackColor = Theme.Bg;
            lblLolAction.Font = Theme.UI(7.6f, false);
            lblLolAction.AutoEllipsis = true;
            lblLolAction.UseCompatibleTextRendering = false;
            lblLolAction.SetBounds(Theme.S(x + 4), Theme.S(sy), Theme.S(w - 8), Theme.S(20));
            scroll.Controls.Add(lblLolAction);
            sy += 28;

            Section(scroll, Lang.T("col.sec.addon"), x, sy);
            sy += 24;
            var addons = MakeConsolePanel(scroll, x, sy, w, 110, false);
            CardLabel(addons, Lang.T("col.addon.title"), 18, 10, w - 280, 18, 8.2f, true, Theme.Fg);
            CardLabel(addons, Lang.T("col.addon.sub"), 18, 31, w - 290, 42, 7.6f, false, Theme.Dim);
            lblLolAddonStatus = CardLabel(addons, "", 18, 79, w - 290, 19, 7.2f, false, Theme.Faint);
            btnLolDelete = new ColumnActionButton(Lang.T("col.btn.delete"), BtnKind.Danger);
            btnLolDelete.SetBounds(Theme.S(w - 254), Theme.S(16), Theme.S(224), Theme.S(36));
            btnLolDelete.Click += OnLolDeleteClick;
            addons.Controls.Add(btnLolDelete);

            if (lolService != null)
            {
                lolService.Changed -= OnLolServiceChanged;
                lolService.Changed += OnLolServiceChanged;
            }
            pageColumn.Disposed += delegate
            {
                if (lolService != null) lolService.Changed -= OnLolServiceChanged;
            };
            RefreshLolColumn();
        }

        public bool WaitForLolIdle(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) == 0) return true;
                Thread.Sleep(50);
                waited += 50;
            }
            return Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) == 0;
        }

        private ColumnTelemetryCell MakeLolCell(Control parent, int x, int y, int w, int h, string caption, string glyph)
        {
            var cell = new ColumnTelemetryCell();
            cell.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            cell.Caption = caption;
            cell.Glyph = glyph;
            parent.Controls.Add(cell);
            return cell;
        }

        private void RefreshLolColumn()
        {
            RefreshLolColumn(true);
        }

        private void RefreshLolColumn(bool inspectFiles)
        {
            if (lolService == null || lblColFreed == null || lblColFreed.IsDisposed) return;
            LolOptimizationSnapshot snapshot;
            try { snapshot = lolService.GetSnapshot(); }
            catch (Exception ex)
            {
                lblLolAction.Text = ex.Message;
                return;
            }

            swLolMaster.SetSilently(snapshot.Enabled);
            swLolCleanup.SetSilently(snapshot.CleanupEnabled);
            swLolHeadless.SetSilently(snapshot.HeadlessEnabled);

            string state; Color stateColor;
            if (!snapshot.Enabled) { state = Lang.T("col.state.off"); stateColor = Theme.Faint; }
            else if (snapshot.Discovering) { state = Lang.T("col.state.scanning"); stateColor = Theme.Accent; }
            else if (snapshot.HeadlessActive) { state = Lang.T("col.state.headless"); stateColor = Theme.Green; }
            else if (snapshot.LcuReady) { state = Lang.T("col.state.linked"); stateColor = Theme.Green; }
            else { state = Lang.T("col.state.wait"); stateColor = Theme.Dim; }
            lblColState.Text = state;
            lblColState.ForeColor = stateColor;
            lblColFreed.Text = CacheSweep.FmtBytes(snapshot.ReleasedWorkingSetBytes);
            lblColKilled.Text = Lang.F("col.hero.killed", snapshot.CleanedProcessCount);
            btnColCancelScan.Visible = snapshot.Discovering;

            cellLolInstall.SetValue(
                snapshot.InstallationFound ? Lang.T("col.install.found") : Lang.T("col.install.none"),
                snapshot.InstallationFound ? LolCompactPath(snapshot.LolRoot) : Lang.T("col.install.wait"),
                snapshot.InstallationFound ? Theme.Green : Theme.Danger);
            cellLolLcu.SetValue(
                snapshot.LcuReady ? Lang.T("col.lcu.on") : Lang.T("col.lcu.off"),
                snapshot.ClientRunning ? Lang.T("col.lcu.client") : Lang.T("col.client.off"),
                snapshot.LcuReady ? Theme.Green : snapshot.ClientRunning ? Theme.Accent : Theme.Faint);
            cellLolPhase.SetValue(LolPhaseText(snapshot.Phase),
                snapshot.GameRunning ? Lang.T("col.game.on") : Lang.T("col.game.off"),
                snapshot.GameRunning ? Theme.Green : snapshot.LcuReady ? Theme.Accent : Theme.Faint);
            cellLolWeGame.SetValue(LolProcessState(snapshot.WeGameProcessCount, snapshot.ClientRunning),
                Lang.T("col.wegame.sub"),
                snapshot.WeGameProcessCount == 0 ? Theme.Green : Theme.Accent);
            cellLolCross.SetValue(LolProcessState(snapshot.CrossProcessCount, snapshot.ClientRunning),
                Lang.T("col.cross.sub"),
                snapshot.CrossProcessCount == 0 ? Theme.Green : Theme.Accent);
            cellLolUx.SetValue(
                snapshot.HeadlessActive ? Lang.T("col.ux.closed") : LolProcessState(snapshot.UxProcessCount, false),
                snapshot.HeadlessActive ? Lang.T("col.ux.restore") :
                    snapshot.UxProcessCount > 0 ? Lang.T("col.ux.visible") : Lang.T("col.client.off"),
                snapshot.HeadlessActive ? Theme.Green : snapshot.UxProcessCount > 0 ? Theme.Accent : Theme.Faint);

            if (inspectFiles && pageColumn != null && pageColumn.Visible && !snapshot.GameRunning)
                QueueLolInspection(snapshot);
            bool runtimeBusy = Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0;
            bool fileBusy = Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0;
            bool inspectBusy = Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0;
            bool busy = runtimeBusy || fileBusy;
            bool off = !snapshot.Enabled;
            bool locked = busy || off || snapshot.Discovering;
            swLolCleanup.Enabled = !off;
            swLolHeadless.Enabled = !off;
            bool weGameUp = snapshot.WeGameProcessCount > 0;
            btnLolLaunch.Text = weGameUp
                ? Lang.T("lol.btn.wegamerunning")
                : Lang.T("col.btn.launch");
            btnLolLaunch.Enabled = !locked && snapshot.WeGameFound && !snapshot.ClientRunning && !weGameUp;
            btnLolClean.Enabled = !locked && snapshot.ClientRunning && snapshot.LcuReady;
            btnLolRestore.Enabled = !locked && snapshot.ClientRunning && (snapshot.HeadlessActive || snapshot.UxProcessCount == 0);
            cardLolCleanup.SetValue(snapshot.CleanupEnabled ? Lang.T("col.proto.on") : Lang.T("col.proto.off"),
                snapshot.CleanupEnabled ? Theme.Green : Theme.Faint);
            cardLolHeadless.SetValue(snapshot.HeadlessEnabled ? Lang.T("col.proto.on") : Lang.T("col.proto.off"),
                snapshot.HeadlessEnabled ? Theme.Green : Theme.Faint);

            string action = !string.IsNullOrEmpty(snapshot.LastError) ? snapshot.LastError : snapshot.LastAction;
            if (snapshot.LastActionUtc != DateTime.MinValue
                && DateTime.UtcNow - snapshot.LastActionUtc > TimeSpan.FromSeconds(20))
                action = "";
            lblLolAction.Text = string.IsNullOrEmpty(action)
                ? Lang.T("col.ready")
                : action;
            lblLolAction.ForeColor = !string.IsNullOrEmpty(snapshot.LastError) ? Theme.Danger : Theme.Dim;

            bool clientBlocksFiles = snapshot.ClientRunning || snapshot.GameRunning;
            btnLolDelete.Enabled = !clientBlocksFiles && !locked && !inspectBusy && lolCanDelete;
            UpdateLolAddonStatus(clientBlocksFiles, off);
            UpdateLolDiscoveryLock(snapshot);
        }

        private void UpdateLolDiscoveryLock(LolOptimizationSnapshot snapshot)
        {
            bool discovering = snapshot != null && snapshot.Discovering;
            lolDiscoveringUi = discovering;
            if (modeButton != null) modeButton.Enabled = !discovering;
            if (discovering) SetModeFlyout(false);
        }

        private void OnLolMasterChanged(object sender, EventArgs e)
        {
            if (lolService == null) return;
            lolService.Enabled = swLolMaster.Checked;
            RefreshLolColumn();
        }

        private void OnLolCleanupChanged(object sender, EventArgs e)
        {
            if (lolService == null) return;
            lolService.CleanupEnabled = swLolCleanup.Checked;
            RefreshLolColumn();
        }

        private void OnLolHeadlessChanged(object sender, EventArgs e)
        {
            if (lolService == null) return;
            lolService.HeadlessEnabled = swLolHeadless.Checked;
            RefreshLolColumn();
        }

        private void OnLolLaunchClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.LaunchWeGame(); }, Lang.T("col.busy.launch"));
        }

        private void OnLolCleanClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.CleanNow(); }, Lang.T("col.busy.clean"));
        }

        private void OnLolRestoreClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.RestoreNow(); }, Lang.T("col.busy.restore"));
        }

        private void RunLolAction(Func<bool> action, string busyText)
        {
            if (action == null || Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolUiBusy, 1, 0) != 0) return;
            SetLolButtonsBusy(true);
            lblLolAction.ForeColor = Theme.Accent;
            lblLolAction.Text = busyText;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string error = "";
                try
                {
                    ok = action();
                    if (!ok)
                    {
                        LolOptimizationSnapshot snapshot = lolService.GetSnapshot();
                        error = snapshot.LastError;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                Interlocked.Exchange(ref lolUiBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        RefreshLolColumn();
                        if (!ok && !string.IsNullOrEmpty(error))
                            PaviseDialog.Warn(this, App.DisplayName, error);
                    });
                }
                catch { }
            });
        }

        private void SetLolButtonsBusy(bool busy)
        {
            if (btnLolLaunch != null) btnLolLaunch.Enabled = !busy;
            if (btnLolClean != null) btnLolClean.Enabled = !busy;
            if (btnLolRestore != null) btnLolRestore.Enabled = !busy;
            if (btnLolDelete != null) btnLolDelete.Enabled = !busy;
        }

        private void OnLolServiceChanged()
        {
            if (IsDisposed || !IsHandleCreated || !UiActive) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    try { UpdateLolDiscoveryLock(lolService != null ? lolService.GetSnapshot() : null); }
                    catch { }
                    if (UiActive && pageColumn != null && pageColumn.Visible) RefreshLolColumn();
                });
            }
            catch { }
        }

        private void QueueLolInspection(LolOptimizationSnapshot snapshot)
        {
            string normalized = (snapshot != null ? snapshot.LolRoot : null) ?? "";
            if (normalized.Length == 0)
            {
                lolInspectRoot = "";
                lolInspectUtc = DateTime.MinValue;
                lolInspectSignature = -1;
                lolCanDelete = false;
                lolInspectBlocked = false;
                lolCandidateBytes = 0;
                lolCandidateCount = 0;
                lolInspectError = "";
                return;
            }
            bool rootChanged = !string.Equals(normalized, lolInspectRoot, StringComparison.OrdinalIgnoreCase);
            int signature = (snapshot.ClientRunning ? 1 : 0)
                | (snapshot.GameRunning ? 2 : 0)
                | (snapshot.WeGameProcessCount > 0 ? 4 : 0)
                | (snapshot.UxProcessCount > 0 ? 8 : 0)
                | (snapshot.CrossProcessCount > 0 ? 16 : 0);
            bool envChanged = signature != lolInspectSignature;
            bool quietEnv = signature == 0;
            TimeSpan maxAge = lolInspectBlocked && quietEnv
                ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2);
            if (!rootChanged && !envChanged
                && DateTime.UtcNow - lolInspectUtc < maxAge) return;
            if (Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0) return;
            if (Interlocked.CompareExchange(ref lolInspectBusy, 1, 0) != 0) return;
            lolInspectSignature = signature;
            if (rootChanged)
            {
                lolCanDelete = false;
                lolInspectBlocked = false;
                lolCandidateBytes = 0;
                lolCandidateCount = 0;
                lolInspectError = "";
                lolInspectUtc = DateTime.MinValue;
            }
            lolInspectRoot = normalized;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool canDelete = false;
                bool blocked = false;
                long candidateBytes = 0;
                int candidateCount = 0;
                string error = "";
                try
                {
                    var inspection = LolAddonCleaner.Inspect(normalized);
                    canDelete = inspection.CanDelete;
                    blocked = inspection.IsBlocked;
                    candidateBytes = inspection.CandidateBytes;
                    candidateCount = inspection.CandidateCount;
                    error = inspection.Error;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        lolCanDelete = canDelete;
                        lolInspectBlocked = blocked;
                        lolCandidateBytes = candidateBytes;
                        lolCandidateCount = candidateCount;
                        lolInspectError = error ?? "";
                        lolInspectUtc = DateTime.UtcNow;
                        Interlocked.Exchange(ref lolInspectBusy, 0);
                        if (!IsDisposed && UiActive) RefreshLolColumn(false);
                    });
                }
                catch { Interlocked.Exchange(ref lolInspectBusy, 0); }
            });
        }

        private void OnLolDeleteClick(object sender, EventArgs e)
        {
            if (lolService == null) return;
            LolOptimizationSnapshot snapshot = lolService.GetSnapshot();
            if (snapshot.ClientRunning || snapshot.GameRunning || !lolCanDelete ||
                Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0) return;
            if (!PaviseDialog.Confirm(this, Lang.T("col.btn.delete"), Lang.T("col.addon.confirm"), DlgKind.Danger))
                return;
            RunLolDelete(snapshot.LolRoot);
        }

        private void RunLolDelete(string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            if (Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolFileOpBusy, 1, 0) != 0)
            {
                lblLolAddonStatus.ForeColor = Theme.Accent;
                lblLolAddonStatus.Text = Lang.T("col.addon.busy");
                return;
            }
            SetLolButtonsBusy(true);
            lblLolAddonStatus.ForeColor = Theme.Accent;
            lblLolAddonStatus.Text = Lang.T("col.addon.deleting");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool success = false;
                string message = "";
                try
                {
                    var result = LolAddonCleaner.Delete(root);
                    success = result.Success;
                    message = result.Message;
                    Logger.Log("英雄联盟附加层删除 " + message);
                }
                catch (Exception ex) { message = ex.Message; }
                Interlocked.Exchange(ref lolFileOpBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        lolInspectUtc = DateTime.MinValue;
                        RefreshLolColumn();
                        if (string.IsNullOrEmpty(message)) return;
                        if (success) PaviseDialog.Info(this, App.DisplayName, message);
                        else PaviseDialog.Warn(this, App.DisplayName, message);
                    });
                }
                catch { }
            });
        }

        private void UpdateLolAddonStatus(bool clientBlocksFiles, bool columnOff)
        {
            if (lblLolAddonStatus == null) return;
            if (columnOff)
            {
                lblLolAddonStatus.Text = Lang.T("lol.state.columnoff");
                lblLolAddonStatus.ForeColor = Theme.Faint;
                return;
            }
            if (clientBlocksFiles)
            {
                lblLolAddonStatus.Text = Lang.T("col.addon.locked");
                lblLolAddonStatus.ForeColor = Theme.Danger;
                return;
            }
            if (Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0)
            {
                lblLolAddonStatus.Text = Lang.T("col.addon.busy");
                lblLolAddonStatus.ForeColor = Theme.Accent;
                return;
            }
            if (!string.IsNullOrEmpty(lolInspectError))
            {
                lblLolAddonStatus.Text = lolInspectError;
                lblLolAddonStatus.ForeColor = Theme.Danger;
                return;
            }
            if (lolInspectBlocked)
            {
                lblLolAddonStatus.Text = Lang.T("col.addon.blocked");
                lblLolAddonStatus.ForeColor = Theme.Danger;
                return;
            }
            if (lolCandidateCount > 0)
            {
                lblLolAddonStatus.Text = Lang.F("col.addon.ready",
                    lolCandidateCount, CacheSweep.FmtBytes(lolCandidateBytes));
                lblLolAddonStatus.ForeColor = Theme.Green;
                return;
            }
            lblLolAddonStatus.Text = Lang.T("col.addon.clean");
            lblLolAddonStatus.ForeColor = Theme.Faint;
        }

        private static string LolPhaseText(string phase)
        {
            string value = phase ?? "";
            if (value.Equals("InProgress", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.ingame");
            if (value.Equals("ChampSelect", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.champselect");
            if (value.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.matchmaking");
            if (value.Equals("ReadyCheck", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.readycheck");
            if (value.Equals("WaitingForStats", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.stats");
            if (value.Equals("Lobby", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.lobby");
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
                return Lang.T("col.phase.idle");
            return value;
        }

        private static string LolProcessState(int count, bool zeroIsClean)
        {
            if (count <= 0)
                return Lang.T(zeroIsClean ? "col.proc.exit" : "col.proc.none");
            return Lang.F("col.proc.count", count);
        }

        private static string LolCompactPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length <= 34) return path;
            return "…" + path.Substring(path.Length - 33);
        }
    }
}
