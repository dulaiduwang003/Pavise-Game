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
        private Toggle swHags, swVbs, swMpo, swIrqAffinity, swNetAffinity, swUsbAffinity, swGmGuard, swNagle;
        private Toggle swNetThrottle, swDevPower;
        private SettingCard cardVbs, cardNetThrottle;
        private int envBusy;
        private static readonly object netQosSync = new object();

        private void BuildEnvironmentPage()
        {
            int y = PageHeader(pageEnvironment, Lang.T("nav.env"), Lang.T("v16.env.sub"), 2);

            var warn = new RoundPanel();
            warn.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(46));
            warn.BackColor = Theme.Bg; warn.Fill = Theme.Card; warn.Border = Theme.Stroke;
            warn.Radius = Theme.S(12); warn.AccentEdge = true;
            CardLabel(warn, Lang.T("sec.env.kernel"), 18, 14, ContentW - 36, 20, 8.2f, true, Theme.Accent);
            pageEnvironment.Controls.Add(warn);
            y += 58;

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageEnvironment.Controls.Add(scroll);

            int sy = 2, cardH;

            swHags = MakeSwitch(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn(), OnHagsToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), Lang.T("set.hags.n"), swHags, out cardH);
            sy += cardH + 8;

            swVbs = MakeSwitch(VbsTweak.DisabledByPavise, OnVbsToggle);
            cardVbs = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), "…", swVbs, out cardH);
            sy += cardH + 8;

            swMpo = MakeSwitch(MpoTweak.DisabledByPavise || MpoTweak.CurrentlyDisabled(), OnMpoToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.mpo"), Lang.T("set.mpo.n"), swMpo, out cardH);
            sy += cardH + 8;

            swIrqAffinity = MakeSwitch(InterruptAffinityTweak.EnabledByPavise, OnIrqAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.irqaffinity"), Lang.T("set.irqaffinity.n"), swIrqAffinity, out cardH);
            sy += cardH + 8;

            swNetAffinity = MakeSwitch(NetworkAffinityTweak.EnabledByPavise, OnNetAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.netaffinity"), Lang.T("set.netaffinity.n"), swNetAffinity, out cardH);
            sy += cardH + 8;

            swUsbAffinity = MakeSwitch(UsbInterruptAffinityTweak.EnabledByPavise, OnUsbAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.usbaffinity"), Lang.T("set.usbaffinity.n"), swUsbAffinity, out cardH);
            sy += cardH + 8;

            swGmGuard = MakeSwitch(GameModeGuard.EnabledByPavise, OnGameModeGuardToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.gmguard"), Lang.T("set.gmguard.n"), swGmGuard, out cardH);
            sy += cardH + 8;

            swNagle = MakeSwitch(NagleTweak.EnabledByPavise, OnNagleToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nagle"), Lang.T("set.nagle.n"), swNagle, out cardH);
            sy += cardH + 8;

            swNetThrottle = MakeSwitch(NetTweak.RepairedByPavise, OnNetThrottleToggle);
            swNetThrottle.Enabled = NetTweak.NeedsRepair() || NetTweak.RepairedByPavise;
            cardNetThrottle = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76,
                Lang.T("set.netthrottle"), Lang.T("set.netthrottle.n") + "\r\n" + NetTweak.Describe(), swNetThrottle, out cardH);
            sy += cardH + 8;

            swDevPower = MakeSwitch(DevicePowerTweak.EnabledByPavise, OnDevPowerToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.devpower"), Lang.T("set.devpower.n"), swDevPower, out cardH);
            sy += cardH + 8;

        }

        private void OnNetThrottleToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swNetThrottle, NetTweak.RepairedByPavise)) return;
            if (swNetThrottle.Checked) NetTweak.Repair(); else NetTweak.Restore();
            swNetThrottle.SetSilently(NetTweak.RepairedByPavise);
            if (cardNetThrottle != null)
                cardNetThrottle.Desc = Lang.T("set.netthrottle.n") + "\r\n" + NetTweak.Describe();
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

        private void OnNagleToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swNagle, NagleTweak.EnabledByPavise)) return;
            bool ok = swNagle.Checked ? NagleTweak.Enable() : NagleTweak.Restore();
            if (ok && swNagle.Checked)
                MessageBox.Show(this, Lang.T("nagle.applied"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swNagle.SetSilently(NagleTweak.EnabledByPavise);
        }

        private bool RequireElevationFor(Toggle sw, bool restoredState)
        {
            if (elevated) return true;
            MessageBox.Show(this, Lang.T("vbs.needadmin"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            sw.SetSilently(restoredState);
            return false;
        }

        private void OnHagsToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHags, HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn())) return;
            bool ok = swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("hags.reboot"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swHags.SetSilently(HagsTweak.EnabledByPavise || HagsTweak.CurrentlyOn());
        }

        private void OnMpoToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swMpo, MpoTweak.DisabledByPavise || MpoTweak.CurrentlyDisabled())) return;
            bool ok = swMpo.Checked ? MpoTweak.Disable() : MpoTweak.Restore();
            if (ok) MessageBox.Show(this, Lang.T("mpo.reboot"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swMpo.SetSilently(MpoTweak.DisabledByPavise || MpoTweak.CurrentlyDisabled());
        }

        private void OnIrqAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swIrqAffinity, InterruptAffinityTweak.EnabledByPavise)) return;
            bool ok = swIrqAffinity.Checked ? InterruptAffinityTweak.Enable() : InterruptAffinityTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("irqaffinity.reboot"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByPavise);
        }

        private void OnUsbAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swUsbAffinity, UsbInterruptAffinityTweak.EnabledByPavise)) return;
            bool ok = swUsbAffinity.Checked ? UsbInterruptAffinityTweak.Enable() : UsbInterruptAffinityTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("irqaffinity.reboot"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByPavise);
        }

        private void OnNetAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swNetAffinity, NetworkAffinityTweak.EnabledByPavise)) return;
            bool ok;
            lock (netQosSync)
            {
                ok = swNetAffinity.Checked
                    ? NetworkAffinityTweak.Enable(gameMode.GetProfiles())
                    : NetworkAffinityTweak.Disable();
            }
            if (ok) MessageBox.Show(this, Lang.T("netaffinity.reboot"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swNetAffinity.SetSilently(NetworkAffinityTweak.EnabledByPavise);
        }

        private void OnVbsToggle(object s, EventArgs e)
        {
            if (swVbs.Checked)
            {
                if (!RequireElevationFor(swVbs, false)) return;
                var r = MessageBox.Show(this, Lang.T("vbs.warn"), "Pavise", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK || !VbsTweak.Disable())
                {
                    swVbs.SetSilently(false); RefreshVbsState(); return;
                }
                RefreshVbsState();
                MessageBox.Show(this, Lang.T("vbs.done"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                if (!RequireElevationFor(swVbs, true)) return;
                if (!VbsTweak.Restore())
                {
                    swVbs.SetSilently(VbsTweak.DisabledByPavise);
                    RefreshVbsState();
                    MessageBox.Show(this, Lang.T("vbs.restorefail"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                RefreshVbsState();
                MessageBox.Show(this, Lang.T("vbs.restored"), "Pavise", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (swMpo != null) swMpo.SetSilently(MpoTweak.DisabledByPavise || MpoTweak.CurrentlyDisabled());
            if (swIrqAffinity != null) swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByPavise);
            if (swNetAffinity != null) swNetAffinity.SetSilently(NetworkAffinityTweak.EnabledByPavise);
            if (swUsbAffinity != null) swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByPavise);
            if (swGmGuard != null) swGmGuard.SetSilently(GameModeGuard.EnabledByPavise);
            if (swNagle != null) swNagle.SetSilently(NagleTweak.EnabledByPavise);
            if (swNetThrottle != null) swNetThrottle.SetSilently(NetTweak.RepairedByPavise);
            if (swDevPower != null) swDevPower.SetSilently(DevicePowerTweak.EnabledByPavise);
        }
    }
}
