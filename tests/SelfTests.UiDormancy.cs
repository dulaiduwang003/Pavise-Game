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

            Eq(33, PaviseCore.DesiredFrameInterval(false, false));
            Eq(33, PaviseCore.DesiredFrameInterval(false, true));
            Eq(33, PaviseCore.DesiredFrameInterval(true, true));
            Eq(200, PaviseCore.DesiredFrameInterval(true, false));

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
