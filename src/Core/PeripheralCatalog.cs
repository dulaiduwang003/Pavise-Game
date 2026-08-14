// @author bdth 2074055628@qq.com
// 文件用途 键鼠输入链与音频外设进程识别名单 命中即豁免后台压制 进程名子串 文件描述 在场设备厂商词条三层匹配

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class PeripheralCatalog
    {
        private static readonly string[] NameKeywords =
        {
            "keyboard", "mouse", "hotkey", "keymap", "macro", "autohotkey",
            "hid", "input", "ime", "pinyin", "qqpy", "sogou", "sgtool", "wetype", "iflyime", "wubi",
            "audio", "sound", "voice", "headset", "headphone", "bluetooth",
            "nahimic", "realtek", "rtkaud", "creative", "dolby", "waves", "sonic",
            "equalizer", "eartrumpet", "wavelink", "goxlr",
            "logi", "lghub", "lcore", "razer", "synapse", "icue", "corsair",
            "steelseries", "armoury", "wooting", "keychron", "dareu", "rapoo",
            "hyperx", "ngenuity", "epos", "sennheiser", "astro", "turtlebeach",
            "edifier", "hecate", "elgato", "roccat",
            "bloody", "a4tech", "vgn", "langtu", "gamepp"
        };

        private static readonly string[] DescriptionWords =
        {
            "keyboard", "mouse", "headset", "earphone", "headphone", "earbud", "speaker",
            "audio", "sound", "voice", "microphone", "bluetooth", "dolby", "equalizer",
            "input method", "ime", "hotkey", "macro", "peripheral",
            "gamepad", "joystick", "controller", "hid",
            "键盘", "鼠标", "耳机", "耳麦", "音频", "声卡", "音箱", "扬声器", "蓝牙", "降噪", "音效",
            "麦克", "输入法", "按键", "外设", "手柄", "灯效", "搜狗", "讯飞输入", "漫步者"
        };

        internal static bool IsInputChainProcess(string name, string path)
        {
            if (IsInputAudioLike(name)) return true;
            string info = FileInfoText(path);
            if (info.Length > 0 && DescriptionLooksInputAudio(info)) return true;
            return MatchesPresentDevice(name, info);
        }

        private static bool MatchesPresentDevice(string name, string info)
        {
            string[] vendorTokens = PeripheralVendorProbe.Tokens();
            if (vendorTokens.Length == 0) return false;
            string lower = (name ?? "").ToLowerInvariant();
            foreach (string token in vendorTokens)
            {
                if (lower.Contains(token)) return true;
                if (info.Length > 0 && info.Contains(token)) return true;
            }
            return false;
        }

        internal static bool IsInputAudioLike(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            foreach (string keyword in NameKeywords)
                if (lower.Contains(keyword)) return true;
            return false;
        }

        internal static bool DescriptionLooksInputAudio(string description)
        {
            if (string.IsNullOrEmpty(description)) return false;
            string lower = description.ToLowerInvariant();
            foreach (string word in DescriptionWords)
                if (lower.Contains(word)) return true;
            return false;
        }

        private static readonly object infoCacheSync = new object();
        private static readonly Dictionary<string, string> infoCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string FileInfoText(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            lock (infoCacheSync)
            {
                string cached;
                if (infoCache.TryGetValue(path, out cached)) return cached;
            }
            string text = "";
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                text = ((info.FileDescription ?? "") + "\n" + (info.ProductName ?? "")
                    + "\n" + (info.CompanyName ?? "")).ToLowerInvariant();
                if (text.Length == 2) text = "";
            }
            catch { }
            lock (infoCacheSync)
            {
                if (infoCache.Count > 512) infoCache.Clear();
                infoCache[path] = text;
            }
            return text;
        }
    }
}
