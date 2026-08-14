// @author bdth 2074055628@qq.com
// 文件用途 从在场键鼠设备提取厂商与型号词条 供后台压制豁免动态识别静态名单认不出的外设软件
// 依据 设备固件自报的产品名 BusReportedDeviceDesc 不依赖厂商驱动装没装 小众牌子由本机实际插着的设备补上
// 词条只用于豁免不用于压制 误匹配的代价是少压一个后台进程 所以取词从宽 停用词挡住通用词

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
            "the", "and", "for", "with", "ver", "rev", "ghz", "mhz", "khz", "dpi", "rgb", "led"
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
                    Logger.Log("外设词条 提取到 " + fresh.Length + " 条 " + string.Join(" ", fresh));
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
            catch (Exception ex) { Logger.Log("外设词条 提取失败 " + ex.Message); }
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
