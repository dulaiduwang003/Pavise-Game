using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class RepairCatalog
    {
        private static readonly RepairTweak[] Items = new RepairTweak[4]
        {
            new RepairTweak("net", () => NetTweak.NeedsRepair(), () => NetTweak.Repair(), () => NetTweak.Restore(), () => NetTweak.Describe(), () => NetTweak.RepairedByPavise),
            new RepairTweak("quantum", () => QuantumTweak.NeedsRepair(), () => QuantumTweak.Repair(), () => QuantumTweak.Restore(), () => QuantumTweak.Describe(), () => QuantumTweak.RepairedByPavise),
            new RepairTweak("linkmetric", () => LinkMetricTweak.NeedsRepair(), () => LinkMetricTweak.Repair(), () => LinkMetricTweak.Restore(), () => LinkMetricTweak.Describe(), () => LinkMetricTweak.RepairedByPavise),
            new RepairTweak("fth", () => FthTweak.NeedsRepair(), () => FthTweak.Repair(), () => FthTweak.Restore(), () => FthTweak.Describe(), () => FthTweak.RepairedByPavise)
        };

        public static IEnumerable<RepairTweak> All
        {
            get
            {
                return Items;
            }
        }

        public static RepairTweak Find(string key)
        {
            RepairTweak[] items = Items;
            foreach (RepairTweak repairTweak in items)
            {
                if (string.Equals(repairTweak.Key, key, StringComparison.Ordinal))
                {
                    return repairTweak;
                }
            }
            return null;
        }
    }
}
