using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
	internal static class GamePerformanceTargetPolicy
	{
		internal sealed class Metadata
		{
			internal string Company;

			internal string Product;

			internal string Description;

			internal string Original;
		}

		private sealed class Cached
		{
			internal bool Denied;

			internal long Until;
		}

		private static readonly object gate = new object();

		private static readonly string windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/') + "\\";

		private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

		private static readonly HashSet<string> helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"UnityCrashHandler32", "UnityCrashHandler64", "crashpad_handler", "WerFault", "WerFaultSecure", "QtWebEngineProcess", "cef_subprocess", "msedgewebview2", "DXSETUP", "vcredist_x86",
			"vcredist_x64"
		};

		private static long Now()
		{
			return DateTime.UtcNow.Ticks;
		}

		internal static bool KnownNonGame(string name, string path)
		{
			name = WhitelistRule.NormalizeName(name);
			string text = WhitelistRule.NormalizeImagePath(path);
			if (helpers.Contains(name) || HardwareControlCatalog.IsHardwareControlProcess(name))
			{
				return true;
			}
			if (text.Length == 0)
			{
				return false;
			}
			if (KnownWorkPackage(text) || (WhitelistRule.IsUnsafeFamilyAnchor(text) && !WhitelistRule.IsJavaRuntimeHost(text)) || PlatformRuntimePolicy.RuntimePlatform(name) != null || OverlayHostCatalog.ShouldProtectProcess(name, text, true))
			{
				return true;
			}
			long num = Now();
			lock (gate)
			{
				Cached value;
				if (cache.TryGetValue(text, out value) && num < value.Until)
				{
					return value.Denied;
				}
			}
			Metadata metadata = null;
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(text);
				Metadata metadata2 = new Metadata();
				metadata2.Company = versionInfo.CompanyName;
				metadata2.Product = versionInfo.ProductName;
				metadata2.Description = versionInfo.FileDescription;
				metadata2.Original = versionInfo.OriginalFilename;
				metadata = metadata2;
			}
			catch
			{
			}
			bool flag = KnownWorkMetadata(metadata);
			lock (gate)
			{
				if (cache.Count >= 256 && !cache.ContainsKey(text))
				{
					cache.Clear();
				}
				cache[text] = new Cached
				{
					Denied = flag,
					Until = num + ((metadata == null) ? TimeSpan.FromSeconds(30.0).Ticks : TimeSpan.FromMinutes(5.0).Ticks)
				};
				return flag;
			}
		}

		private static bool KnownWorkPackage(string path)
		{
			string text = path.Replace('/', '\\').ToLowerInvariant();
			int num = text.IndexOf("\\windowsapps\\", StringComparison.Ordinal);
			if (num < 0)
			{
				return false;
			}
			num += "\\windowsapps\\".Length;
			int num2 = text.IndexOf('\\', num);
			if (num2 < 0)
			{
				return false;
			}
			string text2 = text.Substring(num, num2 - num);
			if (!text2.StartsWith("openai.codex_", StringComparison.Ordinal))
			{
				return text2.StartsWith("openai.chatgpt_", StringComparison.Ordinal);
			}
			return true;
		}

		internal static bool KnownWorkMetadata(Metadata facts)
		{
			if (facts == null)
			{
				return false;
			}
			string a = (facts.Company ?? "").Trim();
			string text = (facts.Product ?? "").Trim();
			if ((Equal(a, "OpenAI OpCo, LLC") || Equal(a, "OpenAI")) && (Equal(text, "Codex") || Equal(text, "ChatGPT")))
			{
				return true;
			}
			if ((Equal(a, "Google LLC") || Equal(a, "Google Inc.")) && (Equal(text, "Google Chrome") || Equal(text, "Chrome")))
			{
				return true;
			}
			if ((Equal(a, "Mozilla Corporation") || Equal(a, "Mozilla Foundation")) && Equal(text, "Firefox"))
			{
				return true;
			}
			if (Equal(a, "Microsoft Corporation"))
			{
				if (!Equal(text, "Microsoft Edge") && !Equal(text, "Microsoft Edge WebView2") && !Equal(text, "Visual Studio Code") && !Equal(text, "Microsoft Visual Studio") && !text.StartsWith("Microsoft Office", StringComparison.OrdinalIgnoreCase))
				{
					return Equal(text, "Microsoft OneDrive");
				}
				return true;
			}
			if (Equal(a, "Adobe Inc.") || Equal(a, "Adobe Systems Incorporated"))
			{
				if (!text.StartsWith("Adobe Acrobat", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Adobe Photoshop", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Adobe Premiere", StringComparison.OrdinalIgnoreCase))
				{
					return text.StartsWith("Adobe After Effects", StringComparison.OrdinalIgnoreCase);
				}
				return true;
			}
			return false;
		}

		private static bool Equal(string a, string b)
		{
			return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}

		internal static bool AllowsGameTarget(string path)
		{
			string text = WhitelistRule.NormalizeImagePath(path);
			if (text.Length == 0 || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (WhitelistRule.IsJavaRuntimeHost(text))
			{
				return false;
			}
			return GameSessionDetector.IsLibraryCandidate(Path.GetFileNameWithoutExtension(text), text, windowsRoot);
		}

		internal static string[] FilterGameTargets(string[] paths, bool requireExisting = false)
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (paths != null)
			{
				foreach (string text in paths)
				{
					if ((!requireExisting || ExistingExecutable(text)) && AllowsGameTarget(text))
					{
						hashSet.Add(text);
					}
				}
			}
			return new List<string>(hashSet).ToArray();
		}

		private static bool ExistingExecutable(string path)
		{
			try
			{
				return !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(GameProfileStore.NormalizePath(path), path, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
			}
			catch
			{
				return false;
			}
		}
	}
}
