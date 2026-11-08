using System;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class WhitelistRule
	{
		internal const string Header = "PAVISE_WHITELIST_V4";

		internal const string PreviousHeader = "PAVISE_WHITELIST_V3";

		internal const string LegacyHeader = "PAVISE_WHITELIST_V2";

		private static readonly string[] UnsafeFamilyHosts = new string[45]
		{
			"explorer", "cmd", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
			"conhost", "openconsole", "windowsterminal", "dllhost", "svchost", "taskhostw", "taskeng", "runtimebroker", "applicationframehost", "msiexec",
			"wsl", "wslhost", "wslservice", "bash", "sh", "python", "pythonw", "py", "pyw", "pypy",
			"pypy3", "node", "deno", "bun", "java", "javaw", "dotnet", "php", "php-win", "perl",
			"ruby", "rubyw", "lua", "csi", "msbuild"
		};

		private static readonly string[] VersionedRuntimeHostPrefixes = new string[14]
		{
			"python", "pythonw", "pyw", "pypy", "node", "java", "javaw", "dotnet", "php", "php-win",
			"perl", "ruby", "rubyw", "lua"
		};

		public readonly WhitelistRuleKind Kind;

		public readonly string Value;

		public string Key
		{
			get
			{
				return (int)Kind + ":" + Value;
			}
		}

		private WhitelistRule(WhitelistRuleKind kind, string value)
		{
			Kind = kind;
			Value = value;
		}

		public static bool TryCreate(WhitelistRuleKind kind, string value, out WhitelistRule rule)
		{
			rule = null;
			string name = ((kind == WhitelistRuleKind.LegacyName) ? NormalizeName(value) : NormalizeImagePath(value));
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (kind != 0 && !string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (kind == WhitelistRuleKind.ApplicationFamily && IsUnsafeFamilyAnchor(name))
			{
				return false;
			}
			rule = new WhitelistRule(kind, name);
			return true;
		}

		public static bool TryParseVersioned(string line, out WhitelistRule rule)
		{
			rule = null;
			if (string.IsNullOrWhiteSpace(line))
			{
				return false;
			}
			int matchIndex = line.IndexOf('|');
			if (matchIndex != 1 || line.Length <= 2)
			{
				return false;
			}
			WhitelistRuleKind kind;
			switch (char.ToUpperInvariant(line[0]))
			{
			case 'N':
				kind = WhitelistRuleKind.LegacyName;
				break;
			case 'P':
				kind = WhitelistRuleKind.ExactPath;
				break;
			case 'F':
				kind = WhitelistRuleKind.ApplicationFamily;
				break;
			default:
				return false;
			}
			string @string;
			try
			{
				@string = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(2)));
			}
			catch
			{
				return false;
			}
			return TryCreate(kind, @string, out rule);
		}

		public string Serialize()
		{
			char c = ((Kind == WhitelistRuleKind.LegacyName) ? 'N' : ((Kind == WhitelistRuleKind.ExactPath) ? 'P' : 'F'));
			return c + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Value));
		}

		internal bool MatchesNormalized(string normalizedName, string normalizedImagePath)
		{
			if (Kind != 0)
			{
				if (!string.IsNullOrEmpty(normalizedImagePath))
				{
					return string.Equals(Value, normalizedImagePath, StringComparison.OrdinalIgnoreCase);
				}
				return false;
			}
			return string.Equals(Value, normalizedName, StringComparison.OrdinalIgnoreCase);
		}

		internal static string NormalizeName(string value)
		{
			string trimmed = (value ?? "").Trim();
			if (trimmed.Length == 0)
			{
				return "";
			}
			try
			{
				string fileName = Path.GetFileName(trimmed);
				if (!string.IsNullOrEmpty(fileName))
				{
					trimmed = fileName;
				}
			}
			catch
			{
			}
			if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				trimmed = trimmed.Substring(0, trimmed.Length - 4);
			}
			return trimmed.Trim();
		}

		internal static string NormalizeImagePath(string value)
		{
			string trimTrimmed = (value ?? "").Trim().Trim('"');
			if (trimTrimmed.Length == 0 || trimTrimmed.IndexOf('\0') >= 0)
			{
				return "";
			}
			trimTrimmed = trimTrimmed.Replace('/', '\\');
			if (trimTrimmed.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
			{
				trimTrimmed = "\\\\" + trimTrimmed.Substring(8);
			}
			else if (trimTrimmed.StartsWith("\\??\\UNC\\", StringComparison.OrdinalIgnoreCase))
			{
				trimTrimmed = "\\\\" + trimTrimmed.Substring(8);
			}
			else if (trimTrimmed.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
			{
				string substringResult = trimTrimmed.Substring(4);
				if (substringResult.Length < 3 || !char.IsLetter(substringResult[0]) || substringResult[1] != ':' || substringResult[2] != '\\')
				{
					return "";
				}
				trimTrimmed = substringResult;
			}
			else if (trimTrimmed.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase))
			{
				string substringResult2 = trimTrimmed.Substring(4);
				if (substringResult2.Length < 3 || !char.IsLetter(substringResult2[0]) || substringResult2[1] != ':' || substringResult2[2] != '\\')
				{
					return "";
				}
				trimTrimmed = substringResult2;
			}
			if (trimTrimmed.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
			{
				return "";
			}
			bool allConditionsMet = trimTrimmed.Length >= 3 && char.IsLetter(trimTrimmed[0]) && trimTrimmed[1] == ':' && trimTrimmed[2] == '\\';
			bool matchesStartsWith = trimTrimmed.StartsWith("\\\\", StringComparison.Ordinal) && trimTrimmed.Length > 3;
			if (!allConditionsMet && !matchesStartsWith)
			{
				return "";
			}
			try
			{
				trimTrimmed = Path.GetFullPath(trimTrimmed);
			}
			catch
			{
				return "";
			}
			string fileName;
			try
			{
				fileName = Path.GetFileName(trimTrimmed);
			}
			catch
			{
				return "";
			}
			if (!string.IsNullOrEmpty(fileName))
			{
				return trimTrimmed;
			}
			return "";
		}

		internal static bool PathEquals(string left, string right)
		{
			string imagePath = NormalizeImagePath(left);
			string imagePath2 = NormalizeImagePath(right);
			if (imagePath.Length > 0 && imagePath2.Length > 0)
			{
				return string.Equals(imagePath, imagePath2, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		internal static bool IsUnsafeFamilyAnchor(string imagePath)
		{
			string path = NormalizeImagePath(imagePath);
			string fileNameWithoutExtension;
			try
			{
				fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
			}
			catch
			{
				return true;
			}
			string[] unsafeFamilyHosts = UnsafeFamilyHosts;
			foreach (string b in unsafeFamilyHosts)
			{
				if (string.Equals(fileNameWithoutExtension, b, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			string[] versionedRuntimeHostPrefixes = VersionedRuntimeHostPrefixes;
			foreach (string prefix in versionedRuntimeHostPrefixes)
			{
				if (HasVersionSuffix(fileNameWithoutExtension, prefix))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsJavaRuntimeHost(string imagePath)
		{
			string fileNameWithoutExtension;
			try
			{
				fileNameWithoutExtension = Path.GetFileNameWithoutExtension(NormalizeImagePath(imagePath));
			}
			catch
			{
				return false;
			}
			if (!string.Equals(fileNameWithoutExtension, "java", StringComparison.OrdinalIgnoreCase) && !string.Equals(fileNameWithoutExtension, "javaw", StringComparison.OrdinalIgnoreCase) && !HasVersionSuffix(fileNameWithoutExtension, "java"))
			{
				return HasVersionSuffix(fileNameWithoutExtension, "javaw");
			}
			return true;
		}

		private static bool HasVersionSuffix(string name, string prefix)
		{
			if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length)
			{
				return false;
			}
			for (int i = prefix.Length; i < name.Length; i++)
			{
				char c = name[i];
				if (!char.IsDigit(c) && c != '.' && c != '-' && c != '_')
				{
					return false;
				}
			}
			return true;
		}
	}
}
