using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class PageViewPosition
    {
        private readonly Dictionary<string, PointF> scrolls = new Dictionary<string, PointF>();

        private readonly Dictionary<string, int> tabs = new Dictionary<string, int>();

        internal static PageViewPosition Capture(Control root, float scale)
        {
            PageViewPosition value = new PageViewPosition();
            scale = Math.Max(0.1f, scale);
            Visit(root, "", delegate(Control control, string path)
            {
                TechTabs techTabs = control as TechTabs;
                if (techTabs != null)
                {
                    value.tabs[path] = techTabs.Index;
                }
                ScrollableControl scrollableControl = control as ScrollableControl;
                if (scrollableControl != null && scrollableControl.AutoScroll)
                {
                    value.scrolls[path] = new PointF((float)(-scrollableControl.AutoScrollPosition.X) / scale, (float)(-scrollableControl.AutoScrollPosition.Y) / scale);
                }
            });
            return value;
        }

        internal void Restore(Control root, float scale)
        {
            Visit(root, "", delegate(Control control, string path)
            {
                TechTabs techTabs = control as TechTabs;
                int value2;
                if (techTabs != null && tabs.TryGetValue(path, out value2))
                {
                    techTabs.Index = value2;
                    techTabs.SnapToSelection();
                }
            });
            root.PerformLayout();
            Visit(root, "", delegate(Control control, string path)
            {
                ScrollableControl scrollableControl = control as ScrollableControl;
                PointF value;
                if (scrollableControl != null && scrollableControl.AutoScroll && scrolls.TryGetValue(path, out value))
                {
                    scrollableControl.AutoScrollPosition = new Point((int)Math.Round(value.X * scale), (int)Math.Round(value.Y * scale));
                }
            });
        }

        private static void Visit(Control control, string path, Action<Control, string> visit)
        {
            visit(control, path);
            for (int i = 0; i < control.Controls.Count; i++)
            {
                Visit(control.Controls[i], path + "/" + i, visit);
            }
        }
    }
}
