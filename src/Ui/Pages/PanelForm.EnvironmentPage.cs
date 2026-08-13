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
        private Toggle swHags, swVbs, swIrqAffinity, swUsbAffinity, swGmGuard;
        private Toggle swNetThrottle, swDevPower, swQuantum, swClock;
        private Toggle swAccessKeys, swHidPower, swInputQueue, swPointerPrec;
        private SettingCard cardVbs, cardNetThrottle, cardQuantum, cardClock;
        private SettingCard cardAccessKeys, cardHidPower, cardInputQueue, cardPointerPrec;
        private TechTabs envTabs;
        private DBPanel[] envTabPanels;
        private int envBusy;

        private void BuildEnvironmentPage()
        {
            int y = PageHeader(pageEnvironment, Lang.T("nav.env"), Lang.T("v16.env.sub"), 2);

            var warn = new RoundPanel();
            warn.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(46));
            warn.BackColor = Theme.Bg; warn.Fill = Theme.Card; warn.Border = Theme.Stroke;
            warn.Radius = Theme.S(12); warn.AccentEdge = true;
            CardLabel(warn, Lang.T("sec.env.kernel"), 18, 14, ContentW - 36, 20, 8.2f, true, Theme.Danger);
            pageEnvironment.Controls.Add(warn);
            y += 58;

            envTabs = new TechTabs();
            envTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            envTabs.SetTabs(
                new[] { Lang.T("env.tab.kernel"), Lang.T("env.tab.device"), Lang.T("env.tab.input") },
                new[] { Lang.T("v17.env.kernel"), Lang.T("v17.env.device"), Lang.T("v17.env.input") });
            pageEnvironment.Controls.Add(envTabs);
            y += 48;

            envTabPanels = new DBPanel[3];
            for (int i = 0; i < envTabPanels.Length; i++)
            {
                var panel = new DBPanel();
                panel.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
                panel.BackColor = Theme.Bg; panel.AutoScroll = true; Native.Dark(panel);
                panel.Visible = i == 0;
                pageEnvironment.Controls.Add(panel);
                envTabPanels[i] = panel;
            }
            envTabs.IndexChanged = delegate(int index)
            {
                for (int i = 0; i < envTabPanels.Length; i++)
                {
                    if (i != index) { Fx.Settle(envTabPanels[i]); envTabPanels[i].Visible = false; }
                }
                envTabPanels[index].Visible = true;
                Fx.SlideIn(envTabPanels[index]);
            };

            Control scroll = envTabPanels[0];
            int sy = 2, cardH;

            swHags = MakeSwitch(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn(), OnHagsToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), Lang.T("set.hags.n"), swHags, out cardH);
            sy += cardH + 8;

            swVbs = MakeSwitch(VbsTweak.DisabledByPavise, OnVbsToggle);
            cardVbs = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), " ", swVbs, out cardH);
            sy += cardH + 8;

            swGmGuard = MakeSwitch(GameModeGuard.EnabledByPavise, OnGameModeGuardToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gmguard"), Lang.T("set.gmguard.n"), swGmGuard, out cardH);
            sy += cardH + 8;

            swQuantum = MakeSwitch(QuantumTweak.RepairedByPavise, OnQuantumToggle);
            swQuantum.Enabled = QuantumTweak.NeedsRepair() || QuantumTweak.RepairedByPavise;
            cardQuantum = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76,
                Lang.T("set.quantum"), Lang.T("set.quantum.n"), swQuantum, out cardH);
            sy += cardH + 8;

            swClock = MakeSwitch(PlatformClockTweak.RepairedByPavise, OnClockToggle);
            swClock.Enabled = PlatformClockTweak.NeedsRepair() || PlatformClockTweak.RepairedByPavise;
            cardClock = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76,
                Lang.T("set.clock"), Lang.T("set.clock.n"), swClock, out cardH);
            sy += cardH + 8;

            scroll = envTabPanels[1]; sy = 2;

            bool discreteGpu = GpuInventory.HasDiscrete;
            swIrqAffinity = MakeSwitch(InterruptAffinityTweak.EnabledByPavise, OnIrqAffinityToggle);
            swIrqAffinity.Enabled = discreteGpu || InterruptAffinityTweak.EnabledByPavise;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.irqaffinity"),
                discreteGpu ? Lang.T("set.irqaffinity.n") : Lang.T("irqaffinity.igpuonly"),
                swIrqAffinity, out cardH);
            sy += cardH + 8;

            swUsbAffinity = MakeSwitch(UsbInterruptAffinityTweak.EnabledByPavise, OnUsbAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.usbaffinity"), Lang.T("set.usbaffinity.n"), swUsbAffinity, out cardH);
            sy += cardH + 8;

            swDevPower = MakeSwitch(DevicePowerTweak.EnabledByPavise, OnDevPowerToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.devpower"), Lang.T("set.devpower.n"), swDevPower, out cardH);
            sy += cardH + 8;

            swNetThrottle = MakeSwitch(NetTweak.RepairedByPavise, OnNetThrottleToggle);
            swNetThrottle.Enabled = NetTweak.NeedsRepair() || NetTweak.RepairedByPavise;
            cardNetThrottle = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76,
                Lang.T("set.netthrottle"), Lang.T("set.netthrottle.n"), swNetThrottle, out cardH);
            sy += cardH + 8;

            scroll = envTabPanels[2]; sy = 2;

            swAccessKeys = MakeSwitch(AccessibilityKeysTweak.EnabledByPavise, OnAccessKeysToggle);
            swAccessKeys.Enabled = AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.EnabledByPavise;
            cardAccessKeys = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.accesskeys"),
                Lang.T("set.accesskeys.n"), swAccessKeys, out cardH);
            sy += cardH + 8;

            swHidPower = MakeSwitch(HidPowerTweak.EnabledByPavise, OnHidPowerToggle);
            cardHidPower = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.hidpower"),
                Lang.T("set.hidpower.n"), swHidPower, out cardH);
            sy += cardH + 8;

            swPointerPrec = MakeSwitch(PointerPrecisionTweak.EnabledByPavise, OnPointerPrecToggle);
            swPointerPrec.Enabled = PointerPrecisionTweak.NeedsFix() || PointerPrecisionTweak.EnabledByPavise;
            cardPointerPrec = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.pointerprec"),
                Lang.T("set.pointerprec.n"), swPointerPrec, out cardH);
            sy += cardH + 8;

            swInputQueue = MakeSwitch(InputMythTweak.RepairedByPavise, OnInputQueueToggle);
            swInputQueue.Enabled = InputMythTweak.NeedsRepair() || InputMythTweak.RepairedByPavise;
            cardInputQueue = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.inputqueue"),
                Lang.T("set.inputqueue.n"), swInputQueue, out cardH);
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
            if (cardNetThrottle != null)
                cardNetThrottle.SetStatus(NetTweak.Describe(),
                    StatusInk(NetTweak.NeedsRepair(), NetTweak.RepairedByPavise));
            if (cardQuantum != null)
                cardQuantum.SetStatus(QuantumTweak.Describe(),
                    StatusInk(QuantumTweak.NeedsRepair(), QuantumTweak.RepairedByPavise));
            if (cardClock != null)
                cardClock.SetStatus(PlatformClockTweak.Describe(),
                    StatusInk(PlatformClockTweak.NeedsRepair(), PlatformClockTweak.RepairedByPavise));
            if (cardAccessKeys != null)
                cardAccessKeys.SetStatus(AccessibilityKeysTweak.Describe(),
                    StatusInk(AccessibilityKeysTweak.NeedsFix(), AccessibilityKeysTweak.EnabledByPavise));
            if (cardHidPower != null)
                cardHidPower.SetStatus(HidPowerTweak.Describe(),
                    StatusInk(!HidPowerTweak.EnabledByPavise, HidPowerTweak.EnabledByPavise));
            if (cardInputQueue != null)
                cardInputQueue.SetStatus(InputMythTweak.Describe(),
                    StatusInk(InputMythTweak.NeedsRepair(), InputMythTweak.RepairedByPavise));
            if (cardPointerPrec != null)
                cardPointerPrec.SetStatus(PointerPrecisionTweak.Describe(),
                    StatusInk(PointerPrecisionTweak.NeedsFix(), PointerPrecisionTweak.EnabledByPavise));
        }

        private void OnAccessKeysToggle(object s, EventArgs e)
        {
            if (swAccessKeys.Checked) AccessibilityKeysTweak.Enable(); else AccessibilityKeysTweak.Restore();
            swAccessKeys.SetSilently(AccessibilityKeysTweak.EnabledByPavise);
            swAccessKeys.Enabled = AccessibilityKeysTweak.NeedsFix() || AccessibilityKeysTweak.EnabledByPavise;
            if (cardAccessKeys != null)
                SyncEnvStatus();
        }

        private void OnPointerPrecToggle(object s, EventArgs e)
        {
            if (swPointerPrec.Checked) PointerPrecisionTweak.Enable(); else PointerPrecisionTweak.Restore();
            swPointerPrec.SetSilently(PointerPrecisionTweak.EnabledByPavise);
            swPointerPrec.Enabled = PointerPrecisionTweak.NeedsFix() || PointerPrecisionTweak.EnabledByPavise;
            if (cardPointerPrec != null)
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

        private void OnInputQueueToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swInputQueue, InputMythTweak.RepairedByPavise)) return;
            bool ok = swInputQueue.Checked ? InputMythTweak.Repair() : InputMythTweak.Restore();
            swInputQueue.SetSilently(InputMythTweak.RepairedByPavise);
            swInputQueue.Enabled = InputMythTweak.NeedsRepair() || InputMythTweak.RepairedByPavise;
            if (cardInputQueue != null)
                SyncEnvStatus();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T("irqaffinity.reboot"));
        }

        private void OnClockToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swClock, PlatformClockTweak.RepairedByPavise)) return;
            if (swClock.Checked) PlatformClockTweak.Repair(); else PlatformClockTweak.Restore();
            swClock.SetSilently(PlatformClockTweak.RepairedByPavise);
            if (cardClock != null)
                SyncEnvStatus();
        }

        private void OnQuantumToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swQuantum, QuantumTweak.RepairedByPavise)) return;
            if (swQuantum.Checked) QuantumTweak.Repair(); else QuantumTweak.Restore();
            swQuantum.SetSilently(QuantumTweak.RepairedByPavise);
            if (cardQuantum != null)
                SyncEnvStatus();
        }

        private void OnNetThrottleToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swNetThrottle, NetTweak.RepairedByPavise)) return;
            if (swNetThrottle.Checked) NetTweak.Repair(); else NetTweak.Restore();
            swNetThrottle.SetSilently(NetTweak.RepairedByPavise);
            if (cardNetThrottle != null)
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

        private bool RequireElevationFor(Toggle sw, bool restoredState)
        {
            if (elevated) return true;
            PaviseDialog.Warn(this, App.DisplayName, Lang.T("vbs.needadmin"));
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

        private void OnIrqAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swIrqAffinity, InterruptAffinityTweak.EnabledByPavise)) return;
            bool ok = swIrqAffinity.Checked ? InterruptAffinityTweak.Enable() : InterruptAffinityTweak.Disable();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T("irqaffinity.reboot"));
            swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByPavise);
        }

        private void OnUsbAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swUsbAffinity, UsbInterruptAffinityTweak.EnabledByPavise)) return;
            if (swUsbAffinity.Checked
                && !PaviseDialog.Confirm(this, App.DisplayName, Lang.T("usbaffinity.warn"), DlgKind.Warn))
            {
                swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByPavise);
                return;
            }
            bool ok = swUsbAffinity.Checked ? UsbInterruptAffinityTweak.Enable() : UsbInterruptAffinityTweak.Disable();
            if (ok) PaviseDialog.Info(this, App.DisplayName, Lang.T("irqaffinity.reboot"));
            swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByPavise);
        }

        private void OnVbsToggle(object s, EventArgs e)
        {
            if (swVbs.Checked)
            {
                if (!RequireElevationFor(swVbs, false)) return;
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

        private void RefreshEnvironmentStateAsync()
        {
            if (!UiActive) return;
            if (Interlocked.Exchange(ref envBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var st = new VbsTweak.State();
                try { st = VbsTweak.Query(); } catch { }
                Interlocked.Exchange(ref envBusy, 0);
                if (!UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive) return;
                        if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByPavise);
                        ApplyVbsState(st);
                    }));
                }
                catch { }
            });
        }

        private void SyncEnvironmentToggles()
        {
            if (swHags != null) swHags.SetSilently(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn());
            if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByPavise);
            if (swIrqAffinity != null) swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByPavise);
            if (swUsbAffinity != null) swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByPavise);
            if (swGmGuard != null) swGmGuard.SetSilently(GameModeGuard.EnabledByPavise);
            if (swNetThrottle != null) swNetThrottle.SetSilently(NetTweak.RepairedByPavise);
            if (swQuantum != null) swQuantum.SetSilently(QuantumTweak.RepairedByPavise);
            if (swClock != null) swClock.SetSilently(PlatformClockTweak.RepairedByPavise);
            if (swDevPower != null) swDevPower.SetSilently(DevicePowerTweak.EnabledByPavise);
            if (swAccessKeys != null) swAccessKeys.SetSilently(AccessibilityKeysTweak.EnabledByPavise);
            if (swHidPower != null) swHidPower.SetSilently(HidPowerTweak.EnabledByPavise);
            if (swInputQueue != null) swInputQueue.SetSilently(InputMythTweak.RepairedByPavise);
            if (swPointerPrec != null) swPointerPrec.SetSilently(PointerPrecisionTweak.EnabledByPavise);
        }
    }
}
