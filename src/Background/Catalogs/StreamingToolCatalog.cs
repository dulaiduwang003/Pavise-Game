using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class StreamingToolCatalog
    {
        // 推流根进程使用发布者核验过的可执行文件名 通用编码器或浏览器宿主不能证明推流归属

        private static readonly HashSet<string> ProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "obs32", "obs64", "obs-browser-page", "obs-ffmpeg-mux",
            "livehime", "直播伴侣", "直播伴侣 Launcher", "kwaiLive", "DYTool", "DYToolEx", "dyClientLancher", "DYffmpeg-mux", "HuyaPresenter",
            "Streamlabs Desktop", "Streamlabs OBS", "XSplit.Core", "PRISMLiveStudio",
            "TikTok LIVE Studio", "MeldStudio", "vMix64", "Wirecast"
        };

        // 显示名跟随界面语言 每次取都按当前语言生成 名单不参与进程匹配
        private static string[] Products()
        {
            return new string[]
            {
                "OBS Studio", Lang.Text("哔哩哔哩直播姬", "Bilibili Livehime"), Lang.Text("抖音直播伴侣", "Douyin Live Companion"),
                Lang.Text("快手直播伴侣", "Kuaishou Live Companion"), Lang.Text("斗鱼直播伴侣", "Douyu Live Companion"), Lang.Text("虎牙开播端", "Huya Live"),
                "Streamlabs Desktop", "XSplit Broadcaster", "PRISM Live Studio", "TikTok LIVE Studio", "Meld Studio", "vMix", "Wirecast"
            };
        }

        internal static bool IsStreamingToolProcess(string name, string imagePath)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            if (!ProcessNames.Contains(normalizedName)) return false;
            string path = WhitelistRule.NormalizeImagePath(imagePath);
            // 必须确认是本机绝对可执行路径 才可建立新的推流家族
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

        internal static string[] ProcessNamesForDisplay()
        {
            string[] names = new string[ProcessNames.Count];
            ProcessNames.CopyTo(names);
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }

        internal static string[] ProductsForDisplay()
        {
            return Products();
        }
    }
}
