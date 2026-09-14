// Manual scheduling plan; the config is a record, saving it does not mean Windows has placed the process on cores
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class CoreSchedulingPlan
    {
        public ulong GameMask, IsolationMask;
        public bool IsolationOn;
        public string Topology = "";
        public bool ReadFailed;

        public CoreSchedulingPlan Clone() { return (CoreSchedulingPlan)MemberwiseClone(); }

        public string Encode()
        {
            return "2|" + Topology + "|" + GameMask.ToString("X") + "|"
                + (IsolationOn ? "1" : "0") + "|" + IsolationMask.ToString("X");
        }

        public static bool TryParse(string raw, out CoreSchedulingPlan plan)
        {
            plan = new CoreSchedulingPlan { ReadFailed = true };
            if (string.IsNullOrEmpty(raw) || raw.Length > 4096) return false;
            string[] p = raw.Split('|');
            ulong game, isolation, retiredHeavy;
            bool legacy = p.Length == 7 && p[0] == "1";
            if (!legacy && !(p.Length == 5 && p[0] == "2")) return false;
            if (p[1].Length == 0 || (p[3] != "0" && p[3] != "1")
                || !Hex(p[2], out game) || !Hex(p[4], out isolation)) return false;
            // V1 heavy-load fields are only format-checked; they are not carried into the new plan and no longer take part in placement validation
            if (legacy && ((p[5] != "0" && p[5] != "1") || !Hex(p[6], out retiredHeavy))) return false;
            plan = new CoreSchedulingPlan { Topology = p[1], GameMask = game,
                IsolationOn = p[3] == "1", IsolationMask = isolation };
            return true;
        }

        private static bool Hex(string s, out ulong value)
        {
            value = 0;
            if (s.Length < 1 || s.Length > 16) return false;
            foreach (char c in s)
                if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F') && !(c >= 'a' && c <= 'f')) return false;
            return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }

    internal static class CoreScheduling
    {
        public const string Key = "GmManualCorePlanV1";
        // Legacy key used only for disabling, override cleanup and concurrent-edit checks; no longer a configurable policy
        public const string HeavyMaskKey = "GmHeavyCoreMaskV1";
        internal static readonly string[] PlacementKeys = { PolicyCatalog.KeyCoreMask,
            PolicyCatalog.KeyStrictCores, PolicyCatalog.KeyCoreDomainAlt,
            PolicyCatalog.KeyHeavySqueeze, HeavyMaskKey };

        // Runtime admission and readback are rechecked by the separate lease worker
#if PAVISE_SELFTEST
        internal static bool? IsolationSupportedForTest;
#endif
        public static bool IsolationSupported
        {
            get
            {
#if PAVISE_SELFTEST
                if (IsolationSupportedForTest.HasValue) return IsolationSupportedForTest.Value;
#endif
                return IntPtr.Size == 8 && Native.OsBuild() >= 19041 && !CpuTopology.MultiGroup;
            }
        }

        public static string Stamp(ulong all, ulong[] cores)
        {
            var parts = new List<string> { all.ToString("X") };
            foreach (ulong core in cores) parts.Add(core.ToString("X"));
            return string.Join(",", parts.ToArray());
        }

        public static string CurrentStamp { get { return Stamp(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks()); } }

        public static ulong WholeCores(ulong mask, ulong[] cores)
        {
            ulong result = 0;
            foreach (ulong core in cores) if ((core & mask) != 0) result |= core;
            return result;
        }

        // The exclusive range is derived straight from the game core selection; the user is not asked to pick twice
        //   Aligned to whole physical cores only, since isolation can only take whole cores; which ones is the user's call
        //   The core holding CPU 0 may be exclusive too; the real floor is the rule below: two whole physical cores must remain outside the exclusive set
        //   Returns 0 when no usable range can be derived; callers treat that as exclusive cores unavailable
        public static ulong ExclusiveMaskFor(ulong gameMask, ulong[] cores)
        {
            if (cores == null || cores.Length == 0) return 0;
            ulong exclusive = WholeCores(gameMask, cores);
            if (exclusive == 0) return 0;
            return SpareCoresOutside(exclusive, cores) >= 2 ? exclusive : 0;
        }

        // Enough physical cores must remain outside the exclusive set; the default is select-all, which would make exclusive fill every core and leave none for the system
        //   This trims the selection back until it can just be exclusive, yielding from the last cores first, and returns the adjusted game selection
        //   Returns 0 if it still fails after yielding everything, meaning no adjustment makes exclusive possible on this machine
        public static ulong TrimForExclusive(ulong gameMask, ulong[] cores)
        {
            if (cores == null || cores.Length == 0) return 0;
            ulong mask = gameMask;
            while (mask != 0)
            {
                if (ExclusiveMaskFor(mask, cores) != 0) return mask;
                // Yield the highest-numbered whole core in the current selection
                ulong whole = WholeCores(mask, cores);
                ulong last = 0;
                foreach (ulong core in cores) if ((core & whole) != 0) last = core;
                if (last == 0) return 0;
                mask &= ~last;
            }
            return 0;
        }

        // How many whole physical cores remain outside the exclusive set; the UI uses it to say how many are still missing
        public static int SpareCoresOutside(ulong exclusiveMask, ulong[] cores)
        {
            if (cores == null) return 0;
            int spare = 0;
            foreach (ulong core in cores) if ((core & exclusiveMask) == 0) spare++;
            return spare;
        }

        public static string Validate(CoreSchedulingPlan p, ulong all, ulong[] cores,
            bool multiGroup, bool isolationSupported)
        {
            if (p == null || p.ReadFailed) return "schedule.error.read";
            if (multiGroup || all == 0 || cores.Length == 0) return "schedule.error.groups";
            if (p.Topology != Stamp(all, cores)) return "schedule.error.topology";
            ulong covered = 0;
            foreach (ulong core in cores)
            {
                if (core == 0 || (core & ~all) != 0 || (covered & core) != 0) return "schedule.error.topology";
                covered |= core;
            }
            if (covered != all) return "schedule.error.topology";
            if ((p.GameMask & ~all) != 0 || CpuTopology.CountSetBits(p.GameMask) < CpuTopology.MinCustomCores)
                return "schedule.error.game";
            // Keep the selection even when off, but reject corrupted partial physical cores and out-of-range data
            if ((p.IsolationMask & ~all) != 0
                || WholeCores(p.IsolationMask, cores) != p.IsolationMask) return "schedule.error.whole";
            // Whether the core holding CPU 0 is exclusive is the user's call; the only floor is two whole physical cores left outside the exclusive set
            if (p.IsolationMask != 0 && SpareCoresOutside(p.IsolationMask, cores) < 2)
                return "schedule.error.spare";
            if (p.IsolationOn && p.IsolationMask == 0) return "schedule.error.isolationempty";
            if (p.IsolationOn && !isolationSupported) return "schedule.error.isolationunsupported";
            return null;
        }

        public static string Validate(CoreSchedulingPlan p)
        {
            return Validate(p, CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks(),
                CpuTopology.MultiGroup, IsolationSupported);
        }

        public static CoreSchedulingPlan LoadGlobal()
        {
            bool present;
            return LoadGlobal(out present);
        }

        public static CoreSchedulingPlan LoadGlobal(out bool present)
        {
            present = true;
            string raw;
            if (!Settings.TryLoadStr(Key, out raw)) return new CoreSchedulingPlan { ReadFailed = true };
            CoreSchedulingPlan p;
            if (raw.Length != 0) { CoreSchedulingPlan.TryParse(raw, out p); return p; }
            present = false;
            // Only migrate the old game core selection; the removed heavy-load switch is no longer read
            p = new CoreSchedulingPlan { Topology = CurrentStamp,
                GameMask = LegacyGameMask(null) };
            return p;
        }

        public static bool HasGlobalRecord()
        {
            string raw;
            return !Settings.TryLoadStr(Key, out raw) || raw.Length != 0;
        }

        public static string GlobalToken()
        {
            string raw;
            if (!Settings.TryLoadStr(Key, out raw)) return "unreadable";
            return raw.Length != 0 ? "record:" + raw : "legacy:" + LoadGlobal().Encode();
        }

        private static ulong LegacyGameMask(GameProfile profile)
        {
            ulong mask;
            string raw = LegacyValue(profile, PolicyCatalog.KeyCoreMask, "");
            if (raw.Length != 0)
                return ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask) ? mask : 0;
            bool strict = LegacyValue(profile, PolicyCatalog.KeyStrictCores, "0") == "1";
            if (strict && CpuTopology.HasSafeBackgroundPartition())
            {
                bool alt = LegacyValue(profile, PolicyCatalog.KeyCoreDomainAlt, "0") == "1";
                return CpuTopology.HasAltPartition() && alt != CpuTopology.AltDomainActive
                    ? CpuTopology.AltStrictBoostMask : CpuTopology.StrictBoostMask;
            }
            return CpuTopology.AllMask;
        }

        private static string LegacyValue(GameProfile p, string key, string fallback)
        {
            string value;
            if (p != null && p.Overrides.TryGetValue(key, out value)) return value;
            PolicyItem item = PolicyCatalog.ItemOf(key);
            return item != null && item.Kind == PolicyValueKind.Bool
                ? (Settings.Load(key, fallback == "1") ? "1" : "0") : Settings.LoadStr(key, fallback);
        }

        public static string LegacyPolicyValue(string key)
        {
            PolicyItem item = PolicyCatalog.ItemOf(key);
            return item == null ? "" : PolicyCatalog.Canonical(key, LegacyValue(null, key, item.Fallback));
        }

        public static string PrerequisiteText(GameProfile profile)
        {
            var policy = PolicyResolver.For(profile);
            return policy.EffBoost ? "" : Lang.T("schedule.prerequisite.game");
        }

        public static CoreSchedulingPlan ForProfile(GameProfile profile, CoreSchedulingPlan global)
        {
            if (profile == null) return global.Clone();
            string raw;
            CoreSchedulingPlan p;
            if (profile.Overrides.TryGetValue(Key, out raw))
            {
                CoreSchedulingPlan.TryParse(raw, out p);
                p.IsolationOn = global.IsolationOn;
                p.IsolationMask = global.IsolationMask;
                p.ReadFailed |= global.ReadFailed;
                return p;
            }
            p = global.Clone();
            if (profile.Overrides.ContainsKey(PolicyCatalog.KeyCoreMask)
                || profile.Overrides.ContainsKey(PolicyCatalog.KeyStrictCores)
                || profile.Overrides.ContainsKey(PolicyCatalog.KeyCoreDomainAlt)) p.GameMask = LegacyGameMask(profile);
            return p;
        }

        public static bool IsPlacementKey(string key)
        {
            foreach (string k in PlacementKeys) if (key == k) return true;
            return false;
        }

        internal static bool IsRetiredKey(string key)
        {
            return key == PolicyCatalog.KeyHeavySqueeze || key == HeavyMaskKey;
        }

        public static string ProfileToken(GameProfile profile)
        {
            if (profile == null) return "";
            var parts = new List<string>();
            string raw;
            if (profile.Overrides.TryGetValue(Key, out raw)) parts.Add(Key + "=" + raw);
            foreach (string key in PlacementKeys)
                if (profile.Overrides.TryGetValue(key, out raw)) parts.Add(key + "=" + raw);
            return string.Join(";", parts.ToArray());
        }

        public static string Value(CoreSchedulingPlan p, string key)
        {
            bool gameValid = !p.ReadFailed && !CpuTopology.MultiGroup && p.Topology == CurrentStamp
                && (p.GameMask & ~CpuTopology.AllMask) == 0
                && CpuTopology.CountSetBits(p.GameMask) >= CpuTopology.MinCustomCores;
            if (key == PolicyCatalog.KeyCoreMask) return gameValid ? p.GameMask.ToString("X") : "";
            if (key == HeavyMaskKey) return "";
            return "0";
        }
    }
}
