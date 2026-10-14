using System;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class ManualCorePlan
    {
        internal const string Key = "GmManualCorePlanV2";
        internal const int MinLogical = 1;
        internal readonly ulong GameMask;
        internal readonly bool Isolation;
        internal readonly bool HardAffinity;
        internal readonly string Topology;

        internal ManualCorePlan(ulong gameMask, bool isolation, string topology, bool hardAffinity = false)
        { GameMask = gameMask; Isolation = isolation; Topology = topology; HardAffinity = hardAffinity; }

        internal string Encode()
        {
            // 保留旧配置的标准编码，避免升级后被格式检查拒绝。
            return (HardAffinity ? "2|" : "1|") + Topology + "|" + GameMask.ToString("X", CultureInfo.InvariantCulture)
                + "|" + (Isolation ? "1" : "0") + (HardAffinity ? "|1" : "");
        }

        internal static string Stamp(ulong all, ulong[] physical)
        {
            string result = all.ToString("X", CultureInfo.InvariantCulture);
            if (physical != null) foreach (ulong core in physical) result += "," + core.ToString("X", CultureInfo.InvariantCulture);
            return result;
        }

        internal static string CurrentStamp()
        { return Stamp(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks()); }

        internal static ManualCorePlan Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 1200) return null;
            string[] p = text.Split('|');
            ulong mask;
            bool hard = p.Length == 5 && p[0] == "2" && p[4] == "1";
            if (!(p.Length == 4 && p[0] == "1") && !hard) return null;
            if ((p[3] != "0" && p[3] != "1")
                || !Hex(p[2], out mask) || mask == 0) return null;
            string[] topology = p[1].Split(',');
            if (topology.Length < 2 || topology.Length > 65) return null;
            foreach (string part in topology) { ulong value; if (!Hex(part, out value) || value == 0) return null; }
            return new ManualCorePlan(mask, p[3] == "1", p[1].ToUpperInvariant(), hard);
        }

        private static bool Hex(string text, out ulong value)
        {
            value = 0;
            return text.Length > 0 && text.Length <= 16
                && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        internal static string Raw(GameProfile profile)
        {
            string value;
            return profile != null && profile.Overrides.TryGetValue(Key, out value) ? value : null;
        }

        internal string Validate(ulong all, ulong[] physical, ulong allocated, bool supported, bool topologyTrusted)
        {
            if (!topologyTrusted || Topology != Stamp(all, physical)) return "schedule.manual.topology";
            ulong covered = 0;
            if (physical == null || physical.Length == 0) return "schedule.manual.topology";
            foreach (ulong core in physical)
            {
                if (core == 0 || (core & ~all) != 0 || (covered & core) != 0) return "schedule.manual.topology";
                covered |= core;
            }
            if (covered != all) return "schedule.manual.topology";
            if ((GameMask & ~all) != 0 || AutoCores.CountBits(GameMask) < MinLogical) return "schedule.manual.minimum";
            ulong whole = AutoCores.WholeCores(GameMask, physical);
            if (((Isolation ? whole : GameMask) & allocated) != 0) return "schedule.manual.allocated";
            if (!Isolation) return null;
            if (!supported) return "schedule.auto.unsupported";
            if (allocated != 0) return "schedule.manual.allocated";
            return AutoCores.ValidateRange(whole, GameMask, all, physical, allocated, true) == null ? null : "schedule.manual.spare";
        }

        internal string ValidateCurrent(ulong allocated)
        {
            return Validate(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks(), allocated,
                AutoCores.Supported, IntPtr.Size == 8 && !CpuTopology.MultiGroup
                    && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
        }

        internal AutoCorePlan RuntimePlan()
        {
            ulong allocated = AutoCores.LiveAllocated();
            ulong[] physical = CpuTopology.PhysicalCoreMasks();
            ulong whole = AutoCores.WholeCores(GameMask, physical);
            return new AutoCorePlan { Exclusive = Isolation ? whole : GameMask, Region = whole,
                Cores = AutoCores.PhysicalCount(whole, physical), Logical = AutoCores.CountBits(GameMask),
                Allocated = allocated, Kind = "manual", Blocked = ValidateCurrent(allocated) };
        }
    }
}
