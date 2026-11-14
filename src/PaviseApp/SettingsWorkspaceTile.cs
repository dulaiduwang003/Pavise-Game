using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SettingsWorkspaceTile : PerformanceFeatureCard
    {
        internal bool BottomSeparator;

        internal Font TitleFont
        {
            get { return Theme.UI(CompactRow ? 9f : 11f, true); }
        }

        protected override string ShownSummary
        {
            get
            {
                string value = Value.Trim();
                return value.Length > 0 ? value : Summary;
            }
        }

        internal override string EffectText
        {
            get { return Value.Trim(); }
        }

        internal SettingsWorkspaceTile()
        {
            CompactRow = true;
            InformationButton.Name = "settingsInformation";
        }

        internal int MeasureHeight(int width)
        {
            return PresentationHeight(width);
        }

        internal void ArrangeContent()
        {
            ArrangePresentation();
        }

        protected override void DrawFeatureIcon(Graphics graphics)
        {
            SettingsWorkspaceIcons.Draw(graphics, IconBounds, IconKey, Enabled ? Theme.Accent : Theme.Dim);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (BottomSeparator)
            {
                using (Pen pen = new Pen(Col.Lerp(Theme.Card, Theme.Stroke, 0.62f)))
                {
                    e.Graphics.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
                }
            }
        }
    }
}
