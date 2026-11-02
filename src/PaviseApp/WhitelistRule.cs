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
			string text = ((kind == WhitelistRuleKind.LegacyName) ? NormalizeName(value) : NormalizeImagePath(value));
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			if (kind != 0 && !string.Equals(Path.GetExtension(text), ".exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (kind == WhitelistRuleKind.ApplicationFamily && IsUnsafeFamilyAnchor(text))
			{
				return false;
			}
			rule = new WhitelistRule(kind, text);
			return true;
		}

		public static bool TryParseVersioned(string line, out WhitelistRule rule)
		{
			rule = null;
			if (string.IsNullOrWhiteSpace(line))
			{
				return false;
			}
			int num = line.IndexOf('|');
			if (num != 1 || line.Length <= 2)
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
			string text = (value ?? "").Trim();
			if (text.Length == 0)
			{
				return "";
			}
			try
			{
				string fileName = Path.GetFileName(text);
				if (!string.IsNullOrEmpty(fileName))
				{
					text = fileName;
				}
			}
			catch
			{
			}
			if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				text = text.Substring(0, text.Length - 4);
			}
			return text.Trim();
		}

		internal static string NormalizeImagePath(string value)
		{
			string text = (value ?? "").Trim().Trim('"');
			if (text.Length == 0 || text.IndexOf('\0') >= 0)
			{
				return "";
			}
			text = text.Replace('/', '\\');
			if (text.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
			{
				text = "\\\\" + text.Substring(8);
			}
			else if (text.StartsWith("\\??\\UNC\\", StringComparison.OrdinalIgnoreCase))
			{
				text = "\\\\" + text.Substring(8);
			}
			else if (text.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
			{
				string text2 = text.Substring(4);
				if (text2.Length < 3 || !char.IsLetter(text2[0]) || text2[1] != ':' || text2[2] != '\\')
				{
					return "";
				}
				text = text2;
			}
			else if (text.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase))
			{
				string text3 = text.Substring(4);
				if (text3.Length < 3 || !char.IsLetter(text3[0]) || text3[1] != ':' || text3[2] != '\\')
				{
					return "";
				}
				text = text3;
			}
			if (text.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase))
			{
				return "";
			}
			bool flag = text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && text[2] == '\\';
			bool flag2 = text.StartsWith("\\\\", StringComparison.Ordinal) && text.Length > 3;
			if (!flag && !flag2)
			{
				return "";
			}
			try
			{
				text = Path.GetFullPath(text);
			}
			catch
			{
				return "";
			}
			string fileName;
			try
			{
				fileName = Path.GetFileName(text);
			}
			catch
			{
				return "";
			}
			if (!string.IsNullOrEmpty(fileName))
			{
				return text;
			}
			return "";
		}

		internal static bool PathEquals(string left, string right)
		{
			string text = NormalizeImagePath(left);
			string text2 = NormalizeImagePath(right);
			if (text.Length > 0 && text2.Length > 0)
			{
				return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
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
