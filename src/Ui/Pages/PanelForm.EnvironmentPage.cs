// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swHags;
        private Toggle swAmdSam;
        private SettingCard cardAmdSam, cardHags;
        private Toggle swWindowedOpt, swVrrOpt, swEnvNvVrr;
        private SettingCard cardWindowedOpt, cardVrrOpt;
        private Toggle swGameLowLatency;
        private TechTabs envTabs;
        private DBPanel[] envTabPanels;

        private void BuildEnvironmentPage()
        {
            int y = PageHeader(pageEnvironment, Lang.T("nav.env"), Lang.T("v16.env.sub"), 2, false);

            envTabs = new TechTabs { Name = "environmentTabs" };
            envTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            envTabs.SetTabs(
                new[] { Lang.T("env.tab.performance"), Lang.T("env.tab.latency"), Lang.T("env.tab.graphics"), AutomaticPolicy.Text("隐私与后台", "Privacy & background") },
                new[] { Lang.T("env.tab.performance.h"), Lang.T("env.tab.latency.h"), Lang.T("env.tab.graphics.h"), AutomaticPolicy.Text("逐项核实，随时恢复", "Verify individually and restore") });
            pageEnvironment.Controls.Add(envTabs);
            y += 48;
            envTabPanels = MakeTabPanels(pageEnvironment, envTabs, 4, y);

            Control scroll = envTabPanels[0];
            int sy = 2, cardH;

            BuildPerformanceTuningEnvironment(scroll, ref sy);
            BuildEnvironmentPerformanceOptions(scroll, ref sy);
            BuildEnvironmentVisualEffects(scroll, ref sy);
            BuildStandbyCleanerPolicyCard(scroll, ref sy);
            RefreshStandbyCleanerPresentation();
            EnableCardCollapse(scroll);

            scroll = envTabPanels[1]; sy = 2;
            BuildEnvironmentTimerOptions(scroll, ref sy);

            swGameLowLatency = MakeSwitch(AutomaticPolicy.GameLowLatency, OnGameLowLatencyToggle);
            swGameLowLatency.Name = "environmentGameLowLatency";
            swGameLowLatency.AccessibleName = Lang.T("set.lowlatency");
            SettingCard lowLatency = MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("set.lowlatency.n"), ScrollContentW, swGameLowLatency, 76), Lang.T("set.lowlatency"),
                Lang.T("set.lowlatency.n"), swGameLowLatency, out cardH);
            sy += cardH + 8;

            EnableCardCollapse(scroll, lowLatency);
            scroll = envTabPanels[2]; sy = 2;

            bool hagsSupported, hagsOnNow;
            HagsTweak.TryQueryState(out hagsSupported, out hagsOnNow);
            swHags = MakeSwitch(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn(), OnHagsToggle);
            swHags.Enabled = hagsSupported || HagsTweak.EnabledByPavise;
            cardHags = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"),
                swHags.Enabled ? Lang.T("set.hags.n") : Lang.T("hags.unsupported"), swHags, out cardH);
            sy += cardH + 8;

            swWindowedOpt = MakeSwitch(false, delegate { ChangeDirectXPreference(DirectXGlobalPreference.Windowed, swWindowedOpt); });
            cardWindowedOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("env.windowed.n"), ScrollContentW, swWindowedOpt, 88),
                Lang.T("env.windowed"), Lang.T("env.windowed.n"), swWindowedOpt, out cardH);
            sy += cardH + 8;

            swVrrOpt = MakeSwitch(false, delegate { ChangeDirectXPreference(DirectXGlobalPreference.Vrr, swVrrOpt); });
            cardVrrOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("env.vrr.n"), ScrollContentW, swVrrOpt, 88),
                Lang.T("env.vrr"), Lang.T("env.vrr.n"), swVrrOpt, out cardH);
            sy += cardH + 8;

            swEnvNvVrr = MakeSwitch(gameMode.NvVrrWindowedEnabled, delegate
            {
                bool requested = swEnvNvVrr.Checked;
                gameMode.NvVrrWindowedEnabled = requested;
                swEnvNvVrr.SetSilently(gameMode.NvVrrWindowedEnabled);
                if (requested != swEnvNvVrr.Checked)
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.graphics.failed"));
            });
            swEnvNvVrr.Enabled = NvVrrWindowed.Applicable() || gameMode.NvVrrWindowedEnabled || NvVrrWindowed.HasResidue;
            string nvDetail = Lang.T("set.nvvrr.n") + "\n" + Lang.T("env.nvvrr.manual");
            if (!swEnvNvVrr.Enabled) nvDetail += "\n" + Lang.T("set.nvvrr.na");
            MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(nvDetail, ScrollContentW, swEnvNvVrr, 88),
                Lang.T("set.nvvrr"), nvDetail, swEnvNvVrr, out cardH);
            sy += cardH + 8;

            bool samOk = AdlxTweaks.Available && AmdSamTweak.Supported();
            swAmdSam = MakeSwitch(AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn(), OnAmdSamToggle);
            swAmdSam.Enabled = (samOk || AmdSamTweak.EnabledByPavise)
                && (AmdSamTweak.EnabledByPavise || !AmdSamTweak.CurrentlyOn());
            cardAmdSam = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.amdsam"),
                samOk || AmdSamTweak.EnabledByPavise ? Lang.T("set.amdsam.n") : Lang.T("set.amd.nosup"), swAmdSam, out cardH);
            sy += cardH + 8;

            BuildEnvironmentRecordingOption(scroll, ref sy);
            SyncEnvStatus();
            EnableCardCollapse(scroll);
            BuildEnvironmentPrivacy(envTabPanels[3]);
        }

        private void OnGameLowLatencyToggle(object sender, EventArgs e)
        {
            if (gameMode.SetGameLowLatency(swGameLowLatency.Checked)) return;
            swGameLowLatency.SetSilently(AutomaticPolicy.GameLowLatency);
            PaviseDialog.Warn(this, App.DisplayName, Lang.T("set.lowlatency.savefail"));
        }

        private static Color StatusInk(bool needsAction, bool doneByPavise)
        {
            if (doneByPavise) return Theme.Green;
            return needsAction ? Theme.Accent : Theme.Faint;
        }

        private static void LockEnvCard(SettingCard card, Toggle toggle, bool externalOn)
        {
            if (card == null || toggle == null) return;
            card.SetLock(externalOn ? Lang.T("lock.external") : !toggle.Enabled ? Lang.T("lock.na") : "", externalOn);
        }

        private void SyncEnvStatus()
        {
            SyncDirectXPreference(DirectXGlobalPreference.Windowed, cardWindowedOpt, swWindowedOpt);
            SyncDirectXPreference(DirectXGlobalPreference.Vrr, cardVrrOpt, swVrrOpt);
            LockEnvCard(cardHags, swHags, HagsTweak.CurrentlyOn() && !HagsTweak.EnabledByPavise);
            LockEnvCard(cardAmdSam, swAmdSam, AdlxTweaks.Available && AmdSamTweak.CurrentlyOn() && !AmdSamTweak.EnabledByPavise);
            if (cardAmdSam != null && AdlxTweaks.Available)
                cardAmdSam.SetStatus(AmdSamTweak.Describe(),
                    StatusInk(!AmdSamTweak.CurrentlyOn(), AmdSamTweak.EnabledByPavise));
        }

        private void ChangeDirectXPreference(DirectXGlobalPreference preference, Toggle toggle)
        {
            bool ok = toggle.Checked ? preference.Enable() : preference.Restore();
            Logger.Log("Environment graphics " + (preference == DirectXGlobalPreference.Windowed ? "windowed" : "VRR")
                + " request=" + toggle.Checked + " success=" + ok);
            SyncEnvStatus();
            if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.graphics.failed"));
        }

        private static void SyncDirectXPreference(DirectXGlobalPreference preference, SettingCard card, Toggle toggle)
        {
            if (card == null || toggle == null) return;
            bool on;
            bool readable = preference.TryReadState(out on);
            bool owned = preference.HasResidue;
            toggle.SetSilently(on || owned);
            toggle.Enabled = owned || (readable && preference.Supported && !on);
            LockEnvCard(card, toggle, on && !owned);
            card.SetStatus(Lang.T(!readable ? "env.graphics.unknown" : owned ? "env.graphics.owned"
                : !preference.Supported ? "lock.na" : on ? "lock.external" : "env.graphics.off"),
                owned ? Theme.Green : Theme.Dim);
        }

        private void OnAmdSamToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swAmdSam, AmdSamTweak.EnabledByPavise)) return;
            if (swAmdSam.Checked) AmdSamTweak.Enable();
            else AmdSamTweak.Restore();
            swAmdSam.SetSilently(AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn());
            swAmdSam.Enabled = AmdSamTweak.EnabledByPavise || !AmdSamTweak.CurrentlyOn();
            if (cardAmdSam != null)
                SyncEnvStatus();
        }

        private bool RequireElevationFor(Toggle sw, bool restoredState)
        {
            if (elevated) return true;
            PaviseDialog.Warn(this, App.DisplayName, Lang.T("env.performance.needadmin"));
            sw.SetSilently(restoredState);
            return false;
        }

        private void OnHagsToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHags, HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn())) return;
            bool ok = swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T("hags.reboot"));
            swHags.SetSilently(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn());
        }

        private void RefreshEnvironmentStateAsync()
        {
            if (UiActive) SyncEnvironmentToggles();
        }

        private void SyncEnvironmentToggles()
        {
            SyncEnvironmentPerformanceOptions();
            SyncPerformanceTuningEnvironment();
            RefreshStandbyCleanerPresentation();
            SyncEnvironmentGameDvr();
            SyncEnvironmentVisualEffects();
            SyncEnvironmentPrivacy();
            if (swGameLowLatency != null) swGameLowLatency.SetSilently(AutomaticPolicy.GameLowLatency);
            if (swHags != null) swHags.SetSilently(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn());
            if (swAmdSam != null) swAmdSam.SetSilently(AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn());
            if (swEnvNvVrr != null) swEnvNvVrr.SetSilently(gameMode.NvVrrWindowedEnabled);
            SyncEnvStatus();
        }
    }
}
