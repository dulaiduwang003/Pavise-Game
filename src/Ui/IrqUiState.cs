using System.Drawing;

namespace PaviseApp
{
    // Status colors are separate from the user-chosen theme color; every read uses the current light/dark theme
    internal static class IrqUiState
    {
        internal static string Placement(IrqDevice device)
        {
            if (device == null) return "none";
            if (device.ManagedElsewhere) return "managed";
            if (!string.IsNullOrEmpty(device.AdjustmentPlacement)) return device.AdjustmentPlacement;
            if (!device.ConfigurationKnown && !device.IsPinned) return "unknown";
            if (device.Effective) return "matches";
            if (device.PlacementMismatch) return "mismatch";
            if (device.AwaitingReboot) return "reboot";
            return device.IsPinned ? "pending" : "none";
        }

        internal static string Text(string state)
        {
            if (state == "none") return Lang.T("irq.ui.nochange");
            if (state == "managed") return Lang.T("irq.tag.owned").Trim('[', ']');
            return Lang.T("irq.verify." + state);
        }

        internal static Color ColorFor(string state)
        {
            if (state == "matches") return Theme.Green;
            if (state == "mismatch" || state == "config") return Theme.Danger;
            if (state == "reboot" || state == "pending" || state == "unknown") return Theme.Warning;
            return Theme.Dim;
        }

        internal static string LogicalCpus(ulong mask)
        {
            return mask == 0 ? Lang.T("irq.ui.unset")
                : Lang.F("irq.flow.cpus", IrqRelocate.MaskText(mask), CpuTopology.CountSetBits(mask));
        }

        internal static string NextAction(bool admin, bool enabled, int displaySessions, int pending)
        {
            if (!admin) return "admin";
            if (pending > 0) return "restart";
            if (!enabled) return "enable";
            return displaySessions > 0 ? "review" : "observe";
        }
    }
}
