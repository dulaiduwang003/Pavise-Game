using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal abstract class GameCardExtension : Control
    {
        public abstract int PreferredHeight { get; }

        public virtual int PreferredHeightForWidth(int width)
        {
            return PreferredHeight;
        }

        public virtual void SetSurface(Color surface)
        {
        }
    }
}
