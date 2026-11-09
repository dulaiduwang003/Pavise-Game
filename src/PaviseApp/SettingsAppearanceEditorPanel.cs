using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SettingsAppearanceEditorPanel : DBPanel
    {
        internal SettingsAppearanceEditorPanel() { Tag = "backdrop-card"; }
        internal Rectangle BackgroundIconBounds;

        internal Rectangle AccentIconBounds;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            DsPaint.CardBackground(e.Graphics, this, Theme.Card);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            SettingsWorkspaceIcons.Draw(e.Graphics, BackgroundIconBounds, "appearance", Theme.Accent);
            SettingsWorkspaceIcons.Draw(e.Graphics, AccentIconBounds, "general", Theme.Accent);
        }
    }
}
