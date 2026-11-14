using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        internal static PerformanceSectionKind? PerformanceSectionFor(string key)
        {
            PerformanceLifetime lifetime = PerformanceLifetimeFor(key);
            if (lifetime != PerformanceLifetime.Session && lifetime != PerformanceLifetime.Persistent) return null;
            PerformanceRestartScope restart = key == "EnvPerformanceBundleV1" || key == "EnvStoragePowerGroupV1"
                ? PerformanceRestartScope.SystemRestart : PerformanceRestartFor(key);
            return restart == PerformanceRestartScope.SystemRestart ? (PerformanceSectionKind?)PerformanceSectionKind.Restart : null;
        }

        private static void CollectPerformanceCards(Control control, List<PerformanceFeatureCard> cards)
        {
            var card = control as PerformanceFeatureCard;
            if (card != null)
            {
                cards.Add(card);
                return;
            }
            foreach (Control child in control.Controls) CollectPerformanceCards(child, cards);
        }

        private static PerformanceSectionKind? ControlPerformanceSection(Control control)
        {
            var cards = new List<PerformanceFeatureCard>();
            CollectPerformanceCards(control, cards);
            PerformanceSectionKind? result = null;
            bool found = false;
            foreach (PerformanceFeatureCard card in cards)
            {
                PerformanceSectionKind? section = PerformanceSectionFor(PerformanceFeatureKey(card.Hosted == null ? "" : card.Hosted.Name));
                if (found && result != section) return null;
                result = section;
                found = true;
            }
            return result;
        }

        private static void CompactPerformanceContent(Control control)
        {
            var card = control as PerformanceFeatureCard;
            if (card != null)
            {
                card.CompactRow = true;
                return;
            }
            var nestedGrid = control as PerformanceFeatureGrid;
            if (nestedGrid != null && nestedGrid.AutoSizeContent) nestedGrid.SingleColumn = true;
            foreach (Control child in control.Controls) CompactPerformanceContent(child);
        }

        private static void GroupPerformanceSections(Control grid, int category)
        {
            if (category < 0 || category >= 4) throw new ArgumentOutOfRangeException("category");
            var loose = new List<Control>();
            var restart = new List<Control>();
            foreach (Control control in grid.Controls)
            {
                var heading = control as PerformanceSectionHeading;
                if (heading != null && heading.SectionKind.HasValue) return;
                // 只调整外层展示顺序，保留复合设置的同步、恢复和高度管理。
                CompactPerformanceContent(control);
                PerformanceSectionKind? section = ControlPerformanceSection(control);
                if (section == PerformanceSectionKind.Restart) restart.Add(control);
                else loose.Add(control);
            }
            var ordered = new List<Control>(loose);
            grid.SuspendLayout();
            try
            {
                if (restart.Count > 0)
                {
                    var heading = new PerformanceSectionHeading
                    {
                        Name = "performanceSectionRestart",
                        Text = PerformanceSectionHeading.SectionTitle(PerformanceSectionKind.Restart),
                        SectionKind = PerformanceSectionKind.Restart
                    };
                    grid.Controls.Add(heading);
                    ordered.Add(heading);
                    ordered.AddRange(restart);
                }
                for (int index = 0; index < ordered.Count; index++) grid.Controls.SetChildIndex(ordered[index], index);
            }
            finally { grid.ResumeLayout(false); }
        }
    }
}
