// @author bdth 2074055628@qq.com

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {
        private DBPanel pageLol;
        private LolOptimizationService lolService;
        private ColumnCommandDeck lolDeck;
        private Toggle swLolMaster;
        private Toggle swLolCleanup;
        private Toggle swLolHeadless;
        private SettingCard cardLolCleanup;
        private SettingCard cardLolHeadless;
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
        private LoadingOverlay lolScanOverlay;
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

        private void BuildLolPage()
        {
            int y = PageHeader(pageLol, LolText("英雄联盟专栏"),
                LolText("WeGame 只负责认证与拉起；大厅就绪后精确退出附加进程，对局中关闭 CEF，赛后自动回显客户端。"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageLol.Controls.Add(scroll);

            int x = 6;
            int w = ScrollContentW;
            int sy = 4;

            lolDeck = new ColumnCommandDeck();
            lolDeck.SetBounds(Theme.S(x), Theme.S(sy), Theme.S(w), Theme.S(124));
            lolDeck.Eyebrow = "AEGIS  //  LEAGUE DIRECTIVE";
            lolDeck.Title = LolText("英雄联盟 · 极限链路");
            lolDeck.Subtitle = LolText("不注入、不改内存、不触碰游戏核心文件。");
            lolDeck.MemoryLabel = LolText("本次累计释放");
            swLolMaster = MakeSwitch(true, OnLolMasterChanged);
            swLolMaster.Bg = Theme.Card;
            swLolMaster.SetBounds(Theme.S(w - 66), Theme.S(10), Theme.S(46), Theme.S(24));
            lolDeck.Controls.Add(swLolMaster);
            scroll.Controls.Add(lolDeck);
            sy += 138;

            int protocolW = (w - 12) / 2;
            swLolCleanup = MakeSwitch(true, OnLolCleanupChanged);
            cardLolCleanup = MakeCard(scroll, x, sy, protocolW, 94,
                LolText("WeGame 借壳启动"),
                LolText("允许正常登录与启动；LCU 确认大厅可用后，精确退出 WeGame、Cross 与腾讯附加进程。"),
                swLolCleanup);
            swLolHeadless = MakeSwitch(true, OnLolHeadlessChanged);
            cardLolHeadless = MakeCard(scroll, x + protocolW + 12, sy, protocolW, 94,
                LolText("对局真无头"),
                LolText("进入对局后通过客户端原生接口关闭 CEF/UX；游戏结束由守护链自动拉起并回显大厅。"),
                swLolHeadless);
            sy += 108;

            Section(scroll, LolText("实时链路状态"), x, sy);
            sy += 24;
            int cellGap = 10;
            int cellW = (w - cellGap * 2) / 3;
            int cellH = 64;
            cellLolInstall = MakeLolCell(scroll, x, sy, cellW, cellH,
                LolText("客户端安装"), "lol");
            cellLolLcu = MakeLolCell(scroll, x + cellW + cellGap, sy, cellW, cellH,
                LolText("LCU 会话"), "settings");
            cellLolPhase = MakeLolCell(scroll, x + (cellW + cellGap) * 2, sy, cellW, cellH,
                LolText("游戏阶段"), "game");
            sy += cellH + 10;
            cellLolWeGame = MakeLolCell(scroll, x, sy, cellW, cellH, "WEGAME", "white");
            cellLolCross = MakeLolCell(scroll, x + cellW + cellGap, sy, cellW, cellH, "CROSS", "chart");
            cellLolUx = MakeLolCell(scroll, x + (cellW + cellGap) * 2, sy, cellW, cellH, "CEF / UX", "log");
            sy += cellH + 14;

            Section(scroll, LolText("手动指令"), x, sy);
            sy += 24;
            int buttonW = (w - 20) / 3;
            btnLolLaunch = new ColumnActionButton(LolText("启动 WeGame"), BtnKind.Primary);
            btnLolLaunch.SetBounds(Theme.S(x), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolLaunch.Click += OnLolLaunchClick;
            btnLolClean = new ColumnActionButton(LolText("立即净化"));
            btnLolClean.SetBounds(Theme.S(x + buttonW + 10), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolClean.Click += OnLolCleanClick;
            btnLolRestore = new ColumnActionButton(LolText("立即恢复客户端"));
            btnLolRestore.SetBounds(Theme.S(x + (buttonW + 10) * 2), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolRestore.Click += OnLolRestoreClick;
            scroll.Controls.AddRange(new Control[] { btnLolLaunch, btnLolClean, btnLolRestore });
            sy += 44;
            lblLolAction = new Label();
            lblLolAction.ForeColor = Theme.Dim;
            lblLolAction.BackColor = Theme.Bg;
            lblLolAction.Font = Theme.Mono(7f);
            lblLolAction.AutoEllipsis = true;
            lblLolAction.UseCompatibleTextRendering = false;
            lblLolAction.SetBounds(Theme.S(x + 4), Theme.S(sy), Theme.S(w - 8), Theme.S(20));
            scroll.Controls.Add(lblLolAction);
            sy += 28;

            Section(scroll, LolText("附加层清理"), x, sy);
            sy += 24;
            var addons = MakeConsolePanel(scroll, x, sy, w, 110, true);
            CardLabel(addons, LolText("附加层直接删除"), 18, 10, w - 280, 18, 8.2f, true, Theme.Fg);
            CardLabel(addons,
                LolText("直接删除 AI 教练、iCreate 录制等附加层；客户端更新会重新下载，无需保留副本。"),
                18, 31, w - 290, 42, 7.6f, false, Theme.Dim);
            lblLolAddonStatus = CardLabel(addons, "", 18, 79, w - 290, 19, 7.2f, false, Theme.Faint);
            btnLolDelete = new ColumnActionButton(LolText("删除附加层"), BtnKind.Danger);
            btnLolDelete.SetBounds(Theme.S(w - 254), Theme.S(16), Theme.S(224), Theme.S(36));
            btnLolDelete.Click += OnLolDeleteClick;
            addons.Controls.Add(btnLolDelete);
            sy += 120;

            lolScanOverlay = new LoadingOverlay();
            lolScanOverlay.SetBounds(
                0, Theme.S(TopH), Theme.S(WinW), Theme.S(WinH - TopH));
            Controls.Add(lolScanOverlay);
            lolScanOverlay.BringToFront();

            if (lolService != null)
            {
                lolService.Changed -= OnLolServiceChanged;
                lolService.Changed += OnLolServiceChanged;
            }
            pageLol.Disposed += delegate
            {
                if (lolService != null) lolService.Changed -= OnLolServiceChanged;
            };
            RefreshLolPage();
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

        private void RefreshLolPage()
        {
            RefreshLolPage(true);
        }

        private void RefreshLolPage(bool inspectFiles)
        {
            if (lolService == null || lolDeck == null || lolDeck.IsDisposed) return;
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

            string deckState = !snapshot.Enabled
                ? LolText("专栏已停用")
                : snapshot.HeadlessActive
                    ? LolText("对局无头运行中")
                    : snapshot.LcuReady
                        ? LolText("链路已接管")
                        : LolText("等待客户端");
            string processText = LolText("累计终止 ")
                + snapshot.CleanedProcessCount.ToString();
            lolDeck.SetState(deckState, CacheSweep.FmtBytes(snapshot.ReleasedWorkingSetBytes), processText,
                snapshot.Enabled && (snapshot.LcuReady || snapshot.GameRunning));

            cellLolInstall.SetValue(
                snapshot.InstallationFound ? LolText("已定位") : LolText("未发现"),
                snapshot.InstallationFound ? LolCompactPath(snapshot.LolRoot) : LolText("等待自动扫描"),
                snapshot.InstallationFound ? Theme.Green : Theme.Danger);
            cellLolLcu.SetValue(
                snapshot.LcuReady ? LolText("已连接") : LolText("未连接"),
                snapshot.ClientRunning ? LolText("LeagueClient 后端在线") :
                    LolText("客户端未运行"),
                snapshot.LcuReady ? Theme.Green : snapshot.ClientRunning ? Theme.Accent : Theme.Faint);
            cellLolPhase.SetValue(LolPhaseText(snapshot.Phase),
                snapshot.GameRunning ? LolText("游戏进程在线") :
                    LolText("未进入对局"),
                snapshot.GameRunning ? Theme.Green : snapshot.LcuReady ? Theme.Accent : Theme.Faint);
            cellLolWeGame.SetValue(LolProcessState(snapshot.WeGameProcessCount, snapshot.ClientRunning),
                LolText("大厅确认后精确退出"),
                snapshot.WeGameProcessCount == 0 ? Theme.Green : Theme.Accent);
            cellLolCross.SetValue(LolProcessState(snapshot.CrossProcessCount, snapshot.ClientRunning),
                LolText("AI 教练 / 录制 / 覆盖层"),
                snapshot.CrossProcessCount == 0 ? Theme.Green : Theme.Accent);
            cellLolUx.SetValue(
                snapshot.HeadlessActive ? LolText("已关闭") : LolProcessState(snapshot.UxProcessCount, false),
                snapshot.HeadlessActive ? LolText("赛后自动回显") :
                    snapshot.UxProcessCount > 0 ? LolText("大厅界面可见") :
                    LolText("客户端未运行"),
                snapshot.HeadlessActive ? Theme.Green : snapshot.UxProcessCount > 0 ? Theme.Accent : Theme.Faint);

            if (inspectFiles && pageLol != null && pageLol.Visible && !snapshot.GameRunning)
                QueueLolInspection(snapshot);
            bool runtimeBusy = Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0;
            bool fileBusy = Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0;
            bool inspectBusy = Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0;
            bool busy = runtimeBusy || fileBusy;
            bool off = !snapshot.Enabled;
            bool locked = busy || off;
            swLolCleanup.Enabled = !off;
            swLolHeadless.Enabled = !off;
            bool weGameUp = snapshot.WeGameProcessCount > 0;
            btnLolLaunch.Text = weGameUp
                ? Lang.T("lol.btn.wegamerunning")
                : LolText("启动 WeGame");
            btnLolLaunch.Enabled = !locked && snapshot.WeGameFound && !snapshot.ClientRunning && !weGameUp;
            btnLolClean.Enabled = !locked && snapshot.ClientRunning && snapshot.LcuReady;
            btnLolRestore.Enabled = !locked && snapshot.ClientRunning && (snapshot.HeadlessActive || snapshot.UxProcessCount == 0);
            cardLolCleanup.SetValue(snapshot.CleanupEnabled ? LolText("自动") : LolText("关闭"),
                snapshot.CleanupEnabled ? Theme.Green : Theme.Faint);
            cardLolHeadless.SetValue(snapshot.HeadlessEnabled ? LolText("自动") : LolText("关闭"),
                snapshot.HeadlessEnabled ? Theme.Green : Theme.Faint);

            string action = !string.IsNullOrEmpty(snapshot.LastError) ? snapshot.LastError : snapshot.LastAction;

            if (snapshot.LastActionUtc != DateTime.MinValue
                && DateTime.UtcNow - snapshot.LastActionUtc > TimeSpan.FromSeconds(20))
                action = "";
            lblLolAction.Text = string.IsNullOrEmpty(action)
                ? LolText("READY // 等待指令")
                : action;
            lblLolAction.ForeColor = !string.IsNullOrEmpty(snapshot.LastError) ? Theme.Danger : Theme.Dim;

            bool clientBlocksFiles = snapshot.ClientRunning || snapshot.GameRunning;
            btnLolDelete.Enabled = !clientBlocksFiles && !locked && !inspectBusy &&
                lolCanDelete;
            UpdateLolAddonStatus(clientBlocksFiles, off);
            UpdateLolScanOverlay(snapshot);
        }

        private void UpdateLolScanOverlay(LolOptimizationSnapshot snapshot)
        {
            if (lolScanOverlay == null) return;
            if (snapshot != null && snapshot.Discovering)
                lolScanOverlay.ShowOverlay(Lang.T("lol.scan.overlay"));
            else
                lolScanOverlay.HideOverlay();
        }

        private void OnLolMasterChanged(object sender, EventArgs e)
        {
            if (lolService == null) return;
            lolService.Enabled = swLolMaster.Checked;
            RefreshLolPage();
        }

        private void OnLolCleanupChanged(object sender, EventArgs e)
        {
            if (lolService == null) return;
            lolService.CleanupEnabled = swLolCleanup.Checked;
            RefreshLolPage();
        }

        private void OnLolHeadlessChanged(object sender, EventArgs e)
        {
            if (lolService == null) return;
            lolService.HeadlessEnabled = swLolHeadless.Checked;
            RefreshLolPage();
        }

        private void OnLolLaunchClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.LaunchWeGame(); },
                LolText("正在启动 WeGame…"));
        }

        private void OnLolCleanClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.CleanNow(); },
                LolText("正在执行精准净化…"));
        }

        private void OnLolRestoreClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.RestoreNow(); },
                LolText("正在恢复客户端界面…"));
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
                        RefreshLolPage();
                        if (!ok && !string.IsNullOrEmpty(error))
                            MessageBox.Show(this, error, "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    if (UiActive && pageLol != null && pageLol.Visible) RefreshLolPage();
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
                        if (!IsDisposed && UiActive) RefreshLolPage(false);
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
            DialogResult result = MessageBox.Show(this,
                LolText("将直接删除当前识别出的英雄联盟附加层目录（AI 教练、iCreate 录制等）。\r\n\r\n此操作不可恢复；客户端更新或修复时会重新下载这些组件。英雄联盟核心、登录链路、更新器和游戏文件不在删除范围内。\r\n\r\n请确认英雄联盟与 WeGame 已完全退出。继续删除吗？"),
                "Aegis", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;
            RunLolDelete(snapshot.LolRoot);
        }

        private void RunLolDelete(string root)
        {
            if (string.IsNullOrEmpty(root) || Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolFileOpBusy, 1, 0) != 0) return;
            SetLolButtonsBusy(true);
            lblLolAddonStatus.ForeColor = Theme.Accent;
            lblLolAddonStatus.Text = LolText("正在删除附加层…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool success = false;
                string message = "";
                try
                {
                    var result = LolAddonCleaner.Delete(root);
                    success = result.Success;
                    message = result.Message;
                    Logger.Log("英雄联盟附加层删除：" + message);
                }
                catch (Exception ex) { message = ex.Message; }
                Interlocked.Exchange(ref lolFileOpBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        lolInspectUtc = DateTime.MinValue;
                        RefreshLolPage();
                        if (!string.IsNullOrEmpty(message))
                            MessageBox.Show(this, message, "Aegis", MessageBoxButtons.OK,
                                success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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
                lblLolAddonStatus.Text = LolText("客户端运行中 · 文件操作已锁定");
                lblLolAddonStatus.ForeColor = Theme.Danger;
                return;
            }
            if (Interlocked.CompareExchange(ref lolFileOpBusy, 0, 0) != 0)
            {
                lblLolAddonStatus.Text = LolText("文件操作进行中…");
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
                lblLolAddonStatus.Text = LolText("检测到占用进程 · 请完全退出客户端");
                lblLolAddonStatus.ForeColor = Theme.Danger;
                return;
            }
            if (lolCandidateCount > 0)
            {
                lblLolAddonStatus.Text = LolText("可删除 ") + lolCandidateCount.ToString()
                    + " · " + CacheSweep.FmtBytes(lolCandidateBytes);
                lblLolAddonStatus.ForeColor = Theme.Green;
                return;
            }
            lblLolAddonStatus.Text = LolText("未发现可删除的附加层");
            lblLolAddonStatus.ForeColor = Theme.Faint;
        }

        private static string LolText(string zh)
        {
            return zh;
        }

        private static string LolPhaseText(string phase)
        {
            string value = phase ?? "";
            if (value.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
                return LolText("对局中");
            if (value.Equals("ChampSelect", StringComparison.OrdinalIgnoreCase))
                return LolText("英雄选择");
            if (value.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase))
                return LolText("匹配中");
            if (value.Equals("ReadyCheck", StringComparison.OrdinalIgnoreCase))
                return LolText("等待接受");
            if (value.Equals("WaitingForStats", StringComparison.OrdinalIgnoreCase))
                return LolText("结算中");
            if (value.Equals("Lobby", StringComparison.OrdinalIgnoreCase))
                return LolText("组队大厅");
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
                return LolText("大厅待命");
            return value;
        }

        private static string LolProcessState(int count, bool zeroIsClean)
        {
            if (count <= 0)
                return zeroIsClean ? LolText("已退出") : LolText("未运行");
            return count.ToString() + LolText(" 个进程");
        }

        private static string LolCompactPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length <= 34) return path;
            return "…" + path.Substring(path.Length - 33);
        }
    }
}
