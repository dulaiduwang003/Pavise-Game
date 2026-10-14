// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;

namespace PaviseApp
{

    internal static class NetAcceleratorCatalog
    {

        private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {

            "uu", "uu_ball",
            "xunyou",
            "leigod", "leigod_launcher", "leishensdk",
            "qiyou",
            "biubiu", "bbservice",
            "dolphinq",
            "wtfast",
        };

        private static readonly string[] Tokens =
        {
            "accelerat", "booster", "加速器",
            "xunyou", "leigod", "leishen", "qiyou", "biubiu", "dolphinq",
            "wtfast", "exitlag", "noping", "mudfish"
        };

        internal static bool IsAcceleratorLikeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            name = WhitelistRule.NormalizeName(name);
            if (ProcessNames.Contains(name)) return true;
            string lower = name.ToLowerInvariant();
            foreach (string token in Tokens) if (lower.Contains(token)) return true;
            return false;
        }

        internal static string[] ProcessNamesForDisplay()
        {
            var names = new string[ProcessNames.Count];
            ProcessNames.CopyTo(names);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }

        internal static string[] TokensForDisplay()
        {
            return (string[])Tokens.Clone();
        }
    }
}
