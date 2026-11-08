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
			string imagePath = WhitelistRule.NormalizeImagePath(path);
			if (helpers.Contains(name) || HardwareControlCatalog.IsHardwareControlProcess(name))
			{
				return true;
			}
			if (imagePath.Length == 0)
			{
				return false;
			}
			if (KnownWorkPackage(imagePath) || (WhitelistRule.IsUnsafeFamilyAnchor(imagePath) && !WhitelistRule.IsJavaRuntimeHost(imagePath)) || PlatformRuntimePolicy.RuntimePlatform(name) != null || OverlayHostCatalog.ShouldProtectProcess(name, imagePath, true))
			{
				return true;
			}
			long nowResult = Now();
			lock (gate)
			{
				Cached value;
				if (cache.TryGetValue(imagePath, out value) && nowResult < value.Until)
				{
					return value.Denied;
				}
			}
			Metadata metadata = null;
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(imagePath);
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
			bool knownWorkMetadataSucceeded = KnownWorkMetadata(metadata);
			lock (gate)
			{
				if (cache.Count >= 256 && !cache.ContainsKey(imagePath))
				{
					cache.Clear();
				}
				cache[imagePath] = new Cached
				{
					Denied = knownWorkMetadataSucceeded,
					Until = nowResult + ((metadata == null) ? TimeSpan.FromSeconds(30.0).Ticks : TimeSpan.FromMinutes(5.0).Ticks)
				};
				return knownWorkMetadataSucceeded;
			}
		}

		private static bool KnownWorkPackage(string path)
		{
			string toLowerInvariantResult = path.Replace('/', '\\').ToLowerInvariant();
			int matchIndex = toLowerInvariantResult.IndexOf("\\windowsapps\\", StringComparison.Ordinal);
			if (matchIndex < 0)
			{
				return false;
			}
			matchIndex += "\\windowsapps\\".Length;
			int matchIndex2 = toLowerInvariantResult.IndexOf('\\', matchIndex);
			if (matchIndex2 < 0)
			{
				return false;
			}
			string substringResult = toLowerInvariantResult.Substring(matchIndex, matchIndex2 - matchIndex);
			if (!substringResult.StartsWith("openai.codex_", StringComparison.Ordinal))
			{
				return substringResult.StartsWith("openai.chatgpt_", StringComparison.Ordinal);
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
			string trimmed = (facts.Product ?? "").Trim();
			if ((Equal(a, "OpenAI OpCo, LLC") || Equal(a, "OpenAI")) && (Equal(trimmed, "Codex") || Equal(trimmed, "ChatGPT")))
			{
				return true;
			}
			if ((Equal(a, "Google LLC") || Equal(a, "Google Inc.")) && (Equal(trimmed, "Google Chrome") || Equal(trimmed, "Chrome")))
			{
				return true;
			}
			if ((Equal(a, "Mozilla Corporation") || Equal(a, "Mozilla Foundation")) && Equal(trimmed, "Firefox"))
			{
				return true;
			}
			if (Equal(a, "Microsoft Corporation"))
			{
				if (!Equal(trimmed, "Microsoft Edge") && !Equal(trimmed, "Microsoft Edge WebView2") && !Equal(trimmed, "Visual Studio Code") && !Equal(trimmed, "Microsoft Visual Studio") && !trimmed.StartsWith("Microsoft Office", StringComparison.OrdinalIgnoreCase))
				{
					return Equal(trimmed, "Microsoft OneDrive");
				}
				return true;
			}
			if (Equal(a, "Adobe Inc.") || Equal(a, "Adobe Systems Incorporated"))
			{
				if (!trimmed.StartsWith("Adobe Acrobat", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("Adobe Photoshop", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("Adobe Premiere", StringComparison.OrdinalIgnoreCase))
				{
					return trimmed.StartsWith("Adobe After Effects", StringComparison.OrdinalIgnoreCase);
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
			string imagePath = WhitelistRule.NormalizeImagePath(path);
			if (imagePath.Length == 0 || !imagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (WhitelistRule.IsJavaRuntimeHost(imagePath))
			{
				return false;
			}
			return GameSessionDetector.IsLibraryCandidate(Path.GetFileNameWithoutExtension(imagePath), imagePath, windowsRoot);
		}

		internal static string[] FilterGameTargets(string[] paths, bool requireExisting = false)
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (paths != null)
			{
				foreach (string entryPath in paths)
				{
					if ((!requireExisting || ExistingExecutable(entryPath)) && AllowsGameTarget(entryPath))
					{
						hashSet.Add(entryPath);
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
