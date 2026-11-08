using System;
using System.Text;

namespace PaviseApp
{
	internal static class GameSearchMatcher
	{
		internal static bool Matches(string query, string name, string path)
		{
			string compatibleTextTrimmed = CompatibleText(query).Trim();
			if (compatibleTextTrimmed.Length == 0)
			{
				return true;
			}
			string compatibleTextResult = CompatibleText(name);
			string compatibleTextResult2 = CompatibleText(path);
			string compactResult = Compact(compatibleTextResult);
			string compactResult2 = Compact(compatibleTextResult2);
			bool matchesReady = false;
			string[] compatibleTextTrimmedParts = compatibleTextTrimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			foreach (string value in compatibleTextTrimmedParts)
			{
				string compactResult3 = Compact(value);
				if (compactResult3.Length != 0)
				{
					matchesReady = true;
					if (compactResult.IndexOf(compactResult3, StringComparison.OrdinalIgnoreCase) < 0 && compactResult2.IndexOf(compactResult3, StringComparison.OrdinalIgnoreCase) < 0)
					{
						return false;
					}
				}
			}
			if (!matchesReady && compatibleTextResult.IndexOf(compatibleTextTrimmed, StringComparison.OrdinalIgnoreCase) < 0)
			{
				return compatibleTextResult2.IndexOf(compatibleTextTrimmed, StringComparison.OrdinalIgnoreCase) >= 0;
			}
			return true;
		}

		private static string CompatibleText(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return "";
			}
			try
			{
				return value.Normalize(NormalizationForm.FormKC);
			}
			catch (ArgumentException)
			{
				return value;
			}
		}

		private static string Compact(string value)
		{
			StringBuilder stringBuilder = new StringBuilder(value.Length);
			foreach (char c in value)
			{
				if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
				{
					stringBuilder.Append(c);
				}
			}
			return stringBuilder.ToString();
		}
	}
}
