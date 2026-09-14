// @author bdth 2074055628@qq.com
// File purpose Enhancement area below the League of Legends card in the game library, top row status and commands, bottom row two protocol toggles and add-on layer deletion
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class LolCardPanel : GameCardExtension
    {
        private static int fileOpBusy;
        private readonly LolOptimizationService service;
        private readonly PillButton btnLaunch, btnClean, btnRestore, btnDelete;
        private readonly Toggle swCleanup, swHeadless;
        private readonly System.Windows.Forms.Timer tick;
        private readonly ToolTip tips = new ToolTip();
        private Color surface = Theme.Card;
        private LolOptimizationSnapshot last;
        private int uiBusy, inspectBusy;
        private string inspectRoot = "", inspectError = "";
        private DateTime inspectUtc = DateTime.MinValue;
        private int inspectSignature = -1, candidateCount;
        private long candidateBytes;
        private bool canDelete, inspectBlocked;
        private readonly List<string> blockers = new List<string>();
        private string addonText = "";
        private Color addonColor = Theme.Faint;

        public LolCardPanel(LolOptimizationService svc)
        {
            service = svc;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false; BackColor = surface;
            AccessibleRole = AccessibleRole.Grouping; AccessibleName = "英雄联盟增强";
            tips.InitialDelay = 500; tips.ReshowDelay = 120; tips.AutoPopDelay = 18000; tips.ShowAlways = true;

            btnLaunch = MakeButton(Lang.T("col.btn.launch"), BtnKind.Primary, delegate { OnLaunch(); });
            btnClean = MakeButton(Lang.T("col.btn.clean"), BtnKind.Normal, delegate { RunAction(delegate { return service.CleanNow(); }); });
            btnRestore = MakeButton(Lang.T("col.btn.restore"), BtnKind.Normal, delegate { RunAction(delegate { return service.RestoreNow(); }); });
            btnDelete = MakeButton(Lang.T("col.btn.delete"), BtnKind.Danger, delegate { OnDelete(); });
            tips.SetToolTip(btnDelete, Lang.T("col.addon.tip"));

            swCleanup = MakeToggle(service.CleanupEnabled, delegate { service.CleanupEnabled = swCleanup.Checked; });
            swHeadless = MakeToggle(service.HeadlessEnabled, delegate { service.HeadlessEnabled = swHeadless.Checked; });
            swCleanup.AccessibleName = Lang.T("col.cleanup"); swHeadless.AccessibleName = Lang.T("col.headless");
            tips.SetToolTip(swCleanup, Lang.T("col.cleanup.tip"));
            tips.SetToolTip(swHeadless, Lang.T("col.headless.tip"));

            tick = new System.Windows.Forms.Timer(); tick.Interval = 2000;
            tick.Tick += delegate { if (Visible && FindForm() != null) RefreshState(true); };
            service.Changed += OnServiceChanged;
            Disposed += delegate
            {
                service.Changed -= OnServiceChanged;
                try { tick.Stop(); tick.Dispose(); } catch { }
                try { tips.Dispose(); } catch { }
            };
            RefreshState(false);
        }

        public override int PreferredHeight { get { return Theme.S(98); } }

        public override void SetSurface(Color value)
        {
            surface = value; BackColor = value;
            foreach (Control c in Controls)
            {
                var fx = c as FxControl;
                if (fx != null) { fx.Bg = value; fx.Invalidate(); }
            }
            Invalidate();
        }

        private PillButton MakeButton(string text, BtnKind kind, EventHandler onClick)
        {
            var b = new PillButton(text, kind);
            b.Bg = surface; b.Font = Theme.UI(8.4f, false); b.Height = Theme.S(30);
            b.Width = TextRenderer.MeasureText(text, b.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        private void SetButtonText(PillButton b, string text)
        {
            if (string.Equals(b.Text, text, StringComparison.Ordinal)) return;
            b.Text = text;
            int w = TextRenderer.MeasureText(text, b.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(30);
            if (b.Width != w) { b.Width = w; PerformLayout(); }
            b.Invalidate();
        }

        private static bool IsHelperBlocker(string entry)
        {
            string name = (entry ?? "").Trim();
            int pidAt = name.IndexOf(" PID ", StringComparison.Ordinal);
            if (pidAt > 0) name = name.Substring(0, pidAt);
            name = name.ToLowerInvariant();
            return name.StartsWith("wegame", StringComparison.Ordinal) || name == "pallas" || name == "rail"
                || name == "tcls_core" || name == "crashpad_handler" || name == "browser"
                || name == "teniodl" || name == "wegameupdate";
        }

        private bool HelpersOnlyBlocking()
        {
            if (!inspectBlocked || blockers.Count == 0) return false;
            foreach (string b in blockers) if (!IsHelperBlocker(b)) return false;
            return true;
        }

        private static string BlockerSummary(List<string> list)
        {
            var names = new List<string>();
            foreach (string b in list)
            {
                string name = b ?? "";
                int pidAt = name.IndexOf(" PID ", StringComparison.Ordinal);
                if (pidAt > 0) name = name.Substring(0, pidAt);
                if (!names.Contains(name)) names.Add(name);
                if (names.Count >= 3) break;
            }
            return string.Join(" ", names.ToArray());
        }

        private static void EndHelperProcesses(List<string> list)
        {
            foreach (string entry in list)
            {
                int pidAt = entry.IndexOf(" PID ", StringComparison.Ordinal);
                int pid;
                if (pidAt <= 0 || !int.TryParse(entry.Substring(pidAt + 5).Trim(), out pid)) continue;
                string expected = entry.Substring(0, pidAt).Trim();
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                    {
                        if (!string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                        p.Kill();
                        p.WaitForExit(3000);
                        Logger.Log("英雄联盟增强 结束残留辅助进程 " + entry);
                    }
                }
                catch (Exception ex) { Logger.Log("英雄联盟增强 结束辅助进程失败 " + entry + " " + ex.Message); }
            }
        }

        private Toggle MakeToggle(bool on, EventHandler handler)
        {
            var t = new Toggle();
            t.Size = new Size(Theme.S(42), Theme.S(22)); t.Bg = surface;
            t.SetSilently(on); t.CheckedChanged += handler;
            Controls.Add(t);
            return t;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { tick.Start(); RefreshState(true); } else tick.Stop();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (swHeadless == null || btnDelete == null) return;
            int w = ClientSize.Width, gap = Theme.S(8);
            int rowA = Theme.S(10), rowB = Theme.S(58);
            int x = w;
            foreach (PillButton b in new[] { btnRestore, btnClean, btnLaunch })
            {
                if (!b.Visible) continue;
                x -= b.Width; b.Location = new Point(x, rowA); x -= gap;
            }
            btnDelete.Location = new Point(w - btnDelete.Width, rowB);
            int tx = Theme.S(4);
            if (swCleanup.Visible)
            {
                tx += LabelWidth(Lang.T("col.cleanup")) + Theme.S(8);
                swCleanup.Location = new Point(tx, rowB + Theme.S(4));
                tx += swCleanup.Width + Theme.S(22);
            }
            tx += LabelWidth(Lang.T("col.headless")) + Theme.S(8);
            swHeadless.Location = new Point(tx, rowB + Theme.S(4));
        }

        private static int LabelWidth(string text)
        {
            return TextRenderer.MeasureText(text, Theme.UI(8.2f, true), Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(surface)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Stroke)) g.DrawLine(p, 0, 0, Width, 0);
            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            LolOptimizationSnapshot s = last;
            int rightLimit = Width;
            foreach (PillButton b in new[] { btnLaunch, btnClean, btnRestore })
                if (b.Visible && b.Left < rightLimit) rightLimit = b.Left;
            rightLimit -= Theme.S(14);

            string state; Color stateColor;
            StateOf(s, out state, out stateColor);
            int cy = Theme.S(25);
            int dot = Theme.S(7);
            using (var b = new SolidBrush(stateColor)) g.FillEllipse(b, Theme.S(4), cy - dot / 2, dot, dot);
            Font stateFont = Theme.UI(8.6f, true);
            int stateW = TextRenderer.MeasureText(state, stateFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            var stateRect = new Rectangle(Theme.S(17), cy - Theme.S(10), Math.Min(stateW, Math.Max(1, rightLimit - Theme.S(17))), Theme.S(20));
            TextRenderer.DrawText(g, state, stateFont, stateRect, stateColor, line);

            int x = stateRect.Right + Theme.S(18);
            Font capFont = Theme.UI(7.4f, false), valFont = Theme.UI(7.4f, true);
            if (s != null)
            {
                if (!s.InstallationFound)
                    x = DrawCell(g, x, cy, rightLimit, capFont, valFont, Lang.T("col.cell.install"),
                        s.Discovering ? Lang.T("col.install.wait") : Lang.T("col.install.none"), Theme.Faint);
                x = DrawCell(g, x, cy, rightLimit, capFont, valFont, Lang.T("col.cell.lcu"),
                    s.LcuReady ? Lang.T("col.lcu.on") : Lang.T("col.lcu.off"),
                    s.LcuReady ? Theme.Green : Theme.Faint);
                x = DrawCell(g, x, cy, rightLimit, capFont, valFont, Lang.T("col.cell.phase"),
                    s.ClientRunning ? PhaseText(s.Phase) : Lang.T("col.client.off"),
                    s.GameRunning ? Theme.Green : s.ClientRunning ? Theme.Dim : Theme.Faint);
                DrawCell(g, x, cy, rightLimit, capFont, valFont, Lang.T("col.cell.ux"),
                    s.HeadlessActive ? Lang.T("col.ux.closed") : s.UxProcessCount > 0 ? Lang.T("col.ux.visible") : Lang.T("col.proc.none"),
                    s.HeadlessActive ? Theme.Green : s.UxProcessCount > 0 ? Theme.Dim : Theme.Faint);
            }

            int by = Theme.S(69);
            Font labFont = Theme.UI(8.2f, true);
            if (swCleanup.Visible)
            TextRenderer.DrawText(g, Lang.T("col.cleanup"), labFont,
                new Rectangle(Theme.S(4), by - Theme.S(10), LabelWidth(Lang.T("col.cleanup")) + 2, Theme.S(20)),
                swCleanup.Enabled ? Theme.Fg : Theme.Faint, line);
            int hx = swCleanup.Visible ? swCleanup.Right + Theme.S(22) : Theme.S(4);
            TextRenderer.DrawText(g, Lang.T("col.headless"), labFont,
                new Rectangle(hx, by - Theme.S(10), LabelWidth(Lang.T("col.headless")) + 2, Theme.S(20)),
                swHeadless.Enabled ? Theme.Fg : Theme.Faint, line);
            int ax = swHeadless.Right + Theme.S(18);
            int aw = btnDelete.Left - Theme.S(12) - ax;
            if (btnDelete.Visible && aw > Theme.S(40))
                TextRenderer.DrawText(g, addonText, Theme.UI(7.4f, false),
                    new Rectangle(ax, by - Theme.S(10), aw, Theme.S(20)), addonColor, line | TextFormatFlags.Right);
        }

        private static int DrawCell(Graphics g, int x, int cy, int limit, Font capFont, Font valFont, string cap, string val, Color valColor)
        {
            const TextFormatFlags f = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int cw = TextRenderer.MeasureText(cap, capFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            int vw = TextRenderer.MeasureText(val, valFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (x + cw + Theme.S(5) + vw > limit) return limit;
            TextRenderer.DrawText(g, cap, capFont, new Rectangle(x, cy - Theme.S(9), cw + 2, Theme.S(18)), Theme.Faint, f);
            x += cw + Theme.S(5);
            TextRenderer.DrawText(g, val, valFont, new Rectangle(x, cy - Theme.S(9), vw + 2, Theme.S(18)), valColor, f);
            return x + vw + Theme.S(14);
        }

        private static void StateOf(LolOptimizationSnapshot s, out string text, out Color color)
        {
            if (s == null || !s.Enabled) { text = Lang.T("col.state.off"); color = Theme.Faint; return; }
            if (s.Discovering) { text = Lang.T("col.state.scanning"); color = Theme.Accent; return; }
            if (s.HeadlessActive) { text = Lang.T("col.state.headless"); color = Theme.Green; return; }
            if (s.LcuReady) { text = Lang.T("col.state.linked"); color = Theme.Green; return; }
            text = Lang.T("col.state.wait"); color = Theme.Dim;
        }

        private static string PhaseText(string phase)
        {
            string v = phase ?? "";
            if (v.Equals("InProgress", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.ingame");
            if (v.Equals("ChampSelect", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.champselect");
            if (v.Equals("Matchmaking", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.matchmaking");
            if (v.Equals("ReadyCheck", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.readycheck");
            if (v.Equals("WaitingForStats", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.stats");
            if (v.Equals("Lobby", StringComparison.OrdinalIgnoreCase)) return Lang.T("col.phase.lobby");
            if (v.Equals("None", StringComparison.OrdinalIgnoreCase) || v.Length == 0) return Lang.T("col.phase.idle");
            return v;
        }

        private void OnServiceChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) RefreshState(true); }); }
            catch { }
        }

        private void RefreshState(bool inspectFiles)
        {
            if (IsDisposed) return;
            LolOptimizationSnapshot s;
            try { s = service.GetSnapshot(); }
            catch (Exception ex) { Logger.Log("英雄联盟增强 读取状态失败 " + ex.Message); return; }
            last = s;
            swCleanup.SetSilently(s.CleanupEnabled);
            swHeadless.SetSilently(s.HeadlessEnabled);
            bool off = !s.Enabled;
            bool busy = Interlocked.CompareExchange(ref uiBusy, 0, 0) != 0
                || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0;
            bool locked = busy || off || s.Discovering;
            swCleanup.Enabled = !off; swHeadless.Enabled = !off;
            bool weGameUp = s.WeGameMainRunning;
            bool weGameAvail = s.WeGameFound || s.Discovering;
            if (btnLaunch.Visible != weGameAvail) { btnLaunch.Visible = weGameAvail; PerformLayout(); }
            if (swCleanup.Visible != s.WeGameFound) { swCleanup.Visible = s.WeGameFound; PerformLayout(); }
            if (s.Discovering)
            {
                SetButtonText(btnLaunch, Lang.T("col.btn.cancel")); btnLaunch.Kind = BtnKind.Normal; btnLaunch.Enabled = !busy && !off;
            }
            else
            {
                SetButtonText(btnLaunch, weGameUp ? Lang.T("lol.btn.wegamerunning") : Lang.T("col.btn.launch"));
                btnLaunch.Kind = BtnKind.Primary;
                btnLaunch.Enabled = !locked && s.WeGameFound && !s.ClientRunning && !weGameUp;
            }
            btnClean.Enabled = !locked && s.ClientRunning && s.LcuReady;
            btnRestore.Enabled = !locked && s.ClientRunning && (s.HeadlessActive || s.UxProcessCount == 0);
            if (inspectFiles && !s.GameRunning) QueueInspection(s);
            bool clientBlocksFiles = s.ClientRunning || s.GameRunning;
            bool inspecting = Interlocked.CompareExchange(ref inspectBusy, 0, 0) != 0;
            bool helperOnly = HelpersOnlyBlocking() && candidateCount > 0 && string.IsNullOrEmpty(inspectError);
            bool addonApplicable = !string.Equals(inspectError, Lang.T("lolq.err.notroot"), StringComparison.Ordinal);
            if (btnDelete.Visible != addonApplicable) { btnDelete.Visible = addonApplicable; PerformLayout(); }
            btnDelete.Enabled = !clientBlocksFiles && !locked && !inspecting && (canDelete || helperOnly);
            UpdateAddonStatus(clientBlocksFiles, off, inspecting);
            PerformLayout(); Invalidate();
        }

        private void UpdateAddonStatus(bool clientBlocksFiles, bool off, bool inspecting)
        {
            if (off) { addonText = Lang.T("lol.state.columnoff"); addonColor = Theme.Faint; return; }
            if (clientBlocksFiles) { addonText = Lang.T("col.addon.locked"); addonColor = Theme.Faint; return; }
            if (Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0) { addonText = Lang.T("col.addon.deleting"); addonColor = Theme.Accent; return; }
            if (inspecting && inspectUtc == DateTime.MinValue) { addonText = Lang.T("col.addon.inspecting"); addonColor = Theme.Dim; return; }
            if (string.Equals(inspectError, Lang.T("lolq.err.notroot"), StringComparison.Ordinal))
            {
                addonText = Lang.T("col.addon.clean"); addonColor = Theme.Faint; return;
            }
            if (!string.IsNullOrEmpty(inspectError)) { addonText = inspectError; addonColor = Theme.Danger; return; }
            if (candidateCount == 0 && inspectUtc != DateTime.MinValue) { addonText = Lang.T("col.addon.clean"); addonColor = Theme.Faint; return; }
            if (inspectBlocked)
            {
                bool helperOnly = HelpersOnlyBlocking();
                addonText = blockers.Count == 0 ? Lang.T("col.addon.blocked")
                    : Lang.F(helperOnly ? "col.addon.helpers" : "col.addon.blockedby", BlockerSummary(blockers));
                addonColor = helperOnly ? Theme.Accent : Theme.Danger;
                return;
            }
            if (candidateCount > 0)
            {
                addonText = Lang.F("col.addon.ready", candidateCount, CacheSweep.FmtBytes(candidateBytes));
                addonColor = Theme.Green; return;
            }
            addonText = Lang.T("col.addon.clean"); addonColor = Theme.Faint;
        }

        private void OnLaunch()
        {
            LolOptimizationSnapshot s = last;
            if (s != null && s.Discovering) { service.CancelDiscovery(); RefreshState(false); return; }
            RunAction(delegate { return service.LaunchWeGame(); });
        }

        private void RunAction(Func<bool> action)
        {
            if (action == null || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0
                || Interlocked.CompareExchange(ref uiBusy, 1, 0) != 0) return;
            SetButtonsBusy();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false; string error = "";
                try
                {
                    ok = action();
                    if (!ok) error = service.GetSnapshot().LastError;
                }
                catch (Exception ex) { error = ex.Message; }
                Interlocked.Exchange(ref uiBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        RefreshState(true);
                        if (!ok && !string.IsNullOrEmpty(error)) PaviseDialog.Warn(FindForm(), App.DisplayName, error);
                    });
                }
                catch { }
            });
        }

        private void SetButtonsBusy()
        {
            btnLaunch.Enabled = false; btnClean.Enabled = false; btnRestore.Enabled = false; btnDelete.Enabled = false;
        }

        private void QueueInspection(LolOptimizationSnapshot s)
        {
            string root = (s != null ? s.LolRoot : null) ?? "";
            if (root.Length == 0)
            {
                inspectRoot = ""; inspectUtc = DateTime.MinValue; inspectSignature = -1;
                canDelete = false; inspectBlocked = false; candidateBytes = 0; candidateCount = 0; inspectError = "";
                return;
            }
            bool rootChanged = !string.Equals(root, inspectRoot, StringComparison.OrdinalIgnoreCase);
            int signature = (s.ClientRunning ? 1 : 0) | (s.GameRunning ? 2 : 0)
                | (s.WeGameProcessCount > 0 ? 4 : 0) | (s.UxProcessCount > 0 ? 8 : 0) | (s.CrossProcessCount > 0 ? 16 : 0);
            bool envChanged = signature != inspectSignature;
            TimeSpan maxAge = inspectBlocked && signature == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(2);
            if (!rootChanged && !envChanged && DateTime.UtcNow - inspectUtc < maxAge) return;
            if (Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0) return;
            if (Interlocked.CompareExchange(ref inspectBusy, 1, 0) != 0) return;
            inspectSignature = signature;
            if (rootChanged)
            {
                canDelete = false; inspectBlocked = false; candidateBytes = 0; candidateCount = 0;
                inspectError = ""; inspectUtc = DateTime.MinValue;
            }
            inspectRoot = root;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool can = false, blocked = false; long bytes = 0; int count = 0; string error = "";
                var names = new List<string>();
                try
                {
                    LolAddonCleaner.Inspection r = LolAddonCleaner.Inspect(root);
                    can = r.CanDelete; blocked = r.IsBlocked; bytes = r.CandidateBytes; count = r.CandidateCount; error = r.Error;
                    names.AddRange(r.BlockingProcesses);
                }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        canDelete = can; inspectBlocked = blocked; candidateBytes = bytes; candidateCount = count;
                        blockers.Clear(); blockers.AddRange(names);
                        inspectError = error ?? ""; inspectUtc = DateTime.UtcNow;
                        Interlocked.Exchange(ref inspectBusy, 0);
                        if (!IsDisposed) RefreshState(false);
                    });
                }
                catch { Interlocked.Exchange(ref inspectBusy, 0); }
            });
        }

        private void OnDelete()
        {
            LolOptimizationSnapshot s = service.GetSnapshot();
            bool helperOnly = HelpersOnlyBlocking() && candidateCount > 0 && string.IsNullOrEmpty(inspectError);
            if (s.ClientRunning || s.GameRunning || !(canDelete || helperOnly)
                || Interlocked.CompareExchange(ref uiBusy, 0, 0) != 0
                || Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0
                || Interlocked.CompareExchange(ref inspectBusy, 0, 0) != 0) return;
            string confirm = Lang.T("col.addon.confirm");
            var toEnd = new List<string>();
            if (helperOnly)
            {
                toEnd.AddRange(blockers);
                confirm += Lang.F("col.addon.confirm.helpers", BlockerSummary(toEnd));
            }
            if (!PaviseDialog.Confirm(FindForm(), Lang.T("col.btn.delete"), confirm, DlgKind.Danger)) return;
            string root = s.LolRoot;
            if (string.IsNullOrEmpty(root) || Interlocked.CompareExchange(ref fileOpBusy, 1, 0) != 0) return;
            SetButtonsBusy();
            addonText = Lang.T("col.addon.deleting"); addonColor = Theme.Accent; Invalidate();
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool success = false; string message = "";
                try
                {
                    if (toEnd.Count > 0) EndHelperProcesses(toEnd);
                    LolAddonCleaner.OperationResult result = LolAddonCleaner.Delete(root);
                    success = result.Success; message = result.Message;
                    Logger.Log("英雄联盟附加层删除 " + message);
                }
                catch (Exception ex) { message = ex.Message; }
                Interlocked.Exchange(ref fileOpBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        inspectUtc = DateTime.MinValue;
                        RefreshState(true);
                        if (string.IsNullOrEmpty(message)) return;
                        if (success) PaviseDialog.Info(FindForm(), App.DisplayName, message);
                        else PaviseDialog.Warn(FindForm(), App.DisplayName, message);
                    });
                }
                catch { }
            });
        }

        public static void WaitForFileOps(int waitMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, waitMs));
            while (Interlocked.CompareExchange(ref fileOpBusy, 0, 0) != 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
        }
    }
}
