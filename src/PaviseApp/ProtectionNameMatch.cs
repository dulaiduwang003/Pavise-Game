using System;

namespace PaviseApp
{
	internal static class ProtectionNameMatch
	{
		internal static bool Word(string value, string word, bool camelCase)
		{
			if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(word))
			{
				return false;
			}
			int wordValue = 0;
			while (wordValue <= value.Length - word.Length)
			{
				int matchIndex = value.IndexOf(word, wordValue, StringComparison.OrdinalIgnoreCase);
				if (matchIndex < 0)
				{
					return false;
				}
				int at = matchIndex + word.Length;
				if (Boundary(value, matchIndex, camelCase) && Boundary(value, at, camelCase))
				{
					return true;
				}
				wordValue = matchIndex + 1;
			}
			return false;
		}

		private static bool Boundary(string value, int at, bool camelCase)
		{
			if (at == 0 || at == value.Length)
			{
				return true;
			}
			char c = value[at - 1];
			char c2 = value[at];
			if (!char.IsLetter(c) || !char.IsLetter(c2))
			{
				return true;
			}
			if (camelCase && char.IsUpper(c2))
			{
				if (!char.IsLower(c))
				{
					if (at + 1 < value.Length)
					{
						return char.IsLower(value[at + 1]);
					}
					return false;
				}
				return true;
			}
			return false;
		}

		internal static bool RuntimeVariant(string name, string product)
		{
			string name2 = WhitelistRule.NormalizeName(name);
			if (!name2.StartsWith(product, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string substringTrimmed = name2.Substring(product.Length).TrimStart('.', '-', '_', ' ');
			if (substringTrimmed.Length == 0)
			{
				return true;
			}
			string[] textEntries = new string[4] { "service", "svc", "engine", "daemon" };
			foreach (string entryText in textEntries)
			{
				if (substringTrimmed.StartsWith(entryText, StringComparison.OrdinalIgnoreCase))
				{
					substringTrimmed = substringTrimmed.Substring(entryText.Length).TrimStart('.', '-', '_', ' ');
					break;
				}
			}
			if (substringTrimmed.Length == 0)
			{
				return true;
			}
			if (substringTrimmed.Equals("x86", StringComparison.OrdinalIgnoreCase) || substringTrimmed.Equals("x64", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string message = substringTrimmed;
			foreach (char c in message)
			{
				if (!char.IsDigit(c) && c != '.')
				{
					return false;
				}
			}
			return true;
		}

		internal static bool Auxiliary(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}
			string[] textEntries = new string[10] { "update", "upgrade", "install", "setup", "crash", "telemetry", "diagnos", "analytics", "webhelper", "reporter" };
			foreach (string value in textEntries)
			{
				if (name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}
	}
}
