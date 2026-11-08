using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
	internal static class PeripheralCatalog
	{
		private static readonly string[] NameKeywords = new string[77]
		{
			"keyboard", "mouse", "hotkey", "keymap", "macro", "autohotkey", "hid", "input", "ime", "pinyin",
			"qqpy", "sogou", "sgtool", "wetype", "iflyime", "wubi", "audio", "sound", "voice", "headset",
			"headphone", "bluetooth", "nahimic", "realtek", "rtkaud", "creative", "dolby", "waves", "sonic", "equalizer",
			"eartrumpet", "wavelink", "goxlr", "logi", "lghub", "lcore", "razer", "synapse", "icue", "corsair",
			"steelseries", "armoury", "wooting", "keychron", "dareu", "rapoo", "hyperx", "ngenuity", "epos", "sennheiser",
			"astro", "turtlebeach", "edifier", "hecate", "elgato", "roccat", "bloody", "a4tech", "vgn", "langtu",
			"gamepp", "gamepad", "joystick", "ds4windows", "rewasd", "xoutput", "joytokey", "x360ce", "dualsensex", "betterjoy",
			"antimicro", "inputmapper", "keysticks", "osk", "tabtip", "listary", "wootility"
		};

		private static readonly string[] DescriptionWords = new string[43]
		{
			"keyboard", "mouse", "headset", "earphone", "headphone", "earbud", "speaker", "audio", "sound", "voice",
			"microphone", "bluetooth", "dolby", "equalizer", "input method", "ime", "hotkey", "macro", "peripheral", "gamepad",
			"joystick", "controller", "hid", "键盘", "鼠标", "耳机", "耳麦", "音频", "声卡", "音箱",
			"扬声器", "蓝牙", "降噪", "音效", "麦克", "输入法", "按键", "外设", "手柄", "灯效",
			"搜狗", "讯飞输入", "漫步者"
		};

		private static readonly object infoCacheSync = new object();

		private static readonly Dictionary<string, string> infoCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		internal static bool IsInputChainProcess(string name, string path)
		{
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
			return false;
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
			return false;
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
