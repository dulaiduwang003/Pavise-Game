using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Panel coreQuickPanel;

        private sealed class CoreQuickChoice
        {
            internal ulong Mask;
            internal int Threads;
        }

        private void BuildCoreShortcuts(int width)
        {
            coreQuickPanel = new Panel { Name = "coreQuickSelection", BackColor = Theme.Card };
            coreQuickPanel.SetBounds(Theme.S(18), 0, Theme.S(width), Theme.S(30));
            coreEditor.Controls.Add(coreQuickPanel);
            AddCoreShortcut("all", Lang.T("schedule.quick.all"), CpuTopology.AllMask, 0);
            if (CpuTopology.Hybrid)
            {
                AddCoreShortcut("perf", Lang.T("schedule.quick.perf"), CpuTopology.PerfMask, 0);
                AddCoreShortcut("eff", Lang.T("schedule.quick.eff"), CpuTopology.EffMask, 0);
            }
            ulong[] dies = CpuTopology.DieMasks();
            ulong cache = CpuTopology.CacheHeavyMask();
            for (int i = 0; i < dies.Length; i++)
                AddCoreShortcut("ccd" + i, Lang.F((dies[i] & cache) != 0 ? "core.band.ccd.cache" : "core.band.ccd", i), dies[i], 0);
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                if (CpuTopology.CountSetBits(core & CpuTopology.AllMask) > 1)
                {
                    AddCoreShortcut("noSmt", Lang.T("schedule.quick.nosmt"), 0, 1);
                    AddCoreShortcut("withSmt", Lang.T("schedule.quick.smt"), 0, 2);
                    break;
                }
        }

        private void AddCoreShortcut(string name, string title, ulong mask, int threads)
        {
            mask &= CpuTopology.AllMask;
            if (threads == 0 && mask == 0) return;
            foreach (Control control in coreQuickPanel.Controls)
            {
                var existing = (CoreQuickChoice)control.Tag;
                if (threads == 0 && existing.Threads == 0 && existing.Mask == mask) return;
            }
            var choice = new CoreQuickChoice { Mask = mask, Threads = threads };
            var button = new PillButton(title) { Name = "coreQuick_" + name, Tag = choice, AccessibleName = title };
            button.Size = new Size(Math.Max(Theme.S(72), TextRenderer.MeasureText(title, Theme.UI(9f, true)).Width + Theme.S(28)), Theme.S(30));
            button.Click += delegate
            {
                if (coreDraftProfile == null) return;
                coreEditMatrix.Selected = CoreShortcutTarget(choice);
                coreDraftTopology = ManualCorePlan.CurrentStamp();
                SaveCoreDraft();
            };
            coreQuickPanel.Controls.Add(button);
        }

        private ulong CoreShortcutTarget(CoreQuickChoice choice)
        {
            ulong available = CpuTopology.AllMask & ~(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
            return choice.Threads == 0 ? choice.Mask & available
                : CoreShortcutThreads(coreEditMatrix.Selected, available, CpuTopology.PhysicalCoreMasks(), choice.Threads == 2);
        }

        internal static ulong CoreShortcutThreads(ulong selected, ulong available, ulong[] physical, bool includeSiblings)
        {
            ulong result = 0;
            foreach (ulong core in physical)
            {
                ulong picked = core & selected & available;
                if (picked == 0) continue;
                result |= includeSiblings ? core & available : picked & (~picked + 1UL);
            }
            return result;
        }

        private void SyncCoreShortcuts()
        {
            if (coreQuickPanel == null) return;
            foreach (PillButton button in coreQuickPanel.Controls)
            {
                var choice = (CoreQuickChoice)button.Tag;
                ulong target = CoreShortcutTarget(choice);
                button.Enabled = CpuTopology.CountSetBits(target) >= ManualCorePlan.MinLogical
                    && (choice.Threads == 0 || target != coreEditMatrix.Selected);
                BtnKind kind = choice.Threads == 0 && target != 0 && target == coreEditMatrix.Selected ? BtnKind.Primary : BtnKind.Normal;
                if (button.Kind != kind) { button.Kind = kind; button.Invalidate(); }
            }
        }

        private void LayoutCoreShortcuts()
        {
            int x = 0, y = 0, gap = Theme.S(6), height = Theme.S(30);
            foreach (Control button in coreQuickPanel.Controls)
            {
                if (x > 0 && x + button.Width > coreQuickPanel.Width) { x = 0; y += height + gap; }
                button.Location = new Point(x, y);
                x += button.Width + gap;
            }
            coreQuickPanel.Top = coreEditorHelp.Bottom + Theme.S(10);
            coreQuickPanel.Height = y + height;
        }
    }
}
