// @author bdth 2074055628@qq.com
// 文件用途 构建系统环境页 集中放置需要重启且会留在机器上的内核与驱动改动
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swHags, swVbs, swGmGuard;
        private Toggle swDevPower, swWindowedOpt, swCfgOff;
        private Toggle swAccessKeys, swHidPower, swSpecMit, swTimerTick, swGlobalTimer;
        private SettingCard cardVbs, cardWindowedOpt, cardSpecMit;
        private SettingCard cardAccessKeys, cardHidPower;
        private TechTabs envTabs;
        private DBPanel[] envTabPanels;
        private int envBusy;

        private void BuildEnvironmentPage()
        {
            int y = PageHeader(pageEnvironment, Lang.T("nav.env"), Lang.T("v16.env.sub"), 2);

            var envBanner = new ModuleBanner();
            envBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            envBanner.Code = "SYSTEM WRITE LAYER // 05";
            envBanner.TitleText = Lang.T("nav.env");
            envBanner.Detail = Lang.T("sec.env.kernel");
            envBanner.State = "RESTART BOUNDARY";
            envBanner.StateColor = Theme.Danger;
            envBanner.Glyph = "chip";
            pageEnvironment.Controls.Add(envBanner);
            y += 84;

            envTabs = new TechTabs();
            envTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            envTabs.SetTabs(
                new[] { Lang.T("env.tab.kernel"), Lang.T("env.tab.device"), Lang.T("env.tab.input") },
                new[] { Lang.T("v17.env.kernel"), Lang.T("v17.env.device"), Lang.T("v17.env.input") });
            pageEnvironment.Controls.Add(envTabs);
            y += 48;

            envTabPanels = MakeTabPanels(pageEnvironment, envTabs, 3, y);

            Control scroll = envTabPanels[0];
            int sy = 2, cardH;

            bool hagsSupported, hagsOnNow;
            HagsTweak.TryQueryState(out hagsSupported, out hagsOnNow);
            swHags = MakeSwitch(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn(), OnHagsToggle);
            swHags.Enabled = hagsSupported || HagsTweak.EnabledByPavise;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"),
                swHags.Enabled ? Lang.T("set.hags.n") : Lang.T("hags.unsupported"), swHags, out cardH);
            sy += cardH + 8;

            swVbs = MakeSwitch(VbsTweak.DisabledByPavise, OnVbsToggle);
            cardVbs = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), " ", swVbs, out cardH);
            sy += cardH + 8;

            swGmGuard = MakeSwitch(GameModeGuard.EnabledByPavise, OnGameModeGuardToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gmguard"), Lang.T("set.gmguard.n"), swGmGuard, out cardH);
            sy += cardH + 8;

            bool win11 = Native.OsBuild() >= 22000;
            swWindowedOpt = MakeSwitch(WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn(), OnWindowedOptToggle);
            swWindowedOpt.Enabled = (win11 || WindowedOptTweak.EnabledByPavise)
                && (WindowedOptTweak.EnabledByPavise || !WindowedOptTweak.CurrentlyOn());
            cardWindowedOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.windowedopt"),
                win11 ? Lang.T("set.windowedopt.n") : Lang.T("windowedopt.oldos"), swWindowedOpt, out cardH);
            sy += cardH + 8;

            swCfgOff = MakeSwitch(CfgOffTweak.Enabled, OnCfgOffToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.cfgoff"), Lang.T("set.cfgoff.n"), swCfgOff, out cardH);
            sy += cardH + 8;

            SpecMitigationTweak.State specSt = SpecMitigationTweak.Query();
            swSpecMit = MakeSwitch(SpecMitigationTweak.DisabledByPavise, OnSpecMitToggle);
            swSpecMit.Enabled = specSt.RecoverableCost || SpecMitigationTweak.DisabledByPavise;
            cardSpecMit = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.specmit"), " ", swSpecMit, out cardH);
            ApplySpecMitState(specSt);
            sy += cardH + 8;

            swTimerTick = MakeSwitch(TimerTickTweak.EnabledByPavise || TimerTickTweak.CurrentlyOn(), OnTimerTickToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.timertick"),
                Lang.T("set.timertick.n"), swTimerTick, out cardH);
            sy += cardH + 8;

            swGlobalTimer = MakeSwitch(GlobalTimerResTweak.EnabledByPavise, OnGlobalTimerToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gtimer"),
                Lang.T("set.gtimer.n"), swGlobalTimer, out cardH);
            sy += cardH + 8;

            scroll = envTabPanels[1]; sy = 2;

            swDevPower = MakeSwitch(DevicePowerTweak.EnabledByPavise, OnDevPowerToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.devpower"), Lang.T("set.devpower.n"), swDevPower, out cardH);
            sy += cardH + 8;

            scroll = envTabPanels[2]; sy = 2;

            swAccessKeys = MakeSwitch(AccessibilityKeysTweak.HasResidue(), OnAccessKeysToggle);
            swAccessKeys.Enabled = AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.EnabledByPavise;
            cardAccessKeys = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.accesskeys"),
                Lang.T("set.accesskeys.n"), swAccessKeys, out cardH);
            sy += cardH + 8;

            swHidPower = MakeSwitch(HidPowerTweak.EnabledByPavise, OnHidPowerToggle);
            cardHidPower = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.hidpower"),
                Lang.T("set.hidpower.n"), swHidPower, out cardH);
            sy += cardH + 8;

            SyncEnvStatus();
            for (int i = 0; i < envTabPanels.Length; i++) EnableCardCollapse(envTabPanels[i]);
        }

        private static Color StatusInk(bool needsAction, bool doneByPavise)
        {
            if (doneByPavise) return Theme.Green;
            return needsAction ? Theme.Accent : Theme.Faint;
        }

        private void SyncEnvStatus()
        {
            if (cardAccessKeys != null)
                cardAccessKeys.SetStatus(AccessibilityKeysTweak.Describe(),
                    StatusInk(AccessibilityKeysTweak.NeedsFix(), AccessibilityKeysTweak.EnabledByPavise));
            if (cardHidPower != null)
                cardHidPower.SetStatus(HidPowerTweak.Describe(),
                    StatusInk(!HidPowerTweak.EnabledByPavise, HidPowerTweak.EnabledByPavise));
            if (cardWindowedOpt != null && Native.OsBuild() >= 22000)
                cardWindowedOpt.SetStatus(WindowedOptTweak.Describe(),
                    StatusInk(!WindowedOptTweak.CurrentlyOn(), WindowedOptTweak.EnabledByPavise));
        }

        private void OnAccessKeysToggle(object s, EventArgs e)
        {
            if (swAccessKeys.Checked) AccessibilityKeysTweak.Enable(); else AccessibilityKeysTweak.Restore();
            swAccessKeys.SetSilently(AccessibilityKeysTweak.HasResidue());
            swAccessKeys.Enabled = AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.HasResidue();
            if (cardAccessKeys != null)
                SyncEnvStatus();
        }

        private void OnWindowedOptToggle(object s, EventArgs e)
        {
            if (swWindowedOpt.Checked) WindowedOptTweak.Enable(); else WindowedOptTweak.Restore();
            swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn());
            swWindowedOpt.Enabled = WindowedOptTweak.EnabledByPavise || !WindowedOptTweak.CurrentlyOn();
            if (cardWindowedOpt != null)
                SyncEnvStatus();
        }

        private void OnHidPowerToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHidPower, HidPowerTweak.EnabledByPavise)) return;
            if (swHidPower.Checked) HidPowerTweak.Enable(); else HidPowerTweak.Restore();
            swHidPower.SetSilently(HidPowerTweak.EnabledByPavise);
            if (cardHidPower != null)
                SyncEnvStatus();
        }

        private void OnDevPowerToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swDevPower, DevicePowerTweak.EnabledByPavise)) return;
            if (swDevPower.Checked) DevicePowerTweak.Enable(); else DevicePowerTweak.Restore();
            swDevPower.SetSilently(DevicePowerTweak.EnabledByPavise);
        }

        private void OnGameModeGuardToggle(object s, EventArgs e)
        {
            if (swGmGuard.Checked) GameModeGuard.Enable(); else GameModeGuard.Restore();
            swGmGuard.SetSilently(GameModeGuard.EnabledByPavise);
        }

        private void OnCfgOffToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swCfgOff, CfgOffTweak.Enabled)) return;
            if (swCfgOff.Checked)
            {
                CfgOffTweak.Enable();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("cfgoff.on"));
            }
            else
            {
                bool ok = CfgOffTweak.Disable();
                PaviseDialog.Info(this, App.DisplayName, Lang.T(ok ? "cfgoff.off" : "cfgoff.restorefail"));
            }
            swCfgOff.SetSilently(CfgOffTweak.Enabled);
        }

        private bool RequireElevationFor(Toggle sw, bool restoredState)
        {
            if (elevated) return true;
            PaviseDialog.Warn(this, App.DisplayName, Lang.T("vbs.needadmin"));
            sw.SetSilently(restoredState);
            return false;
        }

        private void OnGlobalTimerToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swGlobalTimer, GlobalTimerResTweak.EnabledByPavise)) return;
            bool wantOn = swGlobalTimer.Checked;
            bool ok = wantOn ? GlobalTimerResTweak.Enable() : GlobalTimerResTweak.Restore();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T(wantOn ? "gtimer.on" : "gtimer.off"));
            else PaviseDialog.Warn(this, App.DisplayName, Lang.T("gtimer.fail"));
            swGlobalTimer.SetSilently(GlobalTimerResTweak.EnabledByPavise);
        }

        private void OnTimerTickToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swTimerTick, TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn)) return;
            bool wantOn = swTimerTick.Checked;
            bool ok = wantOn ? TimerTickTweak.Enable() : TimerTickTweak.Restore();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T(wantOn ? "timertick.on" : "timertick.off"));
            else PaviseDialog.Warn(this, App.DisplayName, Lang.T("timertick.fail"));
            swTimerTick.SetSilently(TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn);
        }

        private void OnHagsToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHags, HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn())) return;
            bool ok = swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T("hags.reboot"));
            swHags.SetSilently(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn());
        }

        private void OnVbsToggle(object s, EventArgs e)
        {
            if (swVbs.Checked)
            {
                if (!RequireElevationFor(swVbs, false)) return;
                string vbsBlockKey;
                if (VbsTweak.BlockedReason(out vbsBlockKey))
                {
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T(vbsBlockKey));
                    swVbs.SetSilently(false); RefreshVbsState(); return;
                }
                bool agreed = PaviseDialog.Confirm(this, App.DisplayName, Lang.T("vbs.warn"), DlgKind.Warn);
                if (!agreed || !VbsTweak.Disable())
                {
                    swVbs.SetSilently(false); RefreshVbsState(); return;
                }
                RefreshVbsState();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("vbs.done"));
            }
            else
            {
                if (!RequireElevationFor(swVbs, true)) return;
                if (!VbsTweak.Restore())
                {
                    swVbs.SetSilently(VbsTweak.DisabledByPavise);
                    RefreshVbsState();
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T("vbs.restorefail"));
                    return;
                }
                RefreshVbsState();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("vbs.restored"));
            }
        }

        private void RefreshVbsState()
        {
            if (cardVbs == null) return;
            ApplyVbsState(VbsTweak.Query());
        }

        private void ApplyVbsState(VbsTweak.State st)
        {
            if (cardVbs == null) return;
            string key;
            if (VbsTweak.DisabledByPavise && (!st.WmiOk || st.VbsRunning)) key = "vbs.state.pending";
            else if (!st.WmiOk) key = "vbs.state.unknown";
            else if (st.VbsRunning) key = "vbs.state.on";
            else key = "vbs.state.off";
            cardVbs.Desc = Lang.T(key);
        }

        private void OnSpecMitToggle(object s, EventArgs e)
        {
            if (swSpecMit.Checked)
            {
                if (!RequireElevationFor(swSpecMit, false)) return;
                string specBlockKey;
                if (SpecMitigationTweak.BlockedReason(out specBlockKey))
                {
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T(specBlockKey));
                    swSpecMit.SetSilently(false); RefreshSpecMitState(); return;
                }
                bool agreed = PaviseDialog.Confirm(this, App.DisplayName, Lang.T("spec.warn"), DlgKind.Warn);
                if (!agreed || !SpecMitigationTweak.Disable())
                {
                    swSpecMit.SetSilently(false); RefreshSpecMitState(); return;
                }
                RefreshSpecMitState();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("spec.done"));
            }
            else
            {
                if (!RequireElevationFor(swSpecMit, true)) return;
                if (!SpecMitigationTweak.Restore())
                {
                    swSpecMit.SetSilently(SpecMitigationTweak.DisabledByPavise);
                    RefreshSpecMitState();
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T("spec.restorefail"));
                    return;
                }
                RefreshSpecMitState();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("spec.restored"));
            }
        }

        private void RefreshSpecMitState()
        {
            if (cardSpecMit == null) return;
            ApplySpecMitState(SpecMitigationTweak.Query());
        }

        private void ApplySpecMitState(SpecMitigationTweak.State st)
        {
            if (cardSpecMit == null) return;
            string text;
            if (SpecMitigationTweak.DisabledByPavise && (!st.QueryOk || st.RecoverableCost))
                text = Lang.T("spec.state.pending");
            else if (!st.QueryOk) text = Lang.T("spec.state.unknown");
            else if (st.RecoverableCost)
                text = Lang.T("spec.state.on") + SpecMitigationTweak.ActiveCostSummary(st);
            else text = Lang.T("spec.state.off");
            cardSpecMit.Desc = text;
            if (swSpecMit != null)
                swSpecMit.Enabled = st.RecoverableCost || SpecMitigationTweak.DisabledByPavise;
        }

        private void RefreshEnvironmentStateAsync()
        {
            if (!UiActive) return;
            if (Interlocked.Exchange(ref envBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var st = new VbsTweak.State();
                try { st = VbsTweak.Query(); } catch { }
                var specSt = new SpecMitigationTweak.State();
                try { specSt = SpecMitigationTweak.Query(); } catch { }
                try { TimerTickTweak.CurrentlyOn(); } catch { }
                Interlocked.Exchange(ref envBusy, 0);
                if (!UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive) return;
                        if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByPavise);
                        ApplyVbsState(st);
                        if (swSpecMit != null) swSpecMit.SetSilently(SpecMitigationTweak.DisabledByPavise);
                        ApplySpecMitState(specSt);
                        if (swTimerTick != null)
                            swTimerTick.SetSilently(TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn);
                    }));
                }
                catch { }
            });
        }

        private void SyncEnvironmentToggles()
        {
            if (swHags != null) swHags.SetSilently(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn());
            if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByPavise);
            if (swGmGuard != null) swGmGuard.SetSilently(GameModeGuard.EnabledByPavise);
            if (swDevPower != null) swDevPower.SetSilently(DevicePowerTweak.EnabledByPavise);
            if (swAccessKeys != null) swAccessKeys.SetSilently(AccessibilityKeysTweak.EnabledByPavise);
            if (swHidPower != null) swHidPower.SetSilently(HidPowerTweak.EnabledByPavise);
            if (swWindowedOpt != null)
                swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByPavise || WindowedOptTweak.CurrentlyOn());
            if (swCfgOff != null) swCfgOff.SetSilently(CfgOffTweak.Enabled);
            if (swSpecMit != null) swSpecMit.SetSilently(SpecMitigationTweak.DisabledByPavise);
            if (swTimerTick != null)
                swTimerTick.SetSilently(TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn);
            if (swGlobalTimer != null) swGlobalTimer.SetSilently(GlobalTimerResTweak.EnabledByPavise);
        }
    }
}
