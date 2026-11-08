using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // Commit migration only after all disabled groups are saved, preserving explicitly disabled setups.
    internal static class AntiCheatGroupMigration
    {
        internal const string Marker = "TameGroupControlMigratedV1";
        internal delegate bool ReadBool(string name, out bool present, out bool value);

        internal static bool Ensure(ReadBool read, Func<string, bool, bool> save, IEnumerable<string> groups)
        {
            try
            {
                bool present, value;
                if (read == null || save == null || groups == null || !read(Marker, out present, out value)) return false;
                if (present && value) return true;
                if (!read("TameOn", out present, out value)) return false;
                if (present && !value)
                    foreach (string group in groups)
                        if (string.IsNullOrEmpty(group) || !save("Tame_" + group, false)) return false;
                return save(Marker, true);
            }
            catch { return false; }
        }

        internal static bool ReadStored(string name, out bool present, out bool value)
        {
            present = false; value = false;
            string[] names;
            if (!Settings.TryLoadStoredNames(name, out names)) return false;
            foreach (string stored in names)
                if (string.Equals(stored, name, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
            return !present || Settings.TryLoadStoredBool(name, out value);
        }
    }
}
