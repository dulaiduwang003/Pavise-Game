using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal Action FailureMaintenanceViewForTest;
#endif
        private void BuildRecoveryMaintenance(Control parent, ref int y)
        {
            int height;
            var faults = new PillButton(AutomaticPolicy.Text("查看", "View")) { Name = "manageDisabledActions", Bg = Theme.Card };
            faults.Size = new System.Drawing.Size(Theme.S(112), Theme.S(32));
            faults.Click += delegate { ShowDisabledActions(); };
            string failureDetail = AutomaticPolicy.Text("执行失败立即停用对应动作。这里查看原因，并手动重试一次。", "An execution failure immediately disables that action. View the reason and allow one manual retry here.");
            MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(failureDetail, ScrollContentW, faults, 76),
                AutomaticPolicy.Text("已停用动作", "Disabled actions"), failureDetail, faults, out height);
            y += height + 8;
        }

        private void ShowDisabledActions()
        {
            if (runtimeShutdownBusy || IsDisposed || Disposing) return;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (FailureMaintenanceViewForTest == null) throw new InvalidOperationException("Disabled action view was not mocked");
            FailureMaintenanceViewForTest();
#else
            using (var content = new MaintenanceListPanel(ReadFailureItems, RequestActionRetry))
                PaviseDialog.Show(this, AutomaticPolicy.Text("已停用动作", "Disabled actions"),
                    AutomaticPolicy.Text("恢复始终继续。退局后可手动允许对应动作重试一次。", "Restoration continues. After the match, you can allow one retry for a disabled action."), content, 760);
#endif
        }

        internal static List<MaintenanceListItem> ReadFailureItems()
        {
            var result = new List<MaintenanceListItem>();
            foreach (ActionFailureRecord record in ActionFailureStore.Records())
                result.Add(new MaintenanceListItem { Key = record.Key, Title = MaintenanceActionTitle(record.Action),
                    Retryable = !record.RetryPending && !string.IsNullOrEmpty(record.Action) && !string.IsNullOrEmpty(record.Target),
                    Status = record.PersistenceFailed ? AutomaticPolicy.Text("记录保存受限 · 继续停用", "Record could not be saved · still disabled")
                        : record.RetryPending ? AutomaticPolicy.Text("已允许重试一次", "One retry allowed")
                        : AutomaticPolicy.Text("故障 · 已停用", "Fault · disabled"),
                    Detail = (string.IsNullOrEmpty(record.Target) || record.Target == "global" ? "" : record.Target + "\n")
                        + RecordTime(record.OccurredUtc) + "\n" + record.Reason
                        + (string.IsNullOrEmpty(record.Environment) ? "" : "\n" + record.Environment) });
            return result;
        }

        private string RequestActionRetry(string key)
        {
            if (runtimeShutdownBusy || wipeBusy != 0 || gameMode.IsActive)
                return AutomaticPolicy.Text("请在退出对局且维护操作结束后重试。", "Retry after leaving the match and completing maintenance.");
            ActionFailureRecord selected = null;
            foreach (ActionFailureRecord record in ActionFailureStore.Records())
                if (record.Key == key) { selected = record; break; }
            if (!ActionFailureStore.RequestRetry(key))
                return AutomaticPolicy.Text("暂时不能重试，停用记录已保留。", "A retry could not be allowed. The disabled record is retained.");
            if (selected != null && PrivacyControl.IsAction(selected.Action))
            {
                if (!PrivacyControl.Set(selected.Action, true, "", delegate {
                    try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)SyncEnvironmentPrivacy); } catch (InvalidOperationException) { }
                })) return AutomaticPolicy.Text("重试许可已保留，请在环境的隐私页操作。", "Retry permission is retained; use the Privacy page in Environment.");
            }
            gameMode.Poke();
            return null;
        }

        private static string RecordTime(DateTime utc)
        {
            if (utc == DateTime.MinValue) return AutomaticPolicy.Text("时间未知", "Time unknown");
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string MaintenanceActionTitle(string action)
        {
            PerformanceAction definition = PerformanceActionCatalog.Find(action);
            if (definition != null) return definition.Title;
            PrivacyAction privacy = PrivacyActionCatalog.Find(action);
            if (privacy != null) return privacy.Title;
            foreach (PolicyItem item in PolicyCatalog.All)
                if (string.Equals(item.Key, action, StringComparison.Ordinal)) return Lang.T(item.LangKey);
            string key = action ?? "";
            int separator = key.IndexOf(':');
            if (separator >= 0) key = key.Substring(separator + 1);
            switch (key) {
                case "do": return Lang.T("t.gamemodeenv.1");
                case "wu": return Lang.T("t.gamemodeenv.3");
                case "services": return Lang.T("gm.pausesvc");
                case "standby": return Lang.T("gm.standbycleaner");
                case "pqos": return Lang.T("t.gamemodeenv.4");
                case "awake": return Lang.T("t.gamemodeenv.5");
                case "dwmboost": return Lang.T("gm.dwmboost");
                case "maint": return Lang.T("gm.pausemaint");
                case "nvvrr": return Lang.T("set.nvvrr");
                case "intelend": return Lang.T("set.intel.endurance");
                case "laptopperf": return Lang.T("gm.laptopperf");
                case "gamedvr": case "GameDvrOff": return Lang.T("set.dvr");
                case "mmcss": case "GmMmcss": return Lang.T("auto.mmcss");
                case "power": return AutomaticPolicy.Text("对局电源计划", "Match power plan");
                case "GlobalTimerResolutionLeaseV1": return AutomaticPolicy.Text("对局 0.5 ms 计时器", "Match 0.5 ms timer");
                case "NvDriverProfile": return AutomaticPolicy.Text("NVIDIA 驱动配置", "NVIDIA driver profile");
                case "BackgroundSuppression": return AutomaticPolicy.Text("后台进程资源让位", "Background process resource yielding");
                case "FrameCoordination": return AutomaticPolicy.Text("游戏线程协同", "Game thread coordination");
                case "GamePriority": return AutomaticPolicy.Text("游戏 CPU 优先级", "Game CPU priority");
                case "GameIoPriority": return AutomaticPolicy.Text("游戏磁盘 I/O 优先级", "Game disk I/O priority");
                case "GameGpuPriority": return AutomaticPolicy.Text("游戏 GPU 调度优先级", "Game GPU scheduling priority");
                case "GameEcoQoS": return AutomaticPolicy.Text("游戏节能调度限制解除", "Game power-throttling release");
                default: return string.IsNullOrEmpty(action) ? AutomaticPolicy.Text("优化动作", "Optimization action") : action;
            }
        }
    }
}
