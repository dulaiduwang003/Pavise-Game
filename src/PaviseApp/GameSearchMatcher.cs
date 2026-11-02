using System;
using System.Text;

namespace PaviseApp
{
	internal static class GameSearchMatcher
	{
		internal static bool Matches(string query, string name, string path)
		{
			string text = CompatibleText(query).Trim();
			if (text.Length == 0)
			{
				return true;
			}
			string text2 = CompatibleText(name);
			string text3 = CompatibleText(path);
			string text4 = Compact(text2);
			string text5 = Compact(text3);
			bool flag = false;
			string[] array = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			foreach (string value in array)
			{
				string text6 = Compact(value);
				if (text6.Length != 0)
				{
					flag = true;
					if (text4.IndexOf(text6, StringComparison.OrdinalIgnoreCase) < 0 && text5.IndexOf(text6, StringComparison.OrdinalIgnoreCase) < 0)
					{
						return false;
					}
				}
			}
			if (!flag && text2.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
			{
				return text3.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
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
