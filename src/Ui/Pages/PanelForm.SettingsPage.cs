// @author bdth 2074055628@qq.com
// 文件用途 构建设置页 只放应用自身的偏好与维护工具

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swAuto, swAutoHide;
        private SettingCard cardShader, cardAddon;
        private static volatile bool shaderCleaning;
        private int slowBusy;
        private int restoreBusy;
        private int addonBusy;
        private int wipeBusy;

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

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, AutoHideDefault), OnAutoHideToggle);
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

            var btnWipe = new PillButton(Lang.T("btn.wipe"), BtnKind.Danger);
            btnWipe.Bg = Theme.Card;
            btnWipe.Size = new Size(Theme.S(136), Theme.S(32));
            btnWipe.Click += delegate { OnWipeAll(btnWipe); };
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 78, Lang.T("set.wipe.title"), Lang.T("set.wipe.desc"), btnWipe, out cardH);
            sy += cardH + 8;

            var btnAddonGo = new PillButton(Lang.T("btn.clean"));
            btnAddonGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnAddonGo.Click += delegate { OnAddonClean(btnAddonGo); };
            cardAddon = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("addon.open"), Lang.T("addon.open.sub"), btnAddonGo, out cardH);
            cardAddon.Value = " ";
            sy += cardH + 8;

            var btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean(btnShaderGo);
            cardShader = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo, out cardH);
            cardShader.Value = " ";
            sy += cardH + 18;

            var lblAbout = new Label();
            lblAbout.Text = Lang.F("set.about", App.VersionTag, Paths.Data);
            lblAbout.ForeColor = Theme.Faint; lblAbout.BackColor = Theme.Bg;
            lblAbout.Font = Theme.UI(8.25f, false);
            lblAbout.SetBounds(Theme.S(10), Theme.S(sy), Theme.S(ScrollContentW - 10), Theme.S(18));
            scroll.Controls.Add(lblAbout);

            EnableCardCollapse(scroll);
        }

        private void OnAutoToggle(object s, EventArgs e)
        {
            int rc = swAuto.Checked ? TaskHelper.CreateStartupTask() : TaskHelper.DeleteStartupTask();
            if (rc != 0)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("msg.taskfail"));
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
            if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("shader.confirm"), DlgKind.Warn)) return;
            btn.Enabled = false;
            shaderCleaning = true;
            if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CacheSweep.Result cr = ShaderCache.Clean();
                long left = ShaderCache.MeasureBytes();
                Logger.Log("着色器缓存清理 释放 " + CacheSweep.FmtBytes(cr.FreedBytes)
                    + (cr.FailedFiles > 0 ? " " + cr.FailedFiles + " 个文件被占用已跳过" : ""));
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
                        PaviseDialog.Info(this, App.DisplayName, msg);
                    }));
                }
                catch { }
            });
        }

        private string ResolveAddonRoot()
        {
            foreach (GameProfile profile in gameMode.GetProfiles())
            {
                if (string.IsNullOrEmpty(profile.Root)) continue;
                string root;
                string error;
                if (LolAddonCleaner.TryResolveRoot(profile.Root, out root, out error)) return root;
            }
            return null;
        }

        private static string AddonStateText(LolAddonCleaner.Inspection inspection)
        {
            if (inspection == null || !inspection.IsValidRoot) return Lang.T("addon.hint.fail");
            if (inspection.CandidateCount == 0) return Lang.T("addon.hint.clean");
            return Lang.F("addon.hint.ready",
                inspection.CandidateCount, CacheSweep.FmtBytes(inspection.CandidateBytes));
        }

        private void OnAddonClean(PillButton btn)
        {
            if (Interlocked.Exchange(ref addonBusy, 1) == 1) return;
            btn.Enabled = false;
            if (cardAddon != null) cardAddon.Value = Lang.T("addon.hint.deleting");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string text;
                string root = null;
                try { root = ResolveAddonRoot(); } catch { }
                if (root == null) text = Lang.T("addon.noroot");
                else
                {
                    LolAddonCleaner.OperationResult r = null;
                    try { r = LolAddonCleaner.Delete(root); } catch { }
                    if (r == null) text = Lang.T("addon.hint.fail");
                    else if (r.Success)
                    {
                        text = Lang.F("addon.hint.done", r.DeletedCount, CacheSweep.FmtBytes(r.Bytes));
                        Logger.Log("附加层清理 删除 " + r.DeletedCount + " 项 腾出 " + CacheSweep.FmtBytes(r.Bytes));
                    }
                    else text = r.Message;
                }
                Interlocked.Exchange(ref addonBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        if (cardAddon != null && !cardAddon.IsDisposed) cardAddon.Value = text;
                    });
                }
                catch { }
            });
        }

        private void OnWipeAll(PillButton btn)
        {
            if (gameMode.IsActive)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("wipe.ingame"));
                return;
            }
            if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("wipe.confirm"), DlgKind.Danger)) return;
            if (Interlocked.Exchange(ref wipeBusy, 1) != 0) return;
            btn.Enabled = false;
            Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                int files = 0;
                string unrestored = null;
                try
                {
                    gameMode.PanicRestore();
                    tamer.PanicRestore();
                    ok = LegacyPurge.WipeAll(Paths.Data, true, "手动清除全部配置", out files, out unrestored);
                }
                catch (Exception ex)
                {
                    Logger.LogFailure("手动清除全部配置", ex);
                    unrestored = ex.Message;
                }
                Interlocked.Exchange(ref wipeBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        Cursor = Cursors.Default;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        if (ok)
                        {
                            PaviseDialog.Success(this, App.DisplayName, Lang.F("wipe.done", files));
                            Action exit = ExitApp;
                            if (exit != null) exit();
                        }
                        else PaviseDialog.Warn(this, App.DisplayName, Lang.F("wipe.failed", unrestored ?? ""));
                    });
                }
                catch { }
            });
        }

        private void RefreshSlowStateAsync()
        {
            if (!UiActive) return;
            if (Interlocked.Exchange(ref slowBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool task = false;
                long shaderBytes = -1;
                string addonText = null;
                try { task = TaskHelper.TaskExists(); } catch { }
                try { if (!shaderCleaning) shaderBytes = ShaderCache.MeasureBytes(); } catch { }
                try
                {
                    if (addonBusy == 0)
                    {
                        string addonRoot = ResolveAddonRoot();
                        addonText = addonRoot == null
                            ? Lang.T("addon.noroot")
                            : AddonStateText(LolAddonCleaner.Inspect(addonRoot));
                    }
                }
                catch { }
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
                        if (cardAddon != null && !cardAddon.IsDisposed && addonText != null && addonBusy == 0)
                            cardAddon.Value = addonText;
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
                    Logger.Log("一键全部恢复 已执行 " + attempted
                        + " 项 失败 " + failed + " 项 "
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
                if (!restored) Logger.Log("一键全部恢复 " + name + " 未确认完成");
                return restored;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("一键全部恢复 " + name, ex);
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
                    if (completed) PaviseDialog.Success(this, App.DisplayName, message);
                    else PaviseDialog.Warn(this, App.DisplayName, message);
                    SyncAllToggles();
                }));
            }
            catch { }
        }
    }
}
