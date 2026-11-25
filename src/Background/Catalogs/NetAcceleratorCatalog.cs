using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class NetAcceleratorCatalog
    {
        private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "uu", "uu_ball", "uu_launcher", "xunyou", "leigod", "leigod_launcher", "leishensdk", "qiyou", "biubiu", "bbservice", "dolphinq",
            // Steam++ 也就是 Watt Toolkit 在本机跑代理加速 Steam 联网 压住它 经过它的请求全部卡住
            "wtfast", "guguai", "steam++"
        };

        private static readonly string[] Tokens = new string[]
        {
            "accelerat", "gearup", "lagofast", "加速器", "xunyou", "leigod", "leishen", "qiyou", "biubiu", "dolphinq", "wtfast",
            "exitlag", "noping", "mudfish", "clash", "mihomo", "sing-box", "v2ray", "xray", "nekoray", "nekobox",
            "hiddify", "wireguard", "openvpn", "tailscale", "zerotier", "sstap", "netch", "guguai", "ggkuai", "古怪"
        };

        internal static bool IsAcceleratorLikeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            name = WhitelistRule.NormalizeName(name);
            if (string.Equals(name, "MpCopyAccelerator", StringComparison.OrdinalIgnoreCase)) return false;
            if (ProcessNames.Contains(name))
            {
                return true;
            }
            string toLowerInvariantResult = name.ToLowerInvariant();
            string[] tokens = Tokens;
            foreach (string value in tokens)
            {
                if (toLowerInvariantResult.Contains(value))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool IsAcceleratorLikePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                string directoryName = Path.GetDirectoryName(path);
                for (int i = 0; i < 2; i++)
                {
                    if (string.IsNullOrEmpty(directoryName))
                    {
                        break;
                    }
                    string toLowerInvariantResult = Path.GetFileName(directoryName).ToLowerInvariant();
                    if (toLowerInvariantResult.Length == 0)
                    {
                        break;
                    }
                    string[] tokens = Tokens;
                    foreach (string value in tokens)
                    {
                        if (toLowerInvariantResult.Contains(value))
                        {
                            return true;
                        }
                    }
                    directoryName = Path.GetDirectoryName(directoryName);
                }
            }
            catch
            {
            }
            return false;
        }

        internal static string[] ProcessNamesForDisplay()
        {
            string[] textEntries = new string[ProcessNames.Count];
            ProcessNames.CopyTo(textEntries);
            Array.Sort(textEntries, StringComparer.OrdinalIgnoreCase);
            return textEntries;
        }

        internal static string[] TokensForDisplay()
        {
            return (string[])Tokens.Clone();
        }
    }
}
