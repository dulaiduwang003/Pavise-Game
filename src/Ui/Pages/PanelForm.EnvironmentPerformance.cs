using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private readonly Dictionary<string, Toggle> environmentPerformanceSwitches = new Dictionary<string, Toggle>();
        private readonly Dictionary<string, SettingCard> environmentPerformanceCards = new Dictionary<string, SettingCard>();

        private void BuildEnvironmentPerformanceOptions(Control parent, ref int y)
        {
            environmentPerformanceSwitches.Clear();
            environmentPerformanceCards.Clear();
            AddEnvironmentPerformanceOption(parent, ref y, PolicyCatalog.KeyCacheWarm, "gm.cachewarm", "env.cachewarm.manual");
            SyncEnvironmentPerformanceOptions();
        }

        private void BuildEnvironmentTimerOptions(Control parent, ref int y)
        {
            AddEnvironmentPerformanceOption(parent, ref y, PolicyCatalog.KeyTimerTick, "set.timertick", "env.timertick.manual");
            AddEnvironmentPerformanceOption(parent, ref y, PolicyCatalog.KeyGlobalTimer, "set.gtimer", "env.gtimer.manual");
            SyncEnvironmentPerformanceOptions();
        }

        private void AddEnvironmentPerformanceOption(Control parent, ref int y, string key, string title, string description)
        {
            Toggle toggle = MakeSwitch(AutomaticPolicy.EnvironmentPreference(key), delegate { ChangeEnvironmentPerformanceOption(key); });
            toggle.Name = "environment" + key;
            toggle.AccessibleName = Lang.T(title);
            environmentPerformanceSwitches.Add(key, toggle);
            int height;
            string text = Lang.T(description);
            int contentHeight = FullTextCardHeight(text, ScrollContentW, toggle, 88);
            if (key == PolicyCatalog.KeyGlobalTimer)
            {
                string sample = Lang.F("env.gtimer.readback", "0.5000", "15.6250", "0.5000");
                foreach (string state in new[] { "ready", "timer-locked", "timer-limited", "timer-unverified",
                    "pending-reboot", "restore-pending-reboot", "unverified", "failed", "stopped", "restore-failed",
                    "restored", "unsupported", "timer-requested", "" })
                {
                    contentHeight = Math.Max(contentHeight, FullTextCardHeight(GlobalTimerDescription(state, true, sample), ScrollContentW, toggle, 88));
                    contentHeight = Math.Max(contentHeight, FullTextCardHeight(GlobalTimerDescription(state, false, sample), ScrollContentW, toggle, 88));
                }
            }
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW,
                contentHeight, Lang.T(title), text, toggle, out height);
            card.Height += Theme.S(SettingCard.StatusLineH);
            y += height + SettingCard.StatusLineH + 8;
            environmentPerformanceCards.Add(key, card);
        }

        private void ChangeEnvironmentPerformanceOption(string key)
        {
            Toggle toggle = environmentPerformanceSwitches[key];
            bool previous = AutomaticPolicy.EnvironmentPreference(key);
            if (key != PolicyCatalog.KeyCacheWarm && !RequireElevationFor(toggle, previous)) return;
            bool ok = gameMode.SetEnvironmentPreference(key, toggle.Checked);
            SyncEnvironmentPerformanceOptions();
            if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.savefail"));
        }

        private void SyncEnvironmentPerformanceOptions()
        {
            foreach (var pair in environmentPerformanceSwitches)
            {
                string key = pair.Key;
                bool on = AutomaticPolicy.EnvironmentPreference(key);
                pair.Value.SetSilently(on);
                // 系统不支持时仍允许关闭已启用选项。
                pair.Value.Enabled = key != PolicyCatalog.KeyGlobalTimer || Native.OsBuild() >= 20348 || on || GlobalTimerResTweak.OwnsState;
                SettingCard card = environmentPerformanceCards[key];
                string state = gameMode.AutomaticExecutionState(key);
                string status = key == PolicyCatalog.KeyCacheWarm ? (on ? gameMode.CacheWarmStatus : Lang.T("env.performance.off"))
                    : state == "unverified" ? Lang.T("env.performance.unverified")
                    : state == "pending-reboot" ? Lang.T("env.performance.reboot")
                    : state == "restore-pending-reboot" ? Lang.T("env.performance.restore.reboot")
                    : state == "stopped" ? PerformanceTuning.Label(state)
                    : state == "failed" || state == "restore-failed" ? Lang.T("env.performance.failed")
                    : state == "timer-requested" ? Lang.T("env.performance.requested")
                    : state == "timer-locked" || state == "timer-limited" || state == "timer-unverified"
                        ? GameMode.AutomationStateLabel(state)
                    : state == "ready" ? Lang.T("env.performance.ready")
                    : state == "restored" ? PerformanceTuning.Label(state)
                    : !pair.Value.Enabled || state == "unsupported" ? Lang.T("lock.na")
                    : on ? Lang.T("env.performance.pending") : Lang.T("env.performance.off");
                card.SetStatus(status, state == "failed" || state == "restore-failed" || state == "stopped" ? Theme.Warning : on ? Theme.Accent : Theme.Dim);
                if (key == PolicyCatalog.KeyGlobalTimer)
                {
                    string readback = gameMode.GlobalTimerResolutionDetail;
                    card.Desc = GlobalTimerDescription(state, on, readback);
                    card.AccessibleDescription = card.Desc;
                }
            }
        }

        private static string GlobalTimerDescription(string state, bool on, string readback)
        {
            string explanation;
            switch (state)
            {
                case "ready": explanation = "ready"; break;
                case "timer-locked": explanation = "locked"; break;
                case "timer-limited": explanation = "limited"; break;
                case "timer-unverified": explanation = "query-unverified"; break;
                case "pending-reboot": case "restore-pending-reboot": case "unverified":
                case "failed": case "stopped": case "restore-failed": case "restored": case "unsupported":
                    explanation = state; break;
                case "timer-requested": explanation = "requested"; break;
                default: explanation = on ? "pending" : "off"; break;
            }
            return Lang.T("env.gtimer.manual") + "\n" + Lang.T("env.gtimer.state." + explanation)
                + (!on && !string.IsNullOrEmpty(readback) ? "\n" + Lang.T("env.gtimer.state.release-pending") : "")
                + (string.IsNullOrEmpty(readback) ? "" : "\n" + readback);
        }
    }
}
