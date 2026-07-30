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

            // 导航多分组：标题只把它之后的槽位往下推，之前的不受影响
            int[] groups = { 5, 7 };
            Eq(0, NavRail.GroupsAbove(0, groups));
            Eq(0, NavRail.GroupsAbove(4, groups));
            Eq(1, NavRail.GroupsAbove(5, groups));
            Eq(1, NavRail.GroupsAbove(6, groups));
            Eq(2, NavRail.GroupsAbove(7, groups));
            Eq(2, NavRail.GroupsAbove(9, groups));
            Eq(0, NavRail.GroupsAbove(3, null));
            Eq(0, NavRail.GroupsAbove(3, new int[0]));

            // 帧率上限档位与写入 DRS 的模式串必须双向一致，越界索引落回「关」
            Eq("off", PanelForm.FrlModeOf(0));
            Eq("60", PanelForm.FrlModeOf(1));
            Eq("120", PanelForm.FrlModeOf(2));
            Eq("screen", PanelForm.FrlModeOf(3));
            Eq("off", PanelForm.FrlModeOf(9));
            for (int i = 0; i <= 3; i++) Eq(i, PanelForm.FrlIndexOf(PanelForm.FrlModeOf(i)));
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
