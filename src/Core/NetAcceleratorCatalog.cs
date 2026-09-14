// @author bdth 2074055628@qq.com
// File purpose Identify online game accelerator processes so they are exempt from background suppression
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
            if (ProcessNames.Contains(name)) return true;
            string low = name.ToLowerInvariant();
            foreach (string t in Tokens) if (low.Contains(t)) return true;
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
