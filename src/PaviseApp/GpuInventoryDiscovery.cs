using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class GpuDriverBinding
    {
        internal readonly string InstanceId, SoftwareKey;
        internal GpuDriverBinding(string instanceId, string softwareKey)
        { InstanceId = instanceId; SoftwareKey = softwareKey; }
    }

    // Map current display instances to their device-specific driver keys; model IDs are not device identities.
    internal static class GpuInventoryDiscovery
    {
        internal const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        internal static bool TrySoftwareKey(string driver, out string softwareKey)
        {
            softwareKey = null;
            string prefix = DisplayClass + "\\";
            if (driver == null || driver.Length != prefix.Length + 4
                || !driver.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = prefix.Length; i < driver.Length; i++)
                if (driver[i] < '0' || driver[i] > '9') return false;
            softwareKey = driver.Substring(prefix.Length);
            return true;
        }

        internal static bool TryMapPresent(IEnumerable<string> instances,
            Func<string, string> readDriver, out GpuDriverBinding[] bindings)
        {
            bindings = new GpuDriverBinding[0];
            if (instances == null || readDriver == null) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mapped = new List<GpuDriverBinding>();
            try
            {
                foreach (string instance in instances)
                {
                    if (string.IsNullOrEmpty(instance) || instance.Length > 4096) return false;
                    // Software, remote and root-enumerated display devices are not
                    // physical PCI targets for the power/clock controls.
                    if (!instance.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                    int separator = instance.IndexOf('\\', 4);
                    if (separator <= 4 || separator == instance.Length - 1 || PciModelKey(instance) == null) return false;
                    if (!seen.Add(instance)) continue;
                    string softwareKey;
                    if (!TrySoftwareKey(readDriver(instance), out softwareKey)) return false;
                    mapped.Add(new GpuDriverBinding(instance, softwareKey));
                }
            }
            catch { return false; }
            bindings = mapped.ToArray();
            return true;
        }

        internal static bool MatchesInstance(string instance, string matchingDeviceId)
        {
            string first = PciModelKey(instance), second = PciModelKey(matchingDeviceId);
            return first != null && second != null && first == second;
        }

        private static string PciModelKey(string value)
        {
            if (value == null || !value.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return null;
            int end = value.IndexOf('\\', 4);
            string hardware = end < 0 ? value.Substring(4) : value.Substring(4, end - 4);
            string vendor = HexPart(hardware, "VEN_"), device = HexPart(hardware, "DEV_");
            return vendor == null || device == null ? null : vendor + ":" + device;
        }

        private static string HexPart(string hardware, string prefix)
        {
            int offset = hardware.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (offset < 0 || (offset > 0 && hardware[offset - 1] != '&')) return null;
            int start = offset + prefix.Length;
            if (start + 4 > hardware.Length || (start + 4 < hardware.Length && hardware[start + 4] != '&')) return null;
            for (int i = start; i < start + 4; i++)
            {
                char c = hardware[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return null;
            }
            return hardware.Substring(start, 4).ToUpperInvariant();
        }
    }
}
