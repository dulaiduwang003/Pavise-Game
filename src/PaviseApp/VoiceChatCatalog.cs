using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // 语音通话软件在对局中常开着开黑或通话，压到最低优先级容易卡语音，按进程名内置保护，不靠出声检测。
    // 只认进程名与本机绝对路径映像文件名一致的进程，不扩大到同目录的其他程序；需要压制时用户可以不开这些软件。
    internal static class VoiceChatCatalog
    {
        private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KOOK", "KaiHeiLa", "YY",
            "ts3client_win64", "ts3client_win32", "TeamSpeak", "mumble",
            "Discord", "DiscordPTB", "DiscordCanary"
        };

        // 按用户决定，QQ 和微信不内置保护，按普通后台治理；需要时由用户自己加白。
        private static readonly string[] Products = new string[]
        {
            "KOOK（开黑啦）", "YY 语音", "TeamSpeak", "Mumble", "Discord"
        };

        internal static bool IsVoiceChatProcess(string name, string imagePath)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            if (!ProcessNames.Contains(normalizedName)) return false;
            string path = WhitelistRule.NormalizeImagePath(imagePath);
            if (path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
            try
            {
                return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(normalizedName, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static string[] ProductsForDisplay()
        {
            return (string[])Products.Clone();
        }

        internal static string[] ProcessNamesForDisplay()
        {
            var names = new List<string>(ProcessNames);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }
    }
}
