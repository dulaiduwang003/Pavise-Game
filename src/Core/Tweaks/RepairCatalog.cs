// @author bdth 2074055628@qq.com
// File purpose Unified catalog of the family that reverts system values broken by tutorials
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class RepairTweak
    {
        public readonly string Key;
        public readonly Func<bool> NeedsRepair;
        public readonly Func<bool> Repair;
        public readonly Func<bool> Restore;
        public readonly Func<string> Describe;
        public readonly Func<bool> RepairedByPavise;

        public RepairTweak(string key, Func<bool> needsRepair, Func<bool> repair, Func<bool> restore,
            Func<string> describe, Func<bool> repairedByPavise)
        {
            Key = key;
            NeedsRepair = needsRepair;
            Repair = repair;
            Restore = restore;
            Describe = describe;
            RepairedByPavise = repairedByPavise;
        }
    }

    internal static class RepairCatalog
    {
        private static readonly RepairTweak[] Items =
        {
            new RepairTweak("net",
                delegate { return NetTweak.NeedsRepair(); },
                delegate { return NetTweak.Repair(); },
                delegate { return NetTweak.Restore(); },
                delegate { return NetTweak.Describe(); },
                delegate { return NetTweak.RepairedByPavise; }),

            new RepairTweak("inputmyth",
                delegate { return InputMythTweak.NeedsRepair(); },
                delegate { return InputMythTweak.Repair(); },
                delegate { return InputMythTweak.Restore(); },
                delegate { return InputMythTweak.Describe(); },
                delegate { return InputMythTweak.RepairedByPavise; }),

            new RepairTweak("quantum",
                delegate { return QuantumTweak.NeedsRepair(); },
                delegate { return QuantumTweak.Repair(); },
                delegate { return QuantumTweak.Restore(); },
                delegate { return QuantumTweak.Describe(); },
                delegate { return QuantumTweak.RepairedByPavise; }),

            new RepairTweak("linkmetric",
                delegate { return LinkMetricTweak.NeedsRepair(); },
                delegate { return LinkMetricTweak.Repair(); },
                delegate { return LinkMetricTweak.Restore(); },
                delegate { return LinkMetricTweak.Describe(); },
                delegate { return LinkMetricTweak.RepairedByPavise; }),

            new RepairTweak("fth",
                delegate { return FthTweak.NeedsRepair(); },
                delegate { return FthTweak.Repair(); },
                delegate { return FthTweak.Restore(); },
                delegate { return FthTweak.Describe(); },
                delegate { return FthTweak.RepairedByPavise; }),
        };

        public static IEnumerable<RepairTweak> All { get { return Items; } }

        public static RepairTweak Find(string key)
        {
            foreach (RepairTweak t in Items)
                if (string.Equals(t.Key, key, StringComparison.Ordinal)) return t;
            return null;
        }
    }
}
