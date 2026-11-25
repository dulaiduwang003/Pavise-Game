using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace PaviseApp
{
    internal static class PeripheralCatalog
    {
        private static readonly string[] NameKeywords = new string[]
        {
            "keyboard", "mouse", "hotkey", "keymap", "macro", "pinyin",
            "qqpy", "sogou", "sgtool", "wetype", "iflyime", "wubi", "audio", "sound", "voice", "headset",
            "headphone", "bluetooth", "nahimic", "realtek", "rtkaud", "dolby", "equalizer",
            "eartrumpet", "wavelink", "goxlr", "lghub", "razer", "synapse", "corsair",
            "steelseries", "armoury", "wooting", "keychron", "dareu", "rapoo", "hyperx", "ngenuity", "sennheiser",
            "turtlebeach", "edifier", "hecate", "elgato", "roccat", "bloody", "a4tech", "langtu",
            "gamepp", "gamepad", "joystick", "ds4windows", "rewasd", "xoutput", "joytokey", "x360ce", "dualsensex", "betterjoy",
            "antimicro", "inputmapper", "keysticks", "tabtip", "listary", "wootility",
            // 键鼠厂商驱动 进程名里常带品牌 不带品牌的靠下面的公司名描述词兜底
            "flydigi", "飞智", "setpoint", "khalmnpr", "glorious", "masterplus", "ducky",
            "madcatz", "fnatic", "sharkoon", "redragon", "ajazz", "akko", "royalkludge", "oemdrv", "darmoshark", "mchose",
            "lamzu", "zowie", "xtrfy", "ninjutso", "machenike", "thunderobot", "motospeed", "fuhlen", "hyeku",
            "durgod", "ikbc", "realforce", "hhkb", "elecom", "drunkdeer", "nuphy", "epomaker", "monsgeek", "melgeek",
            "kzzi", "incott", "synaptics", "syntp", "etdctrl", "kanata", "weasel", "gearlink",
            "atk hub", "atk-hub", "atk v hub", "attackshark", "g-wolves", "devtray", "basecamp", "base camp", "makalu", "displaypad",
            "azeron", "3dxservice", "nzxt", "cam_helper", "kensington", "endgame gear", "endgamegear", "unleashrgb", "adx hub", "mionix",
            "inzone", "quantumengine", "quantumserver", "xencelabs", "simhub",
            // 手柄 方向盘 数位板
            "betop", "gamesir", "8bitdo", "thrustmaster", "fanatec", "pithouse", "simucube", "vigem",
            "wacom", "wtablet", "pentablet", "xppen", "huion", "gaomon", "tabletdriver",
            // 音频设备与声卡控制
            "krisp", "jabra", "plantronics", "shure", "focusrite", "audeze", "beyerdynamic", "steinberg", "rodecentral", "rodeconnect",
            "maono", "fifine", "boom3d", "dtsapo",
            // 外设灯效
            "signalrgb", "openrgb", "rgbfusion", "mysticlight", "polychrome", "lightingservice"
        };

        private static readonly HashSet<string> NameWordKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hid", "osk", "asio", "input", "astro", "waves", "sonic", "cherry", "pulsar",
            "epos", "icue", "lcore", "mnx", "vgn", "itype", "ipoint", "logi"
        };

        private static readonly string[] DriverNamePrefixes = new string[] { "logioptions", "logioverlay" };
        private static readonly HashSet<string> DriverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ATKPackage", "iCUE", "iCUEDevicePluginHost", "LCore"
        };

        private static readonly string[] DescriptionWords = new string[]
        {
            "keyboard", "mouse", "headset", "earphone", "headphone", "earbud", "speaker", "audio", "sound", "voice",
            "microphone", "bluetooth", "dolby", "equalizer", "input method", "hotkey", "macro", "peripheral", "gamepad",
            "joystick", "controller", "hid", "键盘", "鼠标", "耳机", "耳麦", "音频", "声卡", "音箱",
            "扬声器", "蓝牙", "降噪", "音效", "麦克", "输入法", "按键", "外设", "手柄", "灯效",
            "搜狗", "讯飞输入", "漫步者",
            // 外设厂商的公司名或产品名 exe 改名或只叫 xx空间站 xx驱动 时也能认出来
            "flydigi", "飞智", "logitech", "罗技", "razer", "雷蛇", "corsair", "海盗船", "steelseries", "赛睿",
            "hyperx", "glorious", "wooting", "keychron", "8bitdo", "八位堂", "gamesir", "盖世小鸡", "betop", "北通",
            "雷柏", "达尔优", "双飞燕", "血手幽灵", "黑爵", "狼蛛", "珂芝", "迈从", "达摩鲨", "腹灵",
            "杜伽", "宁芝", "黑峡谷", "富勒", "前行者", "thrustmaster", "fanatec", "wacom", "huion", "绘王",
            "xp-pen", "pen tablet", "数位板", "手写板", "touchpad", "触控板", "摇杆", "方向盘", "sennheiser", "jabra",
            "plantronics", "shure", "focusrite", "beyerdynamic", "synaptics", "asio",
            "3dconnexion", "xencelabs", "azeron", "mionix", "kensington", "attack shark", "g-wolves", "endgame gear", "inzone", "creative technology"
        };

        // 内置进程名匹配先移除 login 片段
        private static readonly Regex LoginFragment = new Regex("login", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // 这些进程只是借了外设厂商的名字 内置词不放行 用户关键词照常生效
        private static readonly HashSet<string> NotInputChainNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sogouexplorer"
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
            if (IsNotInputChainName(name))
            {
                return ContainsUserKeyword(FileInfoText(path));
            }
            string fileInfoTextResult = FileInfoText(path);
            if (fileInfoTextResult.Length > 0 && DescriptionLooksInputAudio(fileInfoTextResult))
            {
                return true;
            }
            return false;
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

        internal static bool IsInputAudioLike(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lowered = name.ToLowerInvariant();
            string processName = TrimProcessName(name);
            if (!NotInputChainNames.Contains(processName))
            {
                if (DriverNames.Contains(processName)) return true;

                string builtInName = name;
                string builtInLowered = lowered;
                if (lowered.Contains("login"))
                {
                    builtInName = LoginFragment.Replace(name, " ");
                    builtInLowered = lowered.Replace("login", " ");
                }
                foreach (string keyword in NameKeywords)
                {
                    if (builtInLowered.Contains(keyword)) return true;
                }
                foreach (string prefix in DriverNamePrefixes)
                {
                    if (processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (ContainsNameWord(builtInName)) return true;
            }
            return ContainsUserKeyword(lowered);
        }

        private static bool ContainsNameWord(string name)
        {
            int start = 0;
            for (int end = 1; end <= name.Length; end++)
            {
                if (!IsNameWordBoundary(name, end)) continue;
                if (char.IsLetterOrDigit(name[start])
                    && NameWordKeywords.Contains(name.Substring(start, end - start))) return true;
                start = end;
            }
            return false;
        }

        private static bool IsNameWordBoundary(string name, int index)
        {
            if (index == name.Length) return true;
            char previous = name[index - 1];
            char current = name[index];
            if (!char.IsLetterOrDigit(previous) || !char.IsLetterOrDigit(current)) return true;
            if (char.IsDigit(previous) != char.IsDigit(current)) return true;
            // 驼峰边界包含缩写到普通单词的切换
            return char.IsUpper(current) && (char.IsLower(previous)
                || (char.IsUpper(previous) && index + 1 < name.Length && char.IsLower(name[index + 1])));
        }

        private static bool IsNotInputChainName(string name)
        {
            return NotInputChainNames.Contains(TrimProcessName(name));
        }

        private static string TrimProcessName(string name)
        {
            string text = name.Trim();
            if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4);
            return text;
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
            var names = new List<string>(NameKeywords);
            names.AddRange(NameWordKeywords);
            names.AddRange(DriverNamePrefixes);
            foreach (string name in DriverNames)
            {
                if (!NameWordKeywords.Contains(name)) names.Add(name);
            }
            return names.ToArray();
        }

        internal static string[] DescriptionWordsForDisplay()
        {
            return (string[])DescriptionWords.Clone();
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
