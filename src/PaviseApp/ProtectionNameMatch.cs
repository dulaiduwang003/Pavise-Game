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
			int num = 0;
			while (num <= value.Length - word.Length)
			{
				int num2 = value.IndexOf(word, num, StringComparison.OrdinalIgnoreCase);
				if (num2 < 0)
				{
					return false;
				}
				int at = num2 + word.Length;
				if (Boundary(value, num2, camelCase) && Boundary(value, at, camelCase))
				{
					return true;
				}
				num = num2 + 1;
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
			string text = WhitelistRule.NormalizeName(name);
			if (!text.StartsWith(product, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text2 = text.Substring(product.Length).TrimStart('.', '-', '_', ' ');
			if (text2.Length == 0)
			{
				return true;
			}
			string[] array = new string[4] { "service", "svc", "engine", "daemon" };
			foreach (string text3 in array)
			{
				if (text2.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
				{
					text2 = text2.Substring(text3.Length).TrimStart('.', '-', '_', ' ');
					break;
				}
			}
			if (text2.Length == 0)
			{
				return true;
			}
			if (text2.Equals("x86", StringComparison.OrdinalIgnoreCase) || text2.Equals("x64", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			string text4 = text2;
			foreach (char c in text4)
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
			string[] array = new string[10] { "update", "upgrade", "install", "setup", "crash", "telemetry", "diagnos", "analytics", "webhelper", "reporter" };
			foreach (string value in array)
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
