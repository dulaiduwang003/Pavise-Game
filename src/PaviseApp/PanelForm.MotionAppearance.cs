using System;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private MotionAppearancePanel motionAppearance;
        private bool motionAppearanceForeground=true;
        private void BuildMotionAppearance(Control parent)
        {
            motionAppearance=new MotionAppearancePanel(gameMode.ActivePreset);
            motionAppearance.Applied+=delegate{if(paviseCore!=null&&!paviseCore.IsDisposed)paviseCore.RefreshVisual();};
            parent.Controls.Add(motionAppearance);
            ScrollableControl scroll=parent as ScrollableControl;
            if(scroll!=null)scroll.Scroll+=delegate{if(motionAppearance!=null&&!motionAppearance.IsDisposed)motionAppearance.SyncViewport();};
        }
        private void SyncMotionAppearanceActivity(bool? foreground=null)
        {
            if(foreground.HasValue)motionAppearanceForeground=foreground.Value;
            if(motionAppearance==null||motionAppearance.IsDisposed)return;
            bool page=UiActive&&curPage==pageSettings&&settingsTabs!=null&&settingsTabs.Index==2;
            bool inGame=gameMode!=null&&gameMode.Enabled&&gameMode.IsActive;
            motionAppearance.SetActivity(page,motionAppearanceForeground,inGame);
        }
    }
}
