using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static class PeripheralCatalog
    {
        private static readonly string[] NameKeywords = new string[]
        {
            "keyboard", "mouse", "hotkey", "keymap", "macro", "autohotkey", "hid", "input", "ime", "pinyin",
            "qqpy", "sogou", "sgtool", "wetype", "iflyime", "wubi", "audio", "sound", "voice", "headset",
            "headphone", "bluetooth", "nahimic", "realtek", "rtkaud", "creative", "dolby", "waves", "sonic", "equalizer",
            "eartrumpet", "wavelink", "goxlr", "logi", "lghub", "lcore", "razer", "synapse", "icue", "corsair",
            "steelseries", "armoury", "wooting", "keychron", "dareu", "rapoo", "hyperx", "ngenuity", "epos", "sennheiser",
            "astro", "turtlebeach", "edifier", "hecate", "elgato", "roccat", "bloody", "a4tech", "vgn", "langtu",
            "gamepp", "gamepad", "joystick", "ds4windows", "rewasd", "xoutput", "joytokey", "x360ce", "dualsensex", "betterjoy",
            "antimicro", "inputmapper", "keysticks", "osk", "tabtip", "listary", "wootility",
            // 键鼠厂商驱动 进程名里常带品牌 不带品牌的靠下面的公司名描述词兜底
            "flydigi", "飞智", "setpoint", "khalmnpr", "itype", "ipoint", "glorious", "masterplus", "ducky", "cherry",
            "madcatz", "fnatic", "sharkoon", "redragon", "ajazz", "akko", "royalkludge", "oemdrv", "darmoshark", "mchose",
            "pulsar", "lamzu", "zowie", "xtrfy", "ninjutso", "machenike", "thunderobot", "motospeed", "fuhlen", "hyeku",
            "durgod", "ikbc", "realforce", "hhkb", "elecom", "drunkdeer", "nuphy", "epomaker", "monsgeek", "melgeek",
            "kzzi", "synaptics", "syntp", "etdctrl", "kanata", "weasel",
            // 手柄 方向盘 数位板
            "betop", "gamesir", "8bitdo", "thrustmaster", "fanatec", "pithouse", "simucube", "vigem",
            "wacom", "wtablet", "pentablet", "xppen", "huion", "gaomon", "tabletdriver",
            // 音频设备与声卡控制
            "krisp", "jabra", "plantronics", "shure", "focusrite", "audeze", "beyerdynamic", "steinberg", "rodecentral", "rodeconnect",
            "maono", "fifine", "boom3d", "dtsapo", "asio",
            // 外设灯效
            "signalrgb", "openrgb", "rgbfusion", "mysticlight", "polychrome", "lightingservice"
        };

        private static readonly string[] DescriptionWords = new string[]
        {
            "keyboard", "mouse", "headset", "earphone", "headphone", "earbud", "speaker", "audio", "sound", "voice",
            "microphone", "bluetooth", "dolby", "equalizer", "input method", "ime", "hotkey", "macro", "peripheral", "gamepad",
            "joystick", "controller", "hid", "键盘", "鼠标", "耳机", "耳麦", "音频", "声卡", "音箱",
            "扬声器", "蓝牙", "降噪", "音效", "麦克", "输入法", "按键", "外设", "手柄", "灯效",
            "搜狗", "讯飞输入", "漫步者",
            // 外设厂商的公司名或产品名 exe 改名或只叫 xx空间站 xx驱动 时也能认出来
            "flydigi", "飞智", "logitech", "罗技", "razer", "雷蛇", "corsair", "海盗船", "steelseries", "赛睿",
            "hyperx", "glorious", "wooting", "keychron", "8bitdo", "八位堂", "gamesir", "盖世小鸡", "betop", "北通",
            "雷柏", "达尔优", "双飞燕", "血手幽灵", "黑爵", "狼蛛", "珂芝", "迈从", "达摩鲨", "腹灵",
            "杜伽", "宁芝", "黑峡谷", "富勒", "前行者", "thrustmaster", "fanatec", "wacom", "huion", "绘王",
            "xp-pen", "pen tablet", "数位板", "手写板", "touchpad", "触控板", "摇杆", "方向盘", "sennheiser", "jabra",
            "plantronics", "shure", "focusrite", "beyerdynamic", "synaptics", "asio"
        };

        internal const int UserKeywordMinLength = 2;

        internal const int UserKeywordMaxLength = 40;

        // 用户在白名单页手动添加的关键词 和内置词一样比对进程名 文件描述 产品名和公司名
        private static volatile string[] userKeywords = new string[0];

        private static int userKeywordGeneration;

        private static readonly object infoCacheSync = new object();

        private static readonly Dictionary<string, string> infoCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsInputChainProcess(string name, string path)
        {
            if (IsAmdNoiseSuppressionProcess(name, path))
            {
                return true;
            }
            if (IsInputAudioLike(name))
            {
                return true;
            }
            string fileInfoTextResult = FileInfoText(path);
            if (fileInfoTextResult.Length > 0 && DescriptionLooksInputAudio(fileInfoTextResult))
            {
                return true;
            }
            return MatchesPresentDevice(name, fileInfoTextResult);
        }

        internal static bool IsAmdNoiseSuppressionProcess(string name, string path)
        {
            // 精确识别降噪组件 不把 AMD 厂商或 Noise 字样扩展成整家软件的豁免
            if (!IsAmdNoiseSuppressionName(name)) return false;
            if (string.IsNullOrEmpty(path) || path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':'
                || (path[2] != '\\' && path[2] != '/')) return false;
            try
            {
                if (!string.Equals(Path.GetFileName(path), "AMDNoiseSuppression.exe", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }
            // 厂商是缓存的最后一项 产品名 描述及其中的换行不参与准入 兼容驱动版本和本地化差异
            // 这是后台策略分类 不把版本字段当作数字签名认证
            string info = FileInfoText(path);
            int companySeparator = info.LastIndexOf('\n');
            if (companySeparator < 0) return false;
            string company = info.Substring(companySeparator + 1).Trim();
            bool amd = string.Equals(company, "Advanced Micro Devices, Inc.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(company, "Advanced Micro Devices, Inc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(company, "Advanced Micro Devices", StringComparison.OrdinalIgnoreCase);
            return amd;
        }

        internal static bool IsAmdNoiseSuppressionName(string name)
        {
            return string.Equals(name, "AMDNoiseSuppression", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AMDNoiseSuppression.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesPresentDevice(string name, string info)
        {
            string[] tokensResult = PeripheralVendorProbe.Tokens();
            if (tokensResult.Length == 0)
            {
                return false;
            }
            string toLowerInvariantResult = (name ?? "").ToLowerInvariant();
            string[] textEntries = tokensResult;
            foreach (string value in textEntries)
            {
                if (toLowerInvariantResult.Contains(value))
                {
                    return true;
                }
                if (info.Length > 0 && info.Contains(value))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool IsInputAudioLike(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string toLowerInvariantResult = name.ToLowerInvariant();
            string[] nameKeywords = NameKeywords;
            foreach (string value in nameKeywords)
            {
                if (toLowerInvariantResult.Contains(value))
                {
                    return true;
                }
            }
            return ContainsUserKeyword(toLowerInvariantResult);
        }

        private static bool ContainsUserKeyword(string lowered)
        {
            string[] current = userKeywords;
            foreach (string value in current)
            {
                if (lowered.Contains(value))
                {
                    return true;
                }
            }
            return false;
        }

        internal static int UserKeywordGeneration
        {
            get { return Volatile.Read(ref userKeywordGeneration); }
        }

        internal static void SetUserKeywords(IEnumerable<string> keywords)
        {
            var list = new List<string>();
            if (keywords != null)
            {
                foreach (string raw in keywords)
                {
                    string keyword = NormalizeUserKeyword(raw);
                    if (keyword != null && !list.Contains(keyword)) list.Add(keyword);
                }
            }
            userKeywords = list.ToArray();
            Interlocked.Increment(ref userKeywordGeneration);
        }

        internal static string[] UserKeywordsForDisplay()
        {
            return (string[])userKeywords.Clone();
        }

        // 去掉首尾空白 引号和 .exe 后统一小写 太短会误伤一大片 太长多半是贴错了 都不收
        internal static string NormalizeUserKeyword(string raw)
        {
            if (raw == null) return null;
            string text = raw.Trim().Trim('"', '\'').Trim();
            if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4).Trim();
            if (text.Length < UserKeywordMinLength || text.Length > UserKeywordMaxLength) return null;
            foreach (char c in text)
            {
                if (char.IsControl(c)) return null;
            }
            return text.ToLowerInvariant();
        }

        internal static bool DescriptionLooksInputAudio(string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                return false;
            }
            string toLowerInvariantResult = description.ToLowerInvariant();
            string[] descriptionWords = DescriptionWords;
            foreach (string value in descriptionWords)
            {
                if (toLowerInvariantResult.Contains(value))
                {
                    return true;
                }
            }
            return ContainsUserKeyword(toLowerInvariantResult);
        }

        internal static string[] NameKeywordsForDisplay()
        {
            return (string[])NameKeywords.Clone();
        }

        internal static string[] DescriptionWordsForDisplay()
        {
            return (string[])DescriptionWords.Clone();
        }

        internal static string[] PresentVendorTokensForDisplay()
        {
            return PeripheralVendorProbe.Tokens();
        }

        private static string FileInfoText(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }
            lock (infoCacheSync)
            {
                string value;
                if (infoCache.TryGetValue(path, out value))
                {
                    return value;
                }
            }
            string fileInfoTextResult = "";
            try
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
                fileInfoTextResult = ((versionInfo.FileDescription ?? "") + "\n" + (versionInfo.ProductName ?? "") + "\n" + (versionInfo.CompanyName ?? "")).ToLowerInvariant();
                if (fileInfoTextResult.Length == 2)
                {
                    fileInfoTextResult = "";
                }
            }
            catch
            {
            }
            lock (infoCacheSync)
            {
                if (infoCache.Count > 512)
                {
                    infoCache.Clear();
                }
                infoCache[path] = fileInfoTextResult;
                return fileInfoTextResult;
            }
        }
    }
}
