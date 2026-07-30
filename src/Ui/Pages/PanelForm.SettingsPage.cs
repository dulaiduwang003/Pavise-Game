// @author bdth 2074055628@qq.com
// 文件用途 构建设置页 只放应用自身的偏好与维护工具

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {
        private Toggle swAuto, swAutoHide;
        private SettingCard cardShader;
        private static volatile bool shaderCleaning;
        private int slowBusy;
        private int restoreBusy;

        private void BuildSettingsPage()
        {
            int y = PageHeader(pageSettings, Lang.T("nav.set"), Lang.T("set.hint"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageSettings.Controls.Add(scroll);

            int sy = 2, cardH;
            Section(scroll, Lang.T("sec.app"), 6, sy); sy += 24;

            swAuto = MakeSwitch(TaskHelper.TaskExistsCached(), OnAutoToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autostart"), Lang.T("set.autostart.n"), swAuto, out cardH);
            sy += cardH + 8;

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, false), OnAutoHideToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.autohide"), Lang.T("set.autohide.n"), swAutoHide, out cardH);
            sy += cardH + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.maint"), 6, sy); sy += 24;

            var btnRestore = new PillButton(Lang.T("btn.panic"), BtnKind.Danger);
            btnRestore.Bg = Theme.Card;
            btnRestore.Size = new Size(Theme.S(136), Theme.S(32));
            btnRestore.Click += delegate { RestoreAllNow(); };
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 78, Lang.T("v15.restore.title"), Lang.T("v15.restore.desc"), btnRestore, out cardH);
            sy += cardH + 8;

            var btnDefender = new PillButton(Lang.T("btn.open"), BtnKind.Normal);
            btnDefender.Bg = Theme.Card;
            btnDefender.Size = new Size(Theme.S(120), Theme.S(32));
            btnDefender.Click += delegate
            {
                Cursor = Cursors.WaitCursor;
                DefenderExclusionDialog dlg;
                try { dlg = new DefenderExclusionDialog(gameMode); }
                finally { Cursor = Cursors.Default; }
                using (dlg) dlg.ShowDialog(this);
            };
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("def.open"), Lang.T("def.open.sub"), btnDefender, out cardH);
            sy += cardH + 8;

            var btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean(btnShaderGo);
            cardShader = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo, out cardH);
            cardShader.Value = "…";
            sy += cardH + 18;

            var lblAbout = new Label();
            lblAbout.Text = Lang.F("set.about", App.VersionTag, Paths.Data);
            lblAbout.ForeColor = Theme.Faint; lblAbout.BackColor = Theme.Bg;
            lblAbout.Font = Theme.UI(8.25f, false);
            lblAbout.SetBounds(Theme.S(10), Theme.S(sy), Theme.S(ScrollContentW - 10), Theme.S(18));
            scroll.Controls.Add(lblAbout);
        }

        private void OnAutoToggle(object s, EventArgs e)
        {
            int rc = swAuto.Checked ? TaskHelper.CreateStartupTask() : TaskHelper.DeleteStartupTask();
            if (rc != 0)
            {
                MessageBox.Show(this, Lang.T("msg.taskfail"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swAuto.SetSilently(TaskHelper.TaskExists());
            }
        }

        private void OnShaderClean(PillButton btn)
        {
            if (shaderCleaning)
            {
                if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
                return;
            }
            if (MessageBox.Show(this, Lang.T("shader.confirm"), "Aegis", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            btn.Enabled = false;
            shaderCleaning = true;
            if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CacheSweep.Result cr = ShaderCache.Clean();
                long left = ShaderCache.MeasureBytes();
                Logger.Log("着色器缓存清理：释放 " + CacheSweep.FmtBytes(cr.FreedBytes)
                    + (cr.FailedFiles > 0 ? "，" + cr.FailedFiles + " 个文件被占用已跳过" : ""));
                shaderCleaning = false;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed) return;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        if (cardShader != null && !cardShader.IsDisposed)
                            cardShader.Value = CacheSweep.FmtBytes(left);
                        string msg = Lang.F("shader.freed", CacheSweep.FmtBytes(cr.FreedBytes))
                            + (cr.FailedFiles > 0 ? "\r\n" + Lang.F("shader.skip", cr.FailedFiles) : "")
                            + "\r\n\r\n" + Lang.T("shader.note");
                        MessageBox.Show(this, msg, "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch { }
            });
        }

        // 计划任务查询与着色器缓存测量都会碰磁盘，放后台线程
        private void RefreshSlowStateAsync()
        {
            if (!UiActive) return;
            if (Interlocked.Exchange(ref slowBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool task = false;
                long shaderBytes = -1;
                try { task = TaskHelper.TaskExists(); } catch { }
                try { if (!shaderCleaning) shaderBytes = ShaderCache.MeasureBytes(); } catch { }
                Interlocked.Exchange(ref slowBusy, 0);
                if (!UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive) return;
                        if (swAuto != null) swAuto.SetSilently(task);
                        if (cardShader != null && !shaderCleaning && shaderBytes >= 0)
                            cardShader.Value = CacheSweep.FmtBytes(shaderBytes);
                    }));
                }
                catch { }
            });
        }

        private void RestoreAllNow()
        {
            if (Interlocked.Exchange(ref restoreBusy, 1) != 0) return;
            Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool completed = false;
                int attempted = 0;
                int failed = 0;
                try
                {
                    if (lolService.HasRecordedHeadlessState())
                    {
                        attempted++;
                        if (!TryRestoreRecordedItem(
                                "英雄联盟大厅界面",
                                delegate { return lolService.RestoreNow(); }))
                            failed++;
                    }
                    attempted++;
                    if (!TryRestoreRecordedItem(
                            "游戏模式",
                            delegate { return gameMode.PanicRestore(); }))
                        failed++;
                    attempted++;
                    if (!TryRestoreRecordedItem(
                            "反作弊压制",
                            delegate { return tamer.PanicRestore(); }))
                        failed++;

                    completed = failed == 0;
                    Logger.Log("一键全部恢复：已执行 " + attempted
                        + " 项，失败 " + failed + " 项；"
                        + (completed ? "恢复流程已完成" : "未确认项保留并继续重试"));
                }
                catch (Exception ex)
                {
                    completed = false;
                    attempted++;
                    failed++;
                    Logger.LogFailure("一键全部恢复流程", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref restoreBusy, 0);
                    ShowRestoreAllResult(completed, failed, attempted);
                }
            });
        }

        private static bool TryRestoreRecordedItem(
            string name, Func<bool> restore)
        {
            try
            {
                bool restored = restore != null && restore();
                if (!restored) Logger.Log("一键全部恢复：" + name + " 未确认完成");
                return restored;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("一键全部恢复：" + name, ex);
                return false;
            }
        }

        private void ShowRestoreAllResult(
            bool completed, int failed, int attempted)
        {
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (IsDisposed) return;
                    Cursor = Cursors.Default;
                    string message = Lang.T(
                        completed ? "panic.done" : "panic.timeout");
                    if (!completed)
                        message += "\r\n\r\n" + Lang.F(
                            "panic.failedcount", failed, attempted);
                    MessageBox.Show(
                        this,
                        message,
                        App.DisplayName,
                        MessageBoxButtons.OK,
                        completed
                            ? MessageBoxIcon.Information
                            : MessageBoxIcon.Warning);
                    SyncAllToggles();
                }));
            }
            catch { }
        }
    }
}
