using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private MotionAppearancePanel motionAppearance;
        private void BuildMotionAppearance(Control parent)
        {
            motionAppearance=new MotionAppearancePanel(gameMode.ActivePreset);
            motionAppearance.Applied+=delegate{if(paviseCore!=null&&!paviseCore.IsDisposed)paviseCore.RefreshVisual();};
            parent.Controls.Add(motionAppearance);
        }
    }
}
