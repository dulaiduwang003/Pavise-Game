// @author bdth 2074055628@qq.com
// File purpose Build the System environment page; gathers kernel and driver changes that need a reboot and stay on the machine
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swHags, swVbs, swGmGuard;
        private Toggle swDevPower, swWindowedOpt, swNicIm, swVrrOpt, swEee, swAmdSam, swMemCompress;
        private Toggle swAccessKeys, swHidPower, swSpecMit, swTimerTick, swGlobalTimer;
        private SettingCard cardVbs, cardWindowedOpt, cardSpecMit, cardVrrOpt, cardEee, cardAmdSam, cardHags;
        private SettingCard cardMemCompress;
        private SettingCard cardAccessKeys, cardHidPower, cardNicIm;
        private SettingCard cardGmGuard, cardTimerTick, cardGlobalTimer, cardDevPower;
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
            cardHags = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"),
                swHags.Enabled ? Lang.T("set.hags.n") : Lang.T("hags.unsupported"), swHags, out cardH);
            sy += cardH + 8;

            bool samOk = AdlxTweaks.Available && AmdSamTweak.Supported();
            swAmdSam = MakeSwitch(AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn(), OnAmdSamToggle);
            swAmdSam.Enabled = (samOk || AmdSamTweak.EnabledByPavise)
                && (AmdSamTweak.EnabledByPavise || !AmdSamTweak.CurrentlyOn());
            cardAmdSam = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.amdsam"),
                samOk || AmdSamTweak.EnabledByPavise ? Lang.T("set.amdsam.n") : Lang.T("set.amd.nosup"), swAmdSam, out cardH);
            sy += cardH + 8;

            swVbs = MakeSwitch(VbsTweak.DisabledByPavise, OnVbsToggle);
            cardVbs = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), " ", swVbs, out cardH);
            sy += cardH + 8;

            swGmGuard = MakeSwitch(GameModeGuard.EnabledByPavise, OnGameModeGuardToggle);
            cardGmGuard = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gmguard"), Lang.T("set.gmguard.n"), swGmGuard, out cardH);
            sy += cardH + 8;

            bool win11 = Native.OsBuild() >= 22000;
            // The switch only reflects whether Pavise turned it on; if the system already had it on it stays off; no tier turns it on for the user
            swWindowedOpt = MakeSwitch(WindowedOptTweak.EnabledByPavise, OnWindowedOptToggle);
            swWindowedOpt.Enabled = (win11 || WindowedOptTweak.EnabledByPavise)
                && (WindowedOptTweak.EnabledByPavise || !WindowedOptTweak.CurrentlyOn());
            cardWindowedOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.windowedopt"),
                win11 ? Lang.T("set.windowedopt.n") : Lang.T("windowedopt.oldos"), swWindowedOpt, out cardH);
            sy += cardH + 8;

            bool vrrOs = VrrOptTweak.OsSupported();
            swVrrOpt = MakeSwitch(VrrOptTweak.EnabledByPavise || VrrOptTweak.CurrentlyOn(), OnVrrOptToggle);
            swVrrOpt.Enabled = (vrrOs || VrrOptTweak.EnabledByPavise)
                && (VrrOptTweak.EnabledByPavise || !VrrOptTweak.CurrentlyOn());
            cardVrrOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.vrropt"),
                vrrOs ? Lang.T("set.vrropt.n") : Lang.T("windowedopt.oldos"), swVrrOpt, out cardH);
            sy += cardH + 8;

            SpecMitigationTweak.State specSt = SpecMitigationTweak.Query();
            swSpecMit = MakeSwitch(SpecMitigationTweak.DisabledByPavise, OnSpecMitToggle);
            swSpecMit.Enabled = specSt.RecoverableCost || SpecMitigationTweak.DisabledByPavise;
            cardSpecMit = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.specmit"), " ", swSpecMit, out cardH);
            ApplySpecMitState(specSt);
            sy += cardH + 8;

            swTimerTick = MakeSwitch(TimerTickTweak.EnabledByPavise || TimerTickTweak.CurrentlyOn(), OnTimerTickToggle);
            cardTimerTick = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.timertick"),
                Lang.T("set.timertick.n"), swTimerTick, out cardH);
            sy += cardH + 8;

            swGlobalTimer = MakeSwitch(GlobalTimerResTweak.EnabledByPavise, OnGlobalTimerToggle);
            cardGlobalTimer = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gtimer"),
                Lang.T("set.gtimer.n"), swGlobalTimer, out cardH);
            sy += cardH + 8;

            // On machines with less than 24GB compression is useful and turning it off only adds paging; the switch is disabled outright with an explanation
            bool ramOk = MemCompressTweak.RamEligible();
            swMemCompress = MakeSwitch(MemCompressTweak.EnabledByPavise, OnMemCompressToggle);
            swMemCompress.Enabled = ramOk || MemCompressTweak.EnabledByPavise;
            cardMemCompress = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.memcompress"),
                swMemCompress.Enabled ? Lang.T("set.memcompress.n") : Lang.T("memcompress.smallram"),
                swMemCompress, out cardH);
            sy += cardH + 8;

            // The read-only area attaches to the tail of this page; sy is reused by the next two tabs right away, so save it first
            int kernelTailSy = sy;
            scroll = envTabPanels[1]; sy = 2;

            swDevPower = MakeSwitch(DevicePowerTweak.EnabledByPavise, OnDevPowerToggle);
            cardDevPower = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.devpower"), Lang.T("set.devpower.n"), swDevPower, out cardH);
            sy += cardH + 8;

            swEee = MakeSwitch(EeeTweak.EnabledByPavise, OnEeeToggle);
            cardEee = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.eee"),
                Lang.T("set.eee.n"), swEee, out cardH);
            sy += cardH + 8;

            swNicIm = MakeSwitch(NicModerationTweak.EnabledByPavise, OnNicImToggle);
            cardNicIm = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nicim"),
                Lang.T("set.nicim.n"), swNicIm, out cardH);
            sy += cardH + 8;

            scroll = envTabPanels[2]; sy = 2;

            swAccessKeys = MakeSwitch(AccessibilityKeysTweak.HasResidue(), OnAccessKeysToggle);
            swAccessKeys.Enabled = AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.HasResidue();
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

        // Lock labels on the environment page: Already on in the system when the system already has it on, Not applicable here when there is nothing local to change; same set as the other pages
        private static void LockEnvCard(SettingCard card, Toggle toggle, bool externalOn)
        {
            if (card == null || toggle == null) return;
            card.SetLock(externalOn ? Lang.T("lock.external") : !toggle.Enabled ? Lang.T("lock.na") : "", externalOn);
        }

        private void SyncEnvStatus()
        {
            LockEnvCard(cardHags, swHags, HagsTweak.CurrentlyOn() && !HagsTweak.EnabledByPavise);
            LockEnvCard(cardWindowedOpt, swWindowedOpt, WindowedOptTweak.CurrentlyOn() && !WindowedOptTweak.EnabledByPavise);
            LockEnvCard(cardVrrOpt, swVrrOpt, VrrOptTweak.CurrentlyOn() && !VrrOptTweak.EnabledByPavise);
            LockEnvCard(cardAmdSam, swAmdSam, AdlxTweaks.Available && AmdSamTweak.CurrentlyOn() && !AmdSamTweak.EnabledByPavise);
            LockEnvCard(cardSpecMit, swSpecMit, false);
            LockEnvCard(cardAccessKeys, swAccessKeys, false);
            if (cardAccessKeys != null)
                cardAccessKeys.SetStatus(AccessibilityKeysTweak.Describe(),
                    StatusInk(AccessibilityKeysTweak.NeedsFix(), AccessibilityKeysTweak.EnabledByPavise));
            if (cardHidPower != null)
                cardHidPower.SetStatus(HidPowerTweak.Describe(),
                    StatusInk(!HidPowerTweak.EnabledByPavise, HidPowerTweak.EnabledByPavise));
            if (cardNicIm != null)
            {
                bool nicConfirmed, nicRecovery;
                string nicStatus = NicModerationTweak.Describe(
                    out nicConfirmed, out nicRecovery);
                cardNicIm.SetStatus(nicStatus,
                    StatusInk(nicRecovery, nicConfirmed));
            }
            if (cardWindowedOpt != null && Native.OsBuild() >= 22000)
                cardWindowedOpt.SetStatus(WindowedOptTweak.Describe(),
                    StatusInk(!WindowedOptTweak.CurrentlyOn(), WindowedOptTweak.EnabledByPavise));
            if (cardVrrOpt != null && VrrOptTweak.OsSupported())
                cardVrrOpt.SetStatus(VrrOptTweak.Describe(),
                    StatusInk(!VrrOptTweak.CurrentlyOn(), VrrOptTweak.EnabledByPavise));
            if (cardEee != null)
                // Unchanged is a neutral state; only the pending kind that needs a manual step gets the accent color, otherwise Unchanged looks like an error under the red theme
                cardEee.SetStatus(EeeTweak.Describe(), StatusInk(EeeTweak.Pending, EeeTweak.EnabledByPavise));
            if (cardAmdSam != null && AdlxTweaks.Available)
                cardAmdSam.SetStatus(AmdSamTweak.Describe(),
                    StatusInk(!AmdSamTweak.CurrentlyOn(), AmdSamTweak.EnabledByPavise));
        }

        private void OnAmdSamToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swAmdSam, AmdSamTweak.EnabledByPavise)) return;
            IrqMutationBoundary.Run(delegate
            {
                if (swAmdSam.Checked) AmdSamTweak.Enable();
                else AmdSamTweak.Restore();
            });
            swAmdSam.SetSilently(AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn());
            swAmdSam.Enabled = AmdSamTweak.EnabledByPavise || !AmdSamTweak.CurrentlyOn();
            if (cardAmdSam != null)
                SyncEnvStatus();
        }

        private void OnVrrOptToggle(object s, EventArgs e)
        {
            IrqMutationBoundary.Run(delegate
            {
                if (swVrrOpt.Checked) VrrOptTweak.Enable();
                else VrrOptTweak.Restore();
            });
            swVrrOpt.SetSilently(VrrOptTweak.EnabledByPavise || VrrOptTweak.CurrentlyOn());
            swVrrOpt.Enabled = VrrOptTweak.EnabledByPavise || !VrrOptTweak.CurrentlyOn();
            if (cardVrrOpt != null)
                SyncEnvStatus();
        }

        // Changing the advanced property makes the NIC renegotiate the link, a few seconds of drop; the switch itself is an informed choice, so no more confirmation prompt
        // Fully takes effect only after a reboot; turning off only re-enables what was on before; the receipt lives in MemCompressTweak itself
        private void OnMemCompressToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swMemCompress, MemCompressTweak.EnabledByPavise)) return;
            IrqMutationBoundary.Run(delegate
            {
                if (swMemCompress.Checked) MemCompressTweak.Enable();
                else MemCompressTweak.Restore();
            });
            swMemCompress.SetSilently(MemCompressTweak.EnabledByPavise);
            if (cardMemCompress != null) SyncEnvStatus();
        }

        private void OnEeeToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swEee, EeeTweak.EnabledByPavise)) return;
            IrqMutationBoundary.Run(delegate
            {
                if (swEee.Checked) EeeTweak.Enable();
                else EeeTweak.Restore();
            });
            swEee.SetSilently(EeeTweak.EnabledByPavise);
            if (cardEee != null)
                SyncEnvStatus();
        }

        private void OnAccessKeysToggle(object s, EventArgs e)
        {
            IrqMutationBoundary.Run(delegate
            {
                if (swAccessKeys.Checked) AccessibilityKeysTweak.Enable();
                else AccessibilityKeysTweak.Restore();
            });
            swAccessKeys.SetSilently(AccessibilityKeysTweak.HasResidue());
            swAccessKeys.Enabled = AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.HasResidue();
            if (cardAccessKeys != null)
                SyncEnvStatus();
        }

        private void OnWindowedOptToggle(object s, EventArgs e)
        {
            IrqMutationBoundary.Run(delegate
            {
                if (swWindowedOpt.Checked) WindowedOptTweak.Enable();
                else WindowedOptTweak.Restore();
            });
            swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByPavise);
            swWindowedOpt.Enabled = WindowedOptTweak.EnabledByPavise || !WindowedOptTweak.CurrentlyOn();
            if (cardWindowedOpt != null)
                SyncEnvStatus();
        }

        private void OnHidPowerToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHidPower, HidPowerTweak.EnabledByPavise)) return;
            IrqMutationBoundary.Run(delegate
            {
                if (swHidPower.Checked) HidPowerTweak.Enable();
                else HidPowerTweak.Restore();
            });
            swHidPower.SetSilently(HidPowerTweak.EnabledByPavise);
            if (cardHidPower != null)
                SyncEnvStatus();
        }

        private void OnNicImToggle(object s, EventArgs e)
        {
            if (swNicIm.Checked)
            {
                if (!RequireElevationFor(swNicIm, false)) return;
                if (!PaviseDialog.Confirm(this, Lang.T("set.nicim"),
                        Lang.T("nicim.warn"), DlgKind.Warn))
                {
                    swNicIm.SetSilently(NicModerationTweak.HasResidue());
                    return;
                }
                if (!IrqMutationBoundary.Run<bool>(NicModerationTweak.Enable))
                {
                    swNicIm.SetSilently(NicModerationTweak.HasResidue());
                    SyncEnvStatus();
                    PaviseDialog.Warn(this, Lang.T("set.nicim"), Lang.T("nicim.applyfail"));
                    return;
                }
                swNicIm.SetSilently(NicModerationTweak.HasResidue());
                if (NicModerationTweak.HasResidue())
                    PaviseDialog.Info(this, Lang.T("set.nicim"), Lang.T("nicim.reboot"));
            }
            else
            {
                if (!RequireElevationFor(swNicIm, true)) return;
                if (!IrqMutationBoundary.Run<bool>(NicModerationTweak.Restore))
                {
                    swNicIm.SetSilently(NicModerationTweak.HasResidue());
                    SyncEnvStatus();
                    PaviseDialog.Warn(this, Lang.T("set.nicim"), Lang.T("nicim.restorefail"));
                    return;
                }
                swNicIm.SetSilently(false);
                PaviseDialog.Info(this, Lang.T("set.nicim"), Lang.T("nicim.restored"));
            }
            SyncEnvStatus();
        }

        private void OnDevPowerToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swDevPower, DevicePowerTweak.EnabledByPavise)) return;
            IrqMutationBoundary.Run(delegate
            {
                if (swDevPower.Checked) DevicePowerTweak.Enable();
                else DevicePowerTweak.Restore();
            });
            swDevPower.SetSilently(DevicePowerTweak.EnabledByPavise);
        }

        private void OnGameModeGuardToggle(object s, EventArgs e)
        {
            IrqMutationBoundary.Run(delegate
            {
                if (swGmGuard.Checked) GameModeGuard.Enable();
                else GameModeGuard.Restore();
            });
            swGmGuard.SetSilently(GameModeGuard.EnabledByPavise);
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
            bool ok = IrqMutationBoundary.Run<bool>(delegate
            {
                return wantOn ? GlobalTimerResTweak.Enable() : GlobalTimerResTweak.Restore();
            });
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T(wantOn ? "gtimer.on" : "gtimer.off"));
            else PaviseDialog.Warn(this, App.DisplayName, Lang.T("gtimer.fail"));
            swGlobalTimer.SetSilently(GlobalTimerResTweak.EnabledByPavise);
        }

        private void OnTimerTickToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swTimerTick, TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn)) return;
            bool wantOn = swTimerTick.Checked;
            bool ok = IrqMutationBoundary.Run<bool>(delegate
            {
                return wantOn ? TimerTickTweak.Enable() : TimerTickTweak.Restore();
            });
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T(wantOn ? "timertick.on" : "timertick.off"));
            else PaviseDialog.Warn(this, App.DisplayName, Lang.T("timertick.fail"));
            swTimerTick.SetSilently(TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn);
        }

        private void OnHagsToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHags, HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn())) return;
            bool ok = IrqMutationBoundary.Run<bool>(delegate
            {
                return swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable();
            });
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
                if (!agreed || !IrqMutationBoundary.Run<bool>(VbsTweak.Disable))
                {
                    swVbs.SetSilently(false); RefreshVbsState(); return;
                }
                RefreshVbsState();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("vbs.done"));
            }
            else
            {
                if (!RequireElevationFor(swVbs, true)) return;
                if (!IrqMutationBoundary.Run<bool>(VbsTweak.Restore))
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
                if (!agreed || !IrqMutationBoundary.Run<bool>(SpecMitigationTweak.Disable))
                {
                    swSpecMit.SetSilently(false); RefreshSpecMitState(); return;
                }
                RefreshSpecMitState();
                PaviseDialog.Info(this, App.DisplayName, Lang.T("spec.done"));
            }
            else
            {
                if (!RequireElevationFor(swSpecMit, true)) return;
                if (!IrqMutationBoundary.Run<bool>(SpecMitigationTweak.Restore))
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
            {
                text = Lang.T("spec.state.on") + SpecMitigationTweak.ActiveCostSummary(st);
                // When what is running is a near-free implementation like retpoline or eIBRS, say outright that disabling gains nothing
                //   This used to be the eligibility gate for the Extreme tier forced item; now the user flips the switch themselves, all the more reason to show the conclusion
                if (!SpecMitigationTweak.WorthDisabling(st))
                    text += " " + Lang.T("spec.state.cheap");
            }
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
            if (swAccessKeys != null) swAccessKeys.SetSilently(AccessibilityKeysTweak.HasResidue());
            if (swHidPower != null) swHidPower.SetSilently(HidPowerTweak.EnabledByPavise);
            if (swWindowedOpt != null)
                swWindowedOpt.SetSilently(WindowedOptTweak.EnabledByPavise);
            if (swVrrOpt != null)
                swVrrOpt.SetSilently(VrrOptTweak.EnabledByPavise || VrrOptTweak.CurrentlyOn());
            if (swEee != null) swEee.SetSilently(EeeTweak.EnabledByPavise);
            if (swAmdSam != null) swAmdSam.SetSilently(AmdSamTweak.EnabledByPavise || AmdSamTweak.CurrentlyOn());
            if (swSpecMit != null) swSpecMit.SetSilently(SpecMitigationTweak.DisabledByPavise);
            if (swTimerTick != null)
                swTimerTick.SetSilently(TimerTickTweak.EnabledByPavise || TimerTickTweak.LastKnownOn);
            if (swGlobalTimer != null) swGlobalTimer.SetSilently(GlobalTimerResTweak.EnabledByPavise);
            if (swNicIm != null) swNicIm.SetSilently(NicModerationTweak.EnabledByPavise);
            if (swMemCompress != null)
            {
                swMemCompress.SetSilently(MemCompressTweak.EnabledByPavise);
                // After turning off, machines under 24GB must gray out again; while on, always leave a way to turn it off
                swMemCompress.Enabled = MemCompressTweak.EnabledByPavise || MemCompressTweak.RamEligible();
            }
            SyncEnvStatus();
        }
    }
}
