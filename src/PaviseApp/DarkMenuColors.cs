using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground
        {
            get
            {
                return Theme.Card;
            }
        }

        public override Color ImageMarginGradientBegin
        {
            get
            {
                return Theme.Card;
            }
        }

        public override Color ImageMarginGradientMiddle
        {
            get
            {
                return Theme.Card;
            }
        }

        public override Color ImageMarginGradientEnd
        {
            get
            {
                return Theme.Card;
            }
        }

        public override Color MenuBorder
        {
            get
            {
                return Theme.Stroke;
            }
        }

        public override Color MenuItemBorder
        {
            get
            {
                return Theme.Sel;
            }
        }

        public override Color MenuItemSelected
        {
            get
            {
                return Theme.CardHover;
            }
        }

        public override Color MenuItemSelectedGradientBegin
        {
            get
            {
                return Theme.CardHover;
            }
        }

        public override Color MenuItemSelectedGradientEnd
        {
            get
            {
                return Theme.CardHover;
            }
        }

        public override Color MenuItemPressedGradientBegin
        {
            get
            {
                return Theme.Nav;
            }
        }

        public override Color MenuItemPressedGradientEnd
        {
            get
            {
                return Theme.Nav;
            }
        }

        public override Color SeparatorDark
        {
            get
            {
                return Theme.Stroke;
            }
        }

        public override Color SeparatorLight
        {
            get
            {
                return Theme.Stroke;
            }
        }

        public override Color CheckBackground
        {
            get
            {
                return Theme.Sel;
            }
        }

        public override Color CheckSelectedBackground
        {
            get
            {
                return Theme.Sel;
            }
        }

        public override Color CheckPressedBackground
        {
            get
            {
                return Theme.Sel;
            }
        }
    }
}
