using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 白名单页手动添加的外设关键词 存在设置里 启动时载入外设目录
    // 内置名单认不出的杂牌驱动 用户填个品牌名或驱动名就能按外设保护
    internal static class PeripheralUserKeywords
    {
        private const string SettingName = "PeripheralUserKeywords";

        internal const int MaxCount = 64;

        private static readonly object sync = new object();

        internal static void Load()
        {
            PeripheralCatalog.SetUserKeywords(Split(Settings.LoadStr(SettingName, "")));
        }

        // 返回 null 表示成功 否则是给用户看的原因
        internal static string Add(string raw)
        {
            string keyword = PeripheralCatalog.NormalizeUserKeyword(raw);
            if (keyword == null) return Lang.F("white.keyword.invalid", PeripheralCatalog.UserKeywordMinLength, PeripheralCatalog.UserKeywordMaxLength);
            lock (sync)
            {
                var list = new List<string>(PeripheralCatalog.UserKeywordsForDisplay());
                if (list.Contains(keyword)) return Lang.T("white.keyword.exists");
                if (list.Count >= MaxCount) return Lang.F("white.keyword.full", MaxCount);
                list.Add(keyword);
                return Save(list);
            }
        }

        internal static string Remove(string keyword)
        {
            lock (sync)
            {
                var list = new List<string>(PeripheralCatalog.UserKeywordsForDisplay());
                if (!list.Remove(keyword)) return null;
                return Save(list);
            }
        }

        internal static string Clear()
        {
            lock (sync)
            {
                if (PeripheralCatalog.UserKeywordsForDisplay().Length == 0) return null;
                return Save(new List<string>());
            }
        }

        private static string Save(List<string> list)
        {
            if (!Settings.SaveStr(SettingName, string.Join("\n", list.ToArray()))) return Lang.T("white.keyword.savefail");
            PeripheralCatalog.SetUserKeywords(list);
            return null;
        }

        private static string[] Split(string stored)
        {
            return (stored ?? "").Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
