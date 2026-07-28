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
        private PillButton btnLolQuarantine;
        private PillButton btnLolQuarantineRestore;
        private PillButton btnLolQuarantineDiscard;
        private Label lblLolAction;
        private Label lblLolQuarantineStatus;
        private int lolUiBusy;
        private int lolInspectBusy;
        private int lolQuarantineBusy;
        private DateTime lolInspectUtc;
        private string lolInspectRoot = "";
        private bool lolCanQuarantine;
        private bool lolCanRestoreQuarantine;
        private bool lolCanDiscardQuarantine;
        private string lolActiveSetName = "";
        private int lolMissingCount;
        private bool lolInspectBlocked;
        private long lolCandidateBytes;
        private long lolQuarantinedBytes;
        private int lolCandidateCount;
        private bool lolHasActiveQuarantine;
        private string lolInspectError = "";

        private void BuildLolPage()
        {
            int y = PageHeader(pageLol, LolText("英雄联盟专栏", "League of Legends", "League of Legends 専用"),
                LolText("WeGame 只负责认证与拉起；大厅就绪后精确退出附加进程，对局中关闭 CEF，赛后自动回显客户端。",
                    "WeGame handles sign-in and launch only. Extras exit after lobby readiness; CEF closes in game and returns afterwards.",
                    "WeGame は認証と起動のみ担当。ロビー準備後に付加プロセスを終了し、対局中は CEF を閉じて終了後に復帰します。"), 2);

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
            lolDeck.Title = LolText("英雄联盟 · 极限链路", "LEAGUE · DIRECT PIPELINE", "LEAGUE · ダイレクトパイプライン");
            lolDeck.Subtitle = LolText("不注入、不改内存、不触碰游戏核心文件。",
                "No injection, no memory edits, no game-core file changes.",
                "注入・メモリ改変・ゲーム中核ファイルの変更は行いません。");
            lolDeck.MemoryLabel = LolText("本次累计释放", "TOTAL MEMORY RELEASED", "累積解放メモリ");
            swLolMaster = MakeSwitch(true, OnLolMasterChanged);
            swLolMaster.Bg = Theme.Card;
            swLolMaster.SetBounds(Theme.S(w - 66), Theme.S(10), Theme.S(46), Theme.S(24));
            lolDeck.Controls.Add(swLolMaster);
            scroll.Controls.Add(lolDeck);
            sy += 138;

            int protocolW = (w - 12) / 2;
            swLolCleanup = MakeSwitch(true, OnLolCleanupChanged);
            cardLolCleanup = MakeCard(scroll, x, sy, protocolW, 94,
                LolText("WeGame 借壳启动", "WeGame launch bridge", "WeGame 起動ブリッジ"),
                LolText("允许正常登录与启动；LCU 确认大厅可用后，精确退出 WeGame、Cross 与腾讯附加进程。",
                    "Sign in normally; after LCU confirms the lobby, precisely exit WeGame, Cross, and Tencent extras.",
                    "通常ログイン後、LCU がロビーを確認すると WeGame・Cross・Tencent 付加プロセスを正確に終了します。"),
                swLolCleanup);
            swLolHeadless = MakeSwitch(true, OnLolHeadlessChanged);
            cardLolHeadless = MakeCard(scroll, x + protocolW + 12, sy, protocolW, 94,
                LolText("对局真无头", "True in-game headless", "対局中の完全ヘッドレス"),
                LolText("进入对局后通过客户端原生接口关闭 CEF/UX；游戏结束由守护链自动拉起并回显大厅。",
                    "Closes CEF/UX through the native client endpoint in game; the recovery guard restores the lobby afterwards.",
                    "対局中はクライアント標準 API で CEF/UX を閉じ、終了後に復旧ガードがロビーを再表示します。"),
                swLolHeadless);
            sy += 108;

            Section(scroll, LolText("实时链路状态", "LIVE PIPELINE STATUS", "リアルタイム状態"), x, sy);
            sy += 24;
            int cellGap = 10;
            int cellW = (w - cellGap * 2) / 3;
            int cellH = 64;
            cellLolInstall = MakeLolCell(scroll, x, sy, cellW, cellH,
                LolText("客户端安装", "INSTALLATION", "インストール"), "lol");
            cellLolLcu = MakeLolCell(scroll, x + cellW + cellGap, sy, cellW, cellH,
                LolText("LCU 会话", "LCU SESSION", "LCU セッション"), "settings");
            cellLolPhase = MakeLolCell(scroll, x + (cellW + cellGap) * 2, sy, cellW, cellH,
                LolText("游戏阶段", "GAMEFLOW", "ゲームフロー"), "game");
            sy += cellH + 10;
            cellLolWeGame = MakeLolCell(scroll, x, sy, cellW, cellH, "WEGAME", "white");
            cellLolCross = MakeLolCell(scroll, x + cellW + cellGap, sy, cellW, cellH, "CROSS", "chart");
            cellLolUx = MakeLolCell(scroll, x + (cellW + cellGap) * 2, sy, cellW, cellH, "CEF / UX", "log");
            sy += cellH + 14;

            Section(scroll, LolText("手动指令", "MANUAL COMMANDS", "手動コマンド"), x, sy);
            sy += 24;
            int buttonW = (w - 20) / 3;
            btnLolLaunch = new ColumnActionButton(LolText("启动 WeGame", "Launch WeGame", "WeGame を起動"), BtnKind.Primary);
            btnLolLaunch.SetBounds(Theme.S(x), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolLaunch.Click += OnLolLaunchClick;
            btnLolClean = new ColumnActionButton(LolText("立即净化", "Clean now", "今すぐクリーン"));
            btnLolClean.SetBounds(Theme.S(x + buttonW + 10), Theme.S(sy), Theme.S(buttonW), Theme.S(38));
            btnLolClean.Click += OnLolCleanClick;
            btnLolRestore = new ColumnActionButton(LolText("立即恢复客户端", "Restore client now", "クライアントを復帰"));
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

            Section(scroll, LolText("可逆附加层", "REVERSIBLE OPTIONAL LAYER", "可逆オプション層"), x, sy);
            sy += 24;
            var quarantine = MakeConsolePanel(scroll, x, sy, w, 158, true);
            CardLabel(quarantine, LolText("版本化隔离舱", "VERSIONED QUARANTINE", "バージョン別隔離"), 18, 10, w - 280, 18, 8.2f, true, Theme.Fg);
            CardLabel(quarantine,
                LolText("隔离 AI 教练、iCreate 录制、诊断/网络助手与下载附加层；保留清单，可一键原位恢复。",
                    "Quarantines AI Coach, iCreate recording, diagnostics/network helpers, and download extras with a restorable manifest.",
                    "AI コーチ、iCreate 録画、診断/ネットワーク補助、ダウンロード付加層を復元可能な記録付きで隔離します。"),
                18, 31, w - 290, 42, 7.6f, false, Theme.Dim);
            lblLolQuarantineStatus = CardLabel(quarantine, "", 18, 79, w - 290, 19, 7.2f, false, Theme.Faint);
            btnLolQuarantine = new ColumnActionButton(LolText("隔离附加层", "Quarantine extras", "付加層を隔離"), BtnKind.Danger);
            btnLolQuarantine.SetBounds(Theme.S(w - 254), Theme.S(16), Theme.S(224), Theme.S(36));
            btnLolQuarantine.Click += OnLolQuarantineClick;
            btnLolQuarantineRestore = new ColumnActionButton(LolText("恢复附加层", "Restore extras", "付加層を復元"));
            btnLolQuarantineRestore.SetBounds(Theme.S(w - 254), Theme.S(62), Theme.S(224), Theme.S(36));
            btnLolQuarantineRestore.Click += OnLolQuarantineRestoreClick;
            btnLolQuarantineDiscard = new ColumnActionButton(LolText("丢弃隔离记录", "Discard batch", "隔離記録を破棄"));
            btnLolQuarantineDiscard.SetBounds(Theme.S(w - 254), Theme.S(108), Theme.S(224), Theme.S(36));
            btnLolQuarantineDiscard.Click += OnLolQuarantineDiscardClick;
            quarantine.Controls.AddRange(new Control[]
            {
                btnLolQuarantine, btnLolQuarantineRestore, btnLolQuarantineDiscard
            });

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
                if (Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) == 0) return true;
                Thread.Sleep(50);
                waited += 50;
            }
            return Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) == 0;
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
                ? LolText("专栏已停用", "COLUMN DISABLED", "専用機能は停止中")
                : snapshot.HeadlessActive
                    ? LolText("对局无头运行中", "HEADLESS IN GAME", "対局ヘッドレス実行中")
                    : snapshot.LcuReady
                        ? LolText("链路已接管", "PIPELINE ENGAGED", "パイプライン接続済み")
                        : LolText("等待客户端", "WAITING FOR CLIENT", "クライアント待機中");
            string processText = LolText("累计终止 ", "PROCESSES CLOSED ", "終了プロセス ")
                + snapshot.CleanedProcessCount.ToString();
            lolDeck.SetState(deckState, CacheSweep.FmtBytes(snapshot.ReleasedWorkingSetBytes), processText,
                snapshot.Enabled && (snapshot.LcuReady || snapshot.GameRunning));

            cellLolInstall.SetValue(
                snapshot.InstallationFound ? LolText("已定位", "DETECTED", "検出済み") : LolText("未发现", "NOT FOUND", "未検出"),
                snapshot.InstallationFound ? LolCompactPath(snapshot.LolRoot) : LolText("等待自动扫描", "Awaiting discovery", "自動検出待ち"),
                snapshot.InstallationFound ? Theme.Green : Theme.Danger);
            cellLolLcu.SetValue(
                snapshot.LcuReady ? LolText("已连接", "CONNECTED", "接続済み") : LolText("未连接", "OFFLINE", "未接続"),
                snapshot.ClientRunning ? LolText("LeagueClient 后端在线", "LeagueClient backend online", "LeagueClient バックエンド稼働") :
                    LolText("客户端未运行", "Client is not running", "クライアント未起動"),
                snapshot.LcuReady ? Theme.Green : snapshot.ClientRunning ? Theme.Accent : Theme.Faint);
            cellLolPhase.SetValue(LolPhaseText(snapshot.Phase),
                snapshot.GameRunning ? LolText("游戏进程在线", "Game process online", "ゲームプロセス稼働") :
                    LolText("未进入对局", "No active match", "対局外"),
                snapshot.GameRunning ? Theme.Green : snapshot.LcuReady ? Theme.Accent : Theme.Faint);
            cellLolWeGame.SetValue(LolProcessState(snapshot.WeGameProcessCount, snapshot.ClientRunning),
                LolText("大厅确认后精确退出", "Exits after lobby confirmation", "ロビー確認後に終了"),
                snapshot.WeGameProcessCount == 0 ? Theme.Green : Theme.Accent);
            cellLolCross.SetValue(LolProcessState(snapshot.CrossProcessCount, snapshot.ClientRunning),
                LolText("AI 教练 / 录制 / 覆盖层", "Coach / recorder / overlay", "コーチ / 録画 / オーバーレイ"),
                snapshot.CrossProcessCount == 0 ? Theme.Green : Theme.Accent);
            cellLolUx.SetValue(
                snapshot.HeadlessActive ? LolText("已关闭", "CLOSED", "終了済み") : LolProcessState(snapshot.UxProcessCount, false),
                snapshot.HeadlessActive ? LolText("赛后自动回显", "Restores after the match", "対局後に自動復帰") :
                    snapshot.UxProcessCount > 0 ? LolText("大厅界面可见", "Lobby UX available", "ロビー UI 利用可能") :
                    LolText("客户端未运行", "Client is not running", "クライアント未起動"),
                snapshot.HeadlessActive ? Theme.Green : snapshot.UxProcessCount > 0 ? Theme.Accent : Theme.Faint);

            if (pageLol != null && pageLol.Visible) QueueLolInspection(snapshot.LolRoot);
            bool runtimeBusy = Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0;
            bool fileBusy = Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0;
            bool inspectBusy = Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0;
            bool busy = runtimeBusy || fileBusy;
            bool weGameUp = snapshot.WeGameProcessCount > 0;
            btnLolLaunch.Text = weGameUp
                ? Lang.T("lol.btn.wegamerunning")
                : LolText("启动 WeGame", "Launch WeGame", "WeGame を起動");
            btnLolLaunch.Enabled = !busy && snapshot.WeGameFound && !snapshot.ClientRunning && !weGameUp;
            btnLolClean.Enabled = !busy && snapshot.ClientRunning && snapshot.LcuReady;
            btnLolRestore.Enabled = !busy && snapshot.ClientRunning && (snapshot.HeadlessActive || snapshot.UxProcessCount == 0);
            cardLolCleanup.SetValue(snapshot.CleanupEnabled ? LolText("自动", "AUTO", "自動") : LolText("关闭", "OFF", "オフ"),
                snapshot.CleanupEnabled ? Theme.Green : Theme.Faint);
            cardLolHeadless.SetValue(snapshot.HeadlessEnabled ? LolText("自动", "AUTO", "自動") : LolText("关闭", "OFF", "オフ"),
                snapshot.HeadlessEnabled ? Theme.Green : Theme.Faint);

            string action = !string.IsNullOrEmpty(snapshot.LastError) ? snapshot.LastError : snapshot.LastAction;
            // lastAction 记录的是"刚才做了什么"，用户却会当"现在是什么状态"读。
            // 过了保鲜期就退回默认文案，否则一条过去的成功消息会一直冒充当前状态。
            if (snapshot.LastActionUtc != DateTime.MinValue
                && DateTime.UtcNow - snapshot.LastActionUtc > TimeSpan.FromSeconds(20))
                action = "";
            lblLolAction.Text = string.IsNullOrEmpty(action)
                ? LolText("READY // 等待指令", "READY // awaiting command", "READY // コマンド待機")
                : action;
            lblLolAction.ForeColor = !string.IsNullOrEmpty(snapshot.LastError) ? Theme.Danger : Theme.Dim;

            bool clientBlocksFiles = snapshot.ClientRunning || snapshot.GameRunning;
            btnLolQuarantine.Enabled = !clientBlocksFiles && !busy && !inspectBusy &&
                lolCanQuarantine;
            btnLolQuarantineRestore.Enabled = !clientBlocksFiles && !busy && !inspectBusy &&
                lolCanRestoreQuarantine;
            btnLolQuarantineDiscard.Enabled = !clientBlocksFiles && !busy && !inspectBusy &&
                lolCanDiscardQuarantine;
            UpdateLolQuarantineStatus(clientBlocksFiles);
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
                LolText("正在启动 WeGame…", "Launching WeGame…", "WeGame を起動中…"));
        }

        private void OnLolCleanClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.CleanNow(); },
                LolText("正在执行精准净化…", "Running precise cleanup…", "精密クリーンアップ実行中…"));
        }

        private void OnLolRestoreClick(object sender, EventArgs e)
        {
            RunLolAction(delegate { return lolService.RestoreNow(); },
                LolText("正在恢复客户端界面…", "Restoring client UX…", "クライアント UI を復帰中…"));
        }

        private void RunLolAction(Func<bool> action, string busyText)
        {
            if (action == null || Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0 ||
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
            if (btnLolQuarantine != null) btnLolQuarantine.Enabled = !busy;
            if (btnLolQuarantineRestore != null) btnLolQuarantineRestore.Enabled = !busy;
            if (btnLolQuarantineDiscard != null) btnLolQuarantineDiscard.Enabled = !busy;
        }

        private void OnLolServiceChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (pageLol != null && pageLol.Visible) RefreshLolPage();
                });
            }
            catch { }
        }

        private void QueueLolInspection(string root)
        {
            string normalized = root ?? "";
            if (normalized.Length == 0)
            {
                lolInspectRoot = "";
                lolInspectUtc = DateTime.MinValue;
                lolCanQuarantine = false;
                lolCanRestoreQuarantine = false;
                lolCanDiscardQuarantine = false;
                lolActiveSetName = "";
                lolMissingCount = 0;
                lolHasActiveQuarantine = false;
                lolInspectBlocked = false;
                lolCandidateBytes = 0;
                lolQuarantinedBytes = 0;
                lolCandidateCount = 0;
                lolInspectError = "";
                return;
            }
            bool rootChanged = !string.Equals(normalized, lolInspectRoot, StringComparison.OrdinalIgnoreCase);
            if (!rootChanged &&
                DateTime.UtcNow - lolInspectUtc < TimeSpan.FromMinutes(2)) return;
            if (Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0) return;
            if (Interlocked.CompareExchange(ref lolInspectBusy, 1, 0) != 0) return;
            if (rootChanged)
            {
                lolCanQuarantine = false;
                lolCanRestoreQuarantine = false;
                lolCanDiscardQuarantine = false;
                lolActiveSetName = "";
                lolHasActiveQuarantine = false;
                lolInspectBlocked = false;
                lolCandidateBytes = 0;
                lolQuarantinedBytes = 0;
                lolCandidateCount = 0;
                lolInspectError = "";
                lolInspectUtc = DateTime.MinValue;
            }
            lolInspectRoot = normalized;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool canQuarantine = false;
                bool canRestore = false;
                bool canDiscard = false;
                string activeSetName = "";
                int missingCount = 0;
                bool blocked = false;
                long candidateBytes = 0;
                long quarantinedBytes = 0;
                int candidateCount = 0;
                bool hasActiveQuarantine = false;
                string error = "";
                try
                {
                    var inspection = LolQuarantineManager.Inspect(normalized);
                    canQuarantine = inspection.CanQuarantine;
                    canRestore = inspection.CanRestore;
                    canDiscard = inspection.CanDiscard;
                    if (inspection.Active.Count == 1)
                    {
                        activeSetName = inspection.Active[0].Name;
                        missingCount = inspection.Active[0].MissingCount;
                    }
                    blocked = inspection.IsBlocked;
                    candidateBytes = inspection.CandidateBytes;
                    quarantinedBytes = inspection.QuarantinedBytes;
                    candidateCount = inspection.CandidateCount;
                    hasActiveQuarantine = inspection.Active.Count > 0;
                    error = inspection.Error;
                }
                catch (Exception ex) { error = ex.Message; }
                lolCanQuarantine = canQuarantine;
                lolCanRestoreQuarantine = canRestore;
                lolCanDiscardQuarantine = canDiscard;
                lolActiveSetName = activeSetName ?? "";
                lolMissingCount = missingCount;
                lolInspectBlocked = blocked;
                lolCandidateBytes = candidateBytes;
                lolQuarantinedBytes = quarantinedBytes;
                lolCandidateCount = candidateCount;
                lolHasActiveQuarantine = hasActiveQuarantine;
                lolInspectError = error ?? "";
                lolInspectUtc = DateTime.UtcNow;
                Interlocked.Exchange(ref lolInspectBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!IsDisposed) RefreshLolPage();
                    });
                }
                catch { }
            });
        }

        private void OnLolQuarantineClick(object sender, EventArgs e)
        {
            if (lolService == null) return;
            LolOptimizationSnapshot snapshot = lolService.GetSnapshot();
            if (snapshot.ClientRunning || snapshot.GameRunning || !lolCanQuarantine ||
                Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0) return;
            DialogResult result = MessageBox.Show(this,
                LolText(
                    "将把当前版本识别出的英雄联盟附加层移动到 Aegis 隔离舱。\r\n\r\n范围包括 AI 教练、iCreate 录制、诊断/网络助手与下载组件。英雄联盟核心、登录链路、更新器和游戏文件不会进入隔离清单。\r\n\r\n客户端更新可能重新下载这些组件；隔离清单会保留，可在本页原位恢复。请确认英雄联盟与 WeGame 已完全退出。\r\n\r\n继续隔离吗？",
                    "Move the optional League components detected for this version into the Aegis quarantine.\r\n\r\nThis covers AI Coach, iCreate recording, diagnostic/network helpers, and download extras. League core, sign-in, patching, and game files are excluded.\r\n\r\nA client update may download them again. The manifest is retained for in-place restore. Confirm League and WeGame are fully closed.\r\n\r\nContinue?",
                    "現在のバージョンで検出した League の付加層を Aegis 隔離領域へ移動します。\r\n\r\nAI コーチ、iCreate 録画、診断/ネットワーク補助、ダウンロード部品が対象です。League 中核、認証、更新、ゲームファイルは対象外です。\r\n\r\n更新で再取得される場合があります。復元用記録は保持されます。League と WeGame が完全に終了していることを確認してください。\r\n\r\n続行しますか？"),
                "Aegis", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;
            RunLolQuarantine(snapshot.LolRoot, true);
        }

        private void OnLolQuarantineRestoreClick(object sender, EventArgs e)
        {
            if (lolService == null) return;
            LolOptimizationSnapshot snapshot = lolService.GetSnapshot();
            if (snapshot.ClientRunning || snapshot.GameRunning || !lolCanRestoreQuarantine ||
                Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0) return;
            RunLolQuarantine(snapshot.LolRoot, false);
        }

        private void OnLolQuarantineDiscardClick(object sender, EventArgs e)
        {
            if (lolService == null) return;
            LolOptimizationSnapshot snapshot = lolService.GetSnapshot();
            string setName = lolActiveSetName;
            if (snapshot.ClientRunning || snapshot.GameRunning || !lolCanDiscardQuarantine ||
                string.IsNullOrEmpty(setName) ||
                Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0) return;
            DialogResult result = MessageBox.Show(this,
                LolText(
                    "丢弃这条隔离记录？\r\n\r\n隔离仓中的副本将被删除，原位置的内容保持不变。仅当每一项都已经回到原位置时才可执行——通常是客户端更新重新下载了这些组件，导致恢复无法覆盖。\r\n\r\n丢弃后即可重新隔离。继续吗？",
                    "Discard this quarantine record?\r\n\r\nThe copies inside the quarantine are deleted; whatever sits in the original location is left untouched. This is only offered when every item is already back in place — typically after a client update re-downloaded them, which blocks restore from overwriting.\r\n\r\nQuarantine becomes available again afterwards. Continue?",
                    "この隔離記録を破棄しますか？\r\n\r\n隔離領域内の複製は削除され、元の位置の内容はそのまま残ります。全項目が既に元の位置に存在する場合のみ実行できます（通常はクライアント更新で再取得され、復元が上書きできない状態です）。\r\n\r\n破棄後は再び隔離できます。続行しますか？"),
                "Aegis", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;
            RunLolDiscard(snapshot.LolRoot, setName);
        }

        private void RunLolDiscard(string root, string setName)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(setName) ||
                Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolQuarantineBusy, 1, 0) != 0) return;
            SetLolButtonsBusy(true);
            lblLolQuarantineStatus.ForeColor = Theme.Accent;
            lblLolQuarantineStatus.Text = LolText("正在丢弃隔离记录…", "Discarding the quarantine record…", "隔離記録を破棄中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool success = false;
                string message = "";
                try
                {
                    var result = LolQuarantineManager.Discard(root, setName);
                    success = result.Success;
                    message = result.Message;
                    Logger.Log("英雄联盟隔离记录丢弃：" + message);
                }
                catch (Exception ex) { message = ex.Message; }
                lolInspectUtc = DateTime.MinValue;
                Interlocked.Exchange(ref lolQuarantineBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        RefreshLolPage();
                        if (!string.IsNullOrEmpty(message))
                            MessageBox.Show(this, message, "Aegis", MessageBoxButtons.OK,
                                success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    });
                }
                catch { }
            });
        }

        private void RunLolQuarantine(string root, bool quarantine)
        {
            if (string.IsNullOrEmpty(root) || Interlocked.CompareExchange(ref lolUiBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolInspectBusy, 0, 0) != 0 ||
                Interlocked.CompareExchange(ref lolQuarantineBusy, 1, 0) != 0) return;
            SetLolButtonsBusy(true);
            lblLolQuarantineStatus.ForeColor = Theme.Accent;
            lblLolQuarantineStatus.Text = quarantine
                ? LolText("正在建立隔离清单并移动附加层…", "Building manifest and quarantining extras…", "記録を作成して付加層を隔離中…")
                : LolText("正在按清单原位恢复…", "Restoring from the manifest…", "記録から元の位置へ復元中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool success = false;
                string message = "";
                try
                {
                    var result = quarantine
                        ? LolQuarantineManager.Quarantine(root)
                        : LolQuarantineManager.Restore(root);
                    success = result.Success;
                    message = result.Message;
                    Logger.Log((quarantine ? "英雄联盟附加层隔离：" : "英雄联盟附加层恢复：") + message);
                }
                catch (Exception ex) { message = ex.Message; }
                lolInspectUtc = DateTime.MinValue;
                Interlocked.Exchange(ref lolQuarantineBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        RefreshLolPage();
                        if (!string.IsNullOrEmpty(message))
                            MessageBox.Show(this, message, "Aegis", MessageBoxButtons.OK,
                                success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    });
                }
                catch { }
            });
        }

        private void UpdateLolQuarantineStatus(bool clientBlocksFiles)
        {
            if (lblLolQuarantineStatus == null) return;
            if (clientBlocksFiles)
            {
                lblLolQuarantineStatus.Text = LolText("客户端运行中 · 文件操作已锁定", "CLIENT RUNNING · FILE ACTIONS LOCKED", "クライアント稼働中・ファイル操作ロック");
                lblLolQuarantineStatus.ForeColor = Theme.Danger;
                return;
            }
            if (Interlocked.CompareExchange(ref lolQuarantineBusy, 0, 0) != 0)
            {
                lblLolQuarantineStatus.Text = LolText("文件操作进行中…", "FILE OPERATION IN PROGRESS…", "ファイル操作中…");
                lblLolQuarantineStatus.ForeColor = Theme.Accent;
                return;
            }
            if (!string.IsNullOrEmpty(lolInspectError))
            {
                lblLolQuarantineStatus.Text = lolCanDiscardQuarantine
                    ? LolText("记录已失效 · 可丢弃后重新隔离", "RECORD STALE · DISCARD TO RE-ENABLE", "記録が無効 · 破棄して再隔離できます")
                    : lolInspectError;
                lblLolQuarantineStatus.ForeColor = Theme.Danger;
                return;
            }
            if (lolInspectBlocked)
            {
                lblLolQuarantineStatus.Text = LolText("检测到占用进程 · 请完全退出客户端", "BLOCKING PROCESSES DETECTED", "使用中プロセスを検出");
                lblLolQuarantineStatus.ForeColor = Theme.Danger;
                return;
            }
            if (lolCanRestoreQuarantine)
            {
                lblLolQuarantineStatus.Text = LolText("隔离中 ", "QUARANTINED ", "隔離済み ") + CacheSweep.FmtBytes(lolQuarantinedBytes)
                    + (lolMissingCount > 0
                        ? LolText(" · 缺失 ", " · MISSING ", " · 欠落 ") + lolMissingCount.ToString()
                        : "");
                lblLolQuarantineStatus.ForeColor = Theme.Accent;
                return;
            }
            if (lolHasActiveQuarantine)
            {
                lblLolQuarantineStatus.Text = LolText("检测到活动隔离批次", "ACTIVE QUARANTINE DETECTED", "有効な隔離を検出");
                lblLolQuarantineStatus.ForeColor = Theme.Accent;
                return;
            }
            if (lolCandidateCount > 0)
            {
                lblLolQuarantineStatus.Text = LolText("可隔离 ", "READY ", "隔離可能 ") + lolCandidateCount.ToString() + " · " + CacheSweep.FmtBytes(lolCandidateBytes);
                lblLolQuarantineStatus.ForeColor = Theme.Green;
                return;
            }
            lblLolQuarantineStatus.Text = LolText("未发现可隔离附加层", "NO OPTIONAL LAYERS FOUND", "隔離可能な付加層なし");
            lblLolQuarantineStatus.ForeColor = Theme.Faint;
        }

        private static string LolText(string zh, string en, string ja)
        {
            return Lang.Cur == 1 ? en : Lang.Cur == 2 ? ja : zh;
        }

        private static string LolPhaseText(string phase)
        {
            string value = phase ?? "";
            if (value.Equals("InProgress", StringComparison.OrdinalIgnoreCase))
                return LolText("对局中", "IN GAME", "対局中");
            if (value.Equals("ChampSelect", StringComparison.OrdinalIgnoreCase))
                return LolText("英雄选择", "CHAMP SELECT", "チャンピオン選択");
            if (value.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase))
                return LolText("匹配中", "MATCHMAKING", "マッチング中");
            if (value.Equals("ReadyCheck", StringComparison.OrdinalIgnoreCase))
                return LolText("等待接受", "READY CHECK", "準備確認");
            if (value.Equals("WaitingForStats", StringComparison.OrdinalIgnoreCase))
                return LolText("结算中", "POST GAME", "試合後処理");
            if (value.Equals("Lobby", StringComparison.OrdinalIgnoreCase))
                return LolText("组队大厅", "PARTY LOBBY", "パーティーロビー");
            if (value.Equals("None", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
                return LolText("大厅待命", "LOBBY IDLE", "ロビー待機");
            return value;
        }

        private static string LolProcessState(int count, bool zeroIsClean)
        {
            if (count <= 0)
                return zeroIsClean ? LolText("已退出", "CLEARED", "終了済み") : LolText("未运行", "NOT RUNNING", "未起動");
            return count.ToString() + LolText(" 个进程", " PROCESS" + (count == 1 ? "" : "ES"), " プロセス");
        }

        private static string LolCompactPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length <= 34) return path;
            return "…" + path.Substring(path.Length - 33);
        }
    }
}
