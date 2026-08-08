// @author bdth 2074055628@qq.com
// UI dormancy state-machine regression tests.

using System;
using System.Windows.Forms;

namespace PaviseApp
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

            Eq(false, PaviseCore.ShouldAnimate(true, true, true, true, FormWindowState.Minimized));
            Eq(false, PaviseCore.ShouldAnimate(true, true, true, false, FormWindowState.Normal));
            Eq(false, PaviseCore.ShouldAnimate(false, true, true, true, FormWindowState.Normal));
            Eq(true, PaviseCore.ShouldAnimate(true, true, true, true, FormWindowState.Normal));

            Eq(PaviseCore.ActiveFrameMs, PaviseCore.DesiredFrameInterval(false, true));
            Eq(PaviseCore.ActiveFrameMs, PaviseCore.DesiredFrameInterval(true, true));
            Eq(PaviseCore.BackgroundFrameMs, PaviseCore.DesiredFrameInterval(false, false));
            Eq(PaviseCore.GameBackgroundFrameMs, PaviseCore.DesiredFrameInterval(true, false));
            Eq(true, PaviseCore.BackgroundFrameMs > PaviseCore.ActiveFrameMs);
            Eq(true, PaviseCore.GameBackgroundFrameMs > PaviseCore.BackgroundFrameMs);

            Eq(UiClock.ForegroundFrameMs, UiClock.DesiredFrameMs(false));
            Eq(UiClock.BackgroundFrameMs, UiClock.DesiredFrameMs(true));
            Eq(UiClock.ForegroundSlowMs, UiClock.DesiredSlowMs(false));
            Eq(UiClock.BackgroundSlowMs, UiClock.DesiredSlowMs(true));
            Eq(true, UiClock.BackgroundFrameMs > UiClock.ForegroundFrameMs);
            Eq(true, UiClock.BackgroundSlowMs > UiClock.ForegroundSlowMs);

            int[] groups = { 5, 7 };
            Eq(0, NavRail.GroupsAbove(0, groups));
            Eq(0, NavRail.GroupsAbove(4, groups));
            Eq(1, NavRail.GroupsAbove(5, groups));
            Eq(1, NavRail.GroupsAbove(6, groups));
            Eq(2, NavRail.GroupsAbove(7, groups));
            Eq(2, NavRail.GroupsAbove(9, groups));
            Eq(0, NavRail.GroupsAbove(3, null));
            Eq(0, NavRail.GroupsAbove(3, new int[0]));

            Eq("off", PanelForm.FrlModeOf(0));
            Eq("60", PanelForm.FrlModeOf(1));
            Eq("120", PanelForm.FrlModeOf(2));
            Eq("240", PanelForm.FrlModeOf(3));
            Eq("screen", PanelForm.FrlModeOf(4));
            Eq("off", PanelForm.FrlModeOf(9));
            for (int i = 0; i <= 4; i++) Eq(i, PanelForm.FrlIndexOf(PanelForm.FrlModeOf(i)));
            Eq(0, PanelForm.FrlIndexOf("nonsense"));
            Eq(0, PanelForm.FrlIndexOf(null));

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
