using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private PrivacyOptionsPanel environmentPrivacy;

        private void BuildEnvironmentPrivacy(Control parent)
        {
            PrivacyControl.AdmissionAllowed = delegate {
                return !IsDisposed && !Disposing && !runtimeShutdownBusy && wipeBusy == 0 && !gameMode.IsActive;
            };
            environmentPrivacy = new PrivacyOptionsPanel(delegate {
                return !runtimeShutdownBusy && wipeBusy == 0 && !gameMode.IsActive;
            }, delegate(string message) {
                if (!IsDisposed && !Disposing) PaviseDialog.Warn(this, App.DisplayName, message);
            }, delegate {
                return Settings.LoadCached("MatchPausePcaV1", false);
            }, delegate(bool enabled) {
                bool saved = Settings.SaveDurable("MatchPausePcaV1", enabled)
                    && Settings.Load("MatchPausePcaV1", !enabled) == enabled;
                if (saved) gameMode.Poke();
                return saved;
            }, elevated);
            environmentPrivacy.Name = "environmentPrivacy";
            environmentPrivacy.SetBounds(Theme.S(6), Theme.S(2), Theme.S(ScrollContentW), Theme.S(600));
            parent.Controls.Add(environmentPrivacy);
            environmentPrivacy.SyncCached();
        }

        private void SyncEnvironmentPrivacy()
        {
            if (environmentPrivacy == null || environmentPrivacy.IsDisposed) return;
            environmentPrivacy.SyncCached();
            environmentPrivacy.RefreshIfNeeded();
        }
    }

    // 页面显示后异步核实，失效页面不接收延迟结果。
    internal sealed class PrivacyOptionsPanel : DBPanel
    {
        private sealed class Row
        {
            internal PrivacyAction Action;
            internal Toggle Toggle;
            internal SettingCard Card;
        }
        private readonly List<Row> rows = new List<Row>();
        private readonly Func<bool> mayModify;
        private readonly Action<string> notify;
        private readonly Func<bool> readMatch;
        private readonly Func<bool, bool> saveMatch;
        private readonly bool elevated;
        private readonly Toggle matchPca;
        private readonly SettingCard matchCard;
        private bool busy, loaded, layingOut, refreshing, refreshCancelled;
        private Row pendingRow;
        private bool pendingDesired;
        private int generation;

#if PAVISE_SELFTEST
        internal Func<PrivacyAction, string, PrivacyResult> StateForTest;
        internal Func<PrivacyAction, bool, string, Action, bool> SetForTest;
        internal Func<PrivacyAction, string, Action, bool> RefreshForTest;
        internal Func<bool> InspectionVisibleForTest;
        internal Action<Action> PostForTest;
        internal bool BusyForTest { get { return busy; } }
        internal void RefreshAllForTest() { RefreshAll(); }
#endif

        internal PrivacyOptionsPanel(Func<bool> mayModify, Action<string> notify,
            Func<bool> readMatch, Func<bool, bool> saveMatch, bool elevated)
        {
            this.mayModify = mayModify; this.notify = notify;
            this.readMatch = readMatch; this.saveMatch = saveMatch; this.elevated = elevated;
            AutoScroll = false; BackColor = Theme.Bg;
            foreach (PrivacyAction action in PrivacyActionCatalog.All)
            {
                var row = new Row { Action = action };
                row.Toggle = new Toggle { Name = "privacyToggle" + action.Key, Bg = Theme.Card,
                    AccessibleName = action.Title, AccessibleDescription = action.Detail };
                row.Toggle.Size = new Size(Theme.S(46), Theme.S(24));
                Row captured = row;
                row.Toggle.CheckedChanged += delegate { Change(captured); };
                row.Card = new SettingCard { Name = "privacyCard" + action.Key, Title = action.Title,
                    Desc = action.Detail, Channel = rows.Count + 1, HostTop = true };
                row.Card.Host(row.Toggle);
                Controls.Add(row.Card); rows.Add(row);
            }
            matchPca = new Toggle { Name = "privacyMatchPausePca", Bg = Theme.Card,
                AccessibleName = PrivacyText("对局暂停兼容性助手", "Pause compatibility assistant during matches") };
            matchPca.Size = new Size(Theme.S(46), Theme.S(24));
            matchPca.SetSilently(ReadMatch()); matchPca.CheckedChanged += delegate { ChangeMatch(); };
            matchCard = new SettingCard { Name = "privacyMatchPcaCard", Title = matchPca.AccessibleName,
                Desc = PrivacyText("仅在对局中暂停可停止的 PCA 服务，退局恢复。关闭策略请使用上方独立选项；旧程序的兼容性提示可能暂时不可用。",
                    "Pause the stoppable PCA service during matches and restore afterward. The separate option above changes the persistent policy. Older applications may temporarily lose compatibility prompts."),
                Channel = rows.Count + 1, HostTop = true };
            matchCard.Host(matchPca); Controls.Add(matchCard);
            SyncCached(); Relayout();
        }

        private static string PrivacyText(string zh, string en) { return AutomaticPolicy.Text(zh, en); }
        private bool ReadMatch() { try { return readMatch != null && readMatch(); } catch { return false; } }
        private bool CanModify() { try { return mayModify == null || mayModify(); } catch { return false; } }
        private bool InspectionVisible
        {
            get {
#if PAVISE_SELFTEST
                if (InspectionVisibleForTest != null) return InspectionVisibleForTest();
#endif
                return Visible;
            }
        }
        private PrivacyResult State(Row row)
        {
            string target = "";
#if PAVISE_SELFTEST
            if (StateForTest != null) return StateForTest(row.Action, target);
#endif
            return PrivacyControl.State(row.Action.Key, target);
        }

        internal void SyncCached()
        {
            if (IsDisposed || Disposing) return;
            foreach (Row row in rows)
            {
                PrivacyResult result = State(row);
                bool enabled = result.Configured || result.HasRecovery;
                row.Toggle.SetSilently(row == pendingRow ? pendingDesired : enabled);
                row.Toggle.Busy = busy || result.Busy;
                row.Toggle.Enabled = !busy && !result.Busy && CanModify()
                    && (result.Configured || result.HasRecovery || (result.State == "restored" && !result.Managed) || (result.RetryAllowed && !result.Managed)
                        || (result.Supported && !result.Managed && result.State != "stopped" && result.State != "unverified"));
                string detail = row.Action.Detail;
                if (row.Action.Advanced) detail += "\n" + PrivacyText("高级选项，不进入默认推荐配置。", "Advanced option; excluded from recommended defaults.");
                if (!string.IsNullOrEmpty(result.Detail)) detail += "\n" + result.Detail;
                row.Card.Desc = detail; row.Card.AccessibleDescription = detail;
                bool warning = result.State == "failed" || result.State == "stopped" || result.State == "restore-failed" || result.State == "unverified"
                    || (result.State == "partial" && enabled && !result.Limited) || (result.State == "ready" && result.Configured);
                row.Card.SetStatus(Status(result), warning ? Theme.Warning : enabled ? Theme.Accent : Theme.Dim);
            }
            matchPca.SetSilently(ReadMatch()); matchPca.Enabled = !busy && CanModify(); matchPca.Busy = busy;
            matchCard.SetStatus(matchPca.Checked ? PrivacyText("对局选项已启用", "Match option enabled") : PrivacyText("未启用", "Off"), Theme.Dim);
            Relayout();
        }

        internal static string Status(PrivacyResult value)
        {
            if (value.Busy) return PrivacyText("正在处理", "Working");
            if (value.State == "stopped" && value.RetryAllowed) return PrivacyText("已允许手动重试一次", "One manual retry allowed");
            if (!value.Configured && value.HasRecovery && (value.State == "configured" || value.State == "partial" || value.State == "ready"))
                return PrivacyText("保留恢复记录 · 关闭开关可还原", "Recovery record retained · turn off to restore");
            switch (value.State) {
                case "configured": return value.Configured
                    ? PrivacyText("已开启 · 配置已核实", "On · configuration verified")
                    : PrivacyText("未开启 · 系统已符合此设置", "Off · system already matches");
                case "partial": return value.Configured
                    ? value.Limited ? PrivacyText("已开启 · 适用范围有限", "On · limited scope")
                        : PrivacyText("已开启 · 当前仅部分设置匹配", "On · only some settings match")
                    : value.Limited ? PrivacyText("未开启 · 系统已有相关配置", "Off · existing settings detected")
                        : PrivacyText("未开启 · 系统已有部分限制", "Off · some restrictions already exist");
                case "pending-reboot": case "restart-required": return PrivacyText("配置已保存 · 需重启", "Configuration saved · restart required");
                case "restore-pending-reboot": return PrivacyText("原配置已恢复 · 需重启", "Original configuration restored · restart required");
                case "restored": return PrivacyText("原配置已恢复", "Original configuration restored");
                case "restore-failed": return PrivacyText("恢复未完成 · 原值保留", "Restore incomplete · originals retained");
                case "managed": return PrivacyText("由系统或组织管理", "Managed by Windows or your organization");
                case "unsupported": return UnavailableStatus(value.UnavailableReason);
                case "stopped": return PrivacyText("故障 · 已停用", "Fault · disabled");
                case "failed": return PrivacyText("操作失败 · 原值保留", "Operation failed · originals retained");
                case "off": return PrivacyText("未启用", "Off");
                case "ready": return value.Configured
                    ? PrivacyText("已开启 · 当前设置不匹配", "On · current settings do not match")
                    : PrivacyText("未开启", "Off");
                default: return PrivacyText("暂时无法读取状态", "Unable to read status");
            }
        }

        private static string UnavailableStatus(string reason)
        {
            switch (reason) {
                case "system": return PrivacyText("需要较新的系统", "Requires a newer Windows version");
                case "edition": return PrivacyText("此版本类型不提供该策略", "This Windows edition does not provide this policy");
                case "component": return PrivacyText("系统未安装此组件", "This component is not installed");
                case "external": return PrivacyText("现有配置无法安全还原", "The existing configuration cannot be safely restored");
                case "interface": return PrivacyText("系统未提供可用接口", "No supported system interface is available");
                case "unimplemented": return PrivacyText("此功能尚未开放", "This feature is not yet available");
                default: return PrivacyText("此项目暂不可用", "This option is currently unavailable");
            }
        }

        private void Change(Row row)
        {
            bool desired = row.Toggle.Checked;
            if (busy || IsDisposed || !row.Toggle.Enabled || row.Toggle.Busy || !CanModify()) { SyncCached(); Inform(PrivacyText("请在退局且维护操作结束后修改。", "Change this after leaving the match and completing maintenance.")); return; }
            if (!elevated) { SyncCached(); Inform(Lang.T("env.performance.needadmin")); return; }
            string target = "";
            int ticket = generation;
            pendingRow = row; pendingDesired = desired;
            busy = true; SyncCached();
            Action completed = delegate { Post(ticket, delegate { busy = false; pendingRow = null; SyncCached(); }); };
            bool accepted;
            try {
#if PAVISE_SELFTEST
                if (SetForTest != null) accepted = SetForTest(row.Action, desired, target, completed);
                else
#endif
                    accepted = PrivacyControl.Set(row.Action.Key, desired, target, completed);
            } catch { accepted = false; }
            if (!accepted) { busy = false; pendingRow = null; SyncCached(); Inform(PrivacyText("暂时无法执行，请稍后重试。", "The request could not start. Please retry shortly.")); }
        }

        private void ChangeMatch()
        {
            bool desired = matchPca.Checked;
            bool ok = false;
            if (!busy && CanModify()) try { ok = saveMatch != null && saveMatch(desired); } catch { }
            SyncCached();
            if (!ok) Inform(PrivacyText("对局选项未保存；请退局后重试。", "The match option was not saved. Retry after leaving the match."));
        }

        private void Inform(string message) { if (notify != null && !IsDisposed && !Disposing) notify(message); }

        internal void RefreshIfNeeded()
        { if (!loaded) RefreshAll(); }

        private void RefreshAll()
        {
            if (busy || IsDisposed || Disposing || !IsHandleCreated || !InspectionVisible || !CanModify()) return;
            int ticket = generation; busy = true; refreshing = true; refreshCancelled = false; SyncCached();
            RefreshRow(0, ticket);
        }

        private void FinishRefresh(bool completed)
        {
            bool restart = refreshCancelled && InspectionVisible && CanModify();
            busy = false; refreshing = false; refreshCancelled = false; loaded = completed;
            SyncCached();
            if (restart) RefreshAll();
        }

        private void RefreshRow(int index, int ticket)
        {
            if (ticket != generation || IsDisposed || Disposing) return;
            // 已开始的单项查询收尾后，不再为隐藏页面或对局追加检查。
            if (refreshCancelled || !InspectionVisible || !CanModify()) { FinishRefresh(false); return; }
            if (index >= rows.Count) { FinishRefresh(true); return; }
            Row row = rows[index]; string target = "";
            bool accepted = RefreshOne(row, target, delegate { Post(ticket, delegate { RefreshRow(index + 1, ticket); }); });
            if (!accepted) FinishRefresh(false);
        }

        private bool RefreshOne(Row row, string target, Action completed)
        {
            try {
#if PAVISE_SELFTEST
                if (RefreshForTest != null) return RefreshForTest(row.Action, target, completed);
#endif
                return PrivacyControl.Refresh(row.Action.Key, target, delegate(PrivacyPlan ignored) { completed(); });
            } catch { return false; }
        }

        private void Post(int ticket, Action work)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || ticket != generation) return;
            Action guarded = delegate { if (!IsDisposed && !Disposing && IsHandleCreated && ticket == generation) work(); };
