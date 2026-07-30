// @author bdth 2074055628@qq.com
// 文件用途 构建系统环境页 集中放置需要重启且会留在机器上的内核与驱动改动

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {
        private Toggle swHags, swVbs, swMpo, swIrqAffinity, swNetAffinity, swUsbAffinity;
        private SettingCard cardVbs;
        private int envBusy;
        // 本页拥有网卡亲和开关；游戏库改动后重下 QoS 策略也要拿这把锁，避免与开关互相打断
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

            swHags = MakeSwitch(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn(), OnHagsToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), Lang.T("set.hags.n"), swHags, out cardH);
            sy += cardH + 8;

            swVbs = MakeSwitch(VbsTweak.DisabledByAegis, OnVbsToggle);
            cardVbs = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), "…", swVbs, out cardH);
            sy += cardH + 8;

            swMpo = MakeSwitch(MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled(), OnMpoToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.mpo"), Lang.T("set.mpo.n"), swMpo, out cardH);
            sy += cardH + 8;

            swIrqAffinity = MakeSwitch(InterruptAffinityTweak.EnabledByAegis, OnIrqAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.irqaffinity"), Lang.T("set.irqaffinity.n"), swIrqAffinity, out cardH);
            sy += cardH + 8;

            swNetAffinity = MakeSwitch(NetworkAffinityTweak.EnabledByAegis, OnNetAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.netaffinity"), Lang.T("set.netaffinity.n"), swNetAffinity, out cardH);
            sy += cardH + 8;

            swUsbAffinity = MakeSwitch(UsbInterruptAffinityTweak.EnabledByAegis, OnUsbAffinityToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.usbaffinity"), Lang.T("set.usbaffinity.n"), swUsbAffinity, out cardH);
            sy += cardH + 8;
        }

        // 这页的六项改动都要求管理员权限，未提权时统一挡在同一处
        private bool RequireElevationFor(Toggle sw, bool restoredState)
        {
            if (elevated) return true;
            MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            sw.SetSilently(restoredState);
            return false;
        }

        private void OnHagsToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swHags, HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn())) return;
            bool ok = swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("hags.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swHags.SetSilently(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn());
        }

        private void OnMpoToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swMpo, MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled())) return;
            bool ok = swMpo.Checked ? MpoTweak.Disable() : MpoTweak.Restore();
            if (ok) MessageBox.Show(this, Lang.T("mpo.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swMpo.SetSilently(MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled());
        }

        private void OnIrqAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swIrqAffinity, InterruptAffinityTweak.EnabledByAegis)) return;
            bool ok = swIrqAffinity.Checked ? InterruptAffinityTweak.Enable() : InterruptAffinityTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("irqaffinity.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByAegis);
        }

        private void OnUsbAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swUsbAffinity, UsbInterruptAffinityTweak.EnabledByAegis)) return;
            bool ok = swUsbAffinity.Checked ? UsbInterruptAffinityTweak.Enable() : UsbInterruptAffinityTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("irqaffinity.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByAegis);
        }

        private void OnNetAffinityToggle(object s, EventArgs e)
        {
            if (!RequireElevationFor(swNetAffinity, NetworkAffinityTweak.EnabledByAegis)) return;
            bool ok;
            lock (netQosSync)
            {
                ok = swNetAffinity.Checked
                    ? NetworkAffinityTweak.Enable(gameMode.GetProfiles())
                    : NetworkAffinityTweak.Disable();
            }
            if (ok) MessageBox.Show(this, Lang.T("netaffinity.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swNetAffinity.SetSilently(NetworkAffinityTweak.EnabledByAegis);
        }

        private void OnVbsToggle(object s, EventArgs e)
        {
            if (swVbs.Checked)
            {
                if (!RequireElevationFor(swVbs, false)) return;
                var r = MessageBox.Show(this, Lang.T("vbs.warn"), "Aegis", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK || !VbsTweak.Disable())
                {
                    swVbs.SetSilently(false); RefreshVbsState(); return;
                }
                RefreshVbsState();
                MessageBox.Show(this, Lang.T("vbs.done"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                if (!RequireElevationFor(swVbs, true)) return;
                if (!VbsTweak.Restore())
                {
                    swVbs.SetSilently(VbsTweak.DisabledByAegis);
                    RefreshVbsState();
                    MessageBox.Show(this, Lang.T("vbs.restorefail"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                RefreshVbsState();
                MessageBox.Show(this, Lang.T("vbs.restored"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (VbsTweak.DisabledByAegis && (!st.WmiOk || st.VbsRunning)) key = "vbs.state.pending";
            else if (!st.WmiOk) key = "vbs.state.unknown";
            else if (st.VbsRunning) key = "vbs.state.on";
            else key = "vbs.state.off";
            cardVbs.Desc = Lang.T(key);
        }

        // VBS 状态要走 WMI 查询，几百毫秒起，放后台线程避免卡住换页动画
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
                        if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByAegis);
                        ApplyVbsState(st);
                    }));
                }
                catch { }
            });
        }

        private void SyncEnvironmentToggles()
        {
            if (swHags != null) swHags.SetSilently(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn());
            if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByAegis);
            if (swMpo != null) swMpo.SetSilently(MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled());
            if (swIrqAffinity != null) swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByAegis);
            if (swNetAffinity != null) swNetAffinity.SetSilently(NetworkAffinityTweak.EnabledByAegis);
            if (swUsbAffinity != null) swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByAegis);
        }
    }
}
