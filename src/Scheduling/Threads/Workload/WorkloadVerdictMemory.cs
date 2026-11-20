using System;
using System.Globalization;
using System.IO;

namespace PaviseApp
{
    // 自动模式判定“不适合给关键角色留出物理核”的游戏（线程多 负载铺满大部分核心 或者选不出可用的核）
    // 结论记 7 天 游戏程序文件变了就作废重判 有效期内开局不再做完整采集 省掉每局几秒钟的事件处理和摘要计算
    internal static class WorkloadVerdictMemory
    {
        internal const int ValidDays = 7;
        private const string Prefix = "WorkloadWideVerdictV1:";

        internal static void Remember(string profileId, string exePath)
        {
            string stamp = Stamp(exePath);
            if (string.IsNullOrEmpty(profileId) || stamp.Length == 0) return;
            Settings.SaveStr(Prefix + profileId, stamp + "|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        internal static void Forget(string profileId)
        {
            if (!string.IsNullOrEmpty(profileId)) Settings.SaveStr(Prefix + profileId, "");
        }

        // 有效时返回还剩几天 没有记录 程序已更新或已过期返回 false
        internal static bool Recent(string profileId, string exePath, out int daysLeft)
        {
            daysLeft = 0;
            if (string.IsNullOrEmpty(profileId)) return false;
            string text = Settings.LoadStr(Prefix + profileId, "");
            int split = text.LastIndexOf('|');
            if (split <= 0) return false;
            long saved;
            if (!long.TryParse(text.Substring(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out saved)) return false;
            string stamp = Stamp(exePath);
            if (stamp.Length == 0 || !string.Equals(text.Substring(0, split), stamp, StringComparison.Ordinal)) return false;
            TimeSpan age = new TimeSpan(Math.Max(0, DateTime.UtcNow.Ticks - saved));
            if (age.TotalDays >= ValidDays) return false;
            daysLeft = Math.Max(1, (int)Math.Ceiling(ValidDays - age.TotalDays));
            return true;
        }

        // 程序文件的大小和修改时间 读不到就不记 也不认旧结论
        private static string Stamp(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath)) return "";
                var info = new FileInfo(exePath);
                if (!info.Exists) return "";
                return info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { return ""; }
        }
    }
}
