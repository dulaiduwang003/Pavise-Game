// Saves UI position only, never touches feature switches; keyed by control-tree path so it holds no disposed controls across theme/DPI rebuilds
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
            var value = new PageViewPosition();
            scale = Math.Max(0.1f, scale);
            Visit(root, "", delegate(Control control, string path)
            {
                var tab = control as TechTabs;
                if (tab != null) value.tabs[path] = tab.Index;
                var scroll = control as ScrollableControl;
                if (scroll != null && scroll.AutoScroll)
                    value.scrolls[path] = new PointF(-scroll.AutoScrollPosition.X / scale, -scroll.AutoScrollPosition.Y / scale);
            });
            return value;
        }

        internal void Restore(Control root, float scale)
        {
            // Restore the tab first so the matching scroll container takes part in layout, then restore the position; Index changes no settings
            Visit(root, "", delegate(Control control, string path)
            {
                int index;
                var tab = control as TechTabs;
                if (tab != null && tabs.TryGetValue(path, out index))
                {
                    tab.Index = index;
                    tab.SnapToSelection();
                }
            });
            root.PerformLayout();
            Visit(root, "", delegate(Control control, string path)
            {
                PointF point;
                var scroll = control as ScrollableControl;
                if (scroll != null && scroll.AutoScroll && scrolls.TryGetValue(path, out point))
                    scroll.AutoScrollPosition = new Point((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale));
            });
        }

        private static void Visit(Control control, string path, Action<Control, string> visit)
        {
            visit(control, path);
            for (int i = 0; i < control.Controls.Count; i++) Visit(control.Controls[i], path + "/" + i, visit);
        }
    }
}
