// @author bdth 2074055628@qq.com
// UI dormancy state-machine regression tests.

using System;
using System.Windows.Forms;

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestUiDormancyState()
        {
            Eq(true, PanelForm.ShouldRunUi(true, FormWindowState.Normal));
            Eq(true, PanelForm.ShouldRunUi(true, FormWindowState.Maximized));
            Eq(false, PanelForm.ShouldRunUi(true, FormWindowState.Minimized));
            Eq(false, PanelForm.ShouldRunUi(false, FormWindowState.Normal));

            bool last = false, armed = false;
            PanelForm.SyncAutoHideBaseline(true, ref last, ref armed);
            Eq(true, last);
            Eq(true, armed);
            Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));
            PanelForm.SyncAutoHideBaseline(false, ref last, ref armed);
            Eq(false, last);
            Eq(false, armed);
            Eq(AutoHideAction.Schedule, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));

            Eq(false, AegisCore.ShouldAnimate(true, true, true, true, FormWindowState.Minimized));
            Eq(false, AegisCore.ShouldAnimate(true, true, true, false, FormWindowState.Normal));
            Eq(false, AegisCore.ShouldAnimate(false, true, true, true, FormWindowState.Normal));
            Eq(true, AegisCore.ShouldAnimate(true, true, true, true, FormWindowState.Normal));

            Eq(33, AegisCore.DesiredFrameInterval(false, false));
            Eq(33, AegisCore.DesiredFrameInterval(false, true));
            Eq(33, AegisCore.DesiredFrameInterval(true, true));
            Eq(200, AegisCore.DesiredFrameInterval(true, false));

            bool wasSuspended = UiClock.Suspended;
            try
            {
                UiClock.Running = false;
                UiClock.Suspended = true;
                UiClock.Wake(4);
                Eq(false, UiClock.Running);

                UiClock.Suspended = false;
                UiClock.Wake(4);
                Eq(true, UiClock.Running);
                UiClock.Suspended = true;
                Eq(false, UiClock.Running);
            }
            finally
            {
                UiClock.Running = false;
                UiClock.Suspended = wasSuspended;
            }
        }
    }
}