#if PAVISE_SELFTEST
            if (PostForTest != null) { PostForTest(guarded); return; }
#endif
            try { BeginInvoke(guarded); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Visible && !loaded) BeginInvoke((Action)delegate { if (!IsDisposed && Visible && !loaded) RefreshAll(); });
        }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { loaded = false; if (refreshing) refreshCancelled = true; return; }
            if (IsHandleCreated && !busy) RefreshAll();
        }
        protected override void OnHandleDestroyed(EventArgs e)
        { generation++; busy = false; pendingRow = null; loaded = false; refreshing = false; refreshCancelled = false; base.OnHandleDestroyed(e); }
        protected override void Dispose(bool disposing)
        { generation++; base.Dispose(disposing); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Relayout(); }

        private void Relayout()
        {
            if (layingOut || matchCard == null) return;
            layingOut = true;
            try {
                int width = Math.Max(Theme.S(250), Width);
                int y = 0;
                foreach (Row row in rows) {
                    row.Card.SetBounds(0, y, width, CardHeight(row.Card));
                    y = row.Card.Bottom + Theme.S(8);
                }
                matchCard.SetBounds(0, y, width, CardHeight(matchCard));
                Height = matchCard.Bottom + Theme.S(4);
            } finally { layingOut = false; }
        }

        private int CardHeight(SettingCard card)
        {
            int hostWidth = card.Hosted == null ? 0 : card.Hosted.Width + Theme.S(DsMetrics.Card.HostGap);
            int textWidth = Math.Max(1, Width - Theme.S(DsMetrics.Card.PadLeft * 2) - hostWidth);
            int textHeight = TextRenderer.MeasureText(card.Desc ?? "", DsType.Font(DsText.Description),
                new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
            int lineHeight = Math.Max(1, DsType.LineHeight(DsText.Description));
            textHeight = (textHeight + lineHeight - 1) / lineHeight * lineHeight;
            return Math.Max(Theme.S(88), Theme.S(DsMetrics.Card.TitleTop + DsMetrics.Card.TitleHeight
                + SettingCard.StatusLineH + DsMetrics.Card.DescTop + DsMetrics.Card.DescBottom + 5) + textHeight);
        }
    }
}
