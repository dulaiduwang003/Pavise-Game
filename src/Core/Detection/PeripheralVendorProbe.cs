// @author bdth 2074055628@qq.com
// File purpose Extract vendor and model terms from present keyboard/mouse devices so background suppression exemption can dynamically recognize peripheral software the static list misses
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PeripheralVendorProbe
    {
        private const int RefreshMs = 600000;
        private const int MaxTokens = 64;

        private static readonly object sync = new object();
        private static string[] tokens = new string[0];
        private static int stamp;
        private static bool scanned;
        // Increments on every change of the term set; the directory verdict cache uses it as the invalidation key, no recompute when terms are unchanged
        private static volatile int generation;
        internal static int Generation { get { return generation; } }

        // Expiry refresh cannot rely on someone happening to ask for terms; the cache-hit path must go through here too, otherwise the ten-minute refresh gets blocked by the cache
        internal static int CurrentGeneration()
        {
            Tokens();
            return generation;
        }

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "usb", "usb2", "usb3", "hid", "compliant", "composite", "device", "devices", "input", "output",
            "mouse", "mice", "keyboard", "keyboards", "keypad", "pointing", "pointer", "cursor",
            "wheel", "button", "buttons", "wireless", "wired", "receiver", "dongle", "adapter",
            "transceiver", "bluetooth", "radio", "gaming", "game", "microsoft", "windows",
            "standard", "system", "generic", "port", "ports", "root", "hub", "hubs", "controller",
            "control", "collection", "consumer", "vendor", "defined", "interface", "class",
            "filter", "driver", "drivers", "firmware", "virtual", "internal", "remote",
            "desktop", "terminal", "server", "precision", "touch", "touchpad", "trackpad",
            "optical", "laser", "mechanical", "membrane", "ergonomic", "rechargeable",
            "edition", "series", "model", "type", "combo", "intel", "amd", "nvidia",
            "pro", "max", "mini", "lite", "plus", "ultra", "air", "nano",
            "black", "white", "pink", "blue", "red", "green", "gray", "grey",
            "silver", "gold", "purple", "yellow",
            "the", "and", "for", "with", "ver", "rev", "ghz", "mhz", "khz", "dpi", "rgb", "led",
            "host", "inc", "ltd", "corp", "corporation", "company", "computer", "technology",
            "electronics", "enhanced", "extensible", "compatible", "chipset", "express",
            "key", "keys", "num", "number", "pad", "media", "audio2", "codec"
        };

        public static string[] Tokens()
        {
            lock (sync)
            {
                int now = Environment.TickCount;
                if (scanned && now - stamp < RefreshMs) return tokens;
                stamp = now;
                scanned = true;
            }
            string[] fresh = Scan();
            lock (sync)
            {
                if (!SameSet(tokens, fresh))
                {
                    tokens = fresh;
                    generation++;
                    Logger.Log(Lang.T("log.peripheralvendorprobe.1") + fresh.Length + Lang.T("log.peripheralvendorprobe.2") + string.Join(" ", fresh));
                }
                return tokens;
            }
        }

        private static string[] Scan()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (InputDevice d in InputChainProbe.Devices())
                {
                    CollectNode(d.InstanceId, set);
                    if (d.Transport != InputTransport.Usb) continue;
                    foreach (string parent in PresentDevices.ParentChain(d.InstanceId))
                    {
                        if (!parent.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) continue;
                        string hardware, instance;
                        if (!HidPowerTweak.SplitInstance(parent, out hardware, out instance)) continue;
                        if (hardware.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
                        CollectNode(parent, set);
                    }
                }
            }
            catch (Exception ex) { Logger.Warn(Lang.T("log.peripheralvendorprobe.3") + ex.Message); }
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            if (list.Count > MaxTokens) list.RemoveRange(MaxTokens, list.Count - MaxTokens);
            return list.ToArray();
        }

        private static void CollectNode(string instanceId, HashSet<string> set)
        {
            AddTokens(PresentDevices.BusReportedDesc(instanceId), set);
            try
            {
                using (RegistryKey node = Registry.LocalMachine.OpenSubKey(
                    InputChainProbe.EnumRoot + "\\" + instanceId))
                {
                    if (node == null) return;
                    AddTokens(InputChainProbe.CleanDesc(node.GetValue("FriendlyName") as string), set);
                    AddTokens(InputChainProbe.CleanDesc(node.GetValue("DeviceDesc") as string), set);
                    AddTokens(InputChainProbe.CleanDesc(node.GetValue("Mfg") as string), set);
                }
            }
            catch { }
        }

        internal static void AddTokens(string raw, HashSet<string> set)
        {
            if (string.IsNullOrEmpty(raw)) return;
            int start = -1;
            for (int i = 0; i <= raw.Length; i++)
            {
                char c = i < raw.Length ? raw[i] : ' ';
                bool alnum = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (alnum)
                {
                    if (start < 0) start = i;
                    continue;
                }
                if (start >= 0)
                {
                    Take(raw, start, i - start, set);
                    start = -1;
                }
            }
        }

        private static void Take(string raw, int start, int length, HashSet<string> set)
        {
            if (length < 3 || length > 24) return;
            char first = raw[start];
            bool letter = (first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z');
            if (!letter) return;
            string token = raw.Substring(start, length).ToLowerInvariant();
            if (StopWords.Contains(token)) return;
            set.Add(token);
        }

        private static bool SameSet(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
