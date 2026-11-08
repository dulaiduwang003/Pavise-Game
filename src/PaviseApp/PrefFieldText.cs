using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class PrefFieldText
	{
		public static string MergeField(string current, string field, string value)
		{
			List<string> list = new List<string>();
			bool mergeFieldReady = false;
			if (!string.IsNullOrEmpty(current))
			{
				string[] currentParts = current.Split(';');
				foreach (string entryText in currentParts)
				{
					string entryTextTrimmed = entryText.Trim();
					if (entryTextTrimmed.Length == 0)
					{
						continue;
					}
					int matchIndex = entryTextTrimmed.IndexOf('=');
					string a = ((matchIndex > 0) ? entryTextTrimmed.Substring(0, matchIndex).Trim() : entryTextTrimmed);
					if (matchIndex > 0 && string.Equals(a, field, StringComparison.OrdinalIgnoreCase))
					{
						if (!mergeFieldReady)
						{
							list.Add(field + "=" + value);
							mergeFieldReady = true;
						}
					}
					else
					{
						list.Add(entryTextTrimmed);
					}
				}
			}
			if (!mergeFieldReady)
			{
				list.Add(field + "=" + value);
			}
			return string.Join(";", list.ToArray()) + ";";
		}

		public static string RemoveField(string current, string field)
		{
			List<string> list = new List<string>();
			if (!string.IsNullOrEmpty(current))
			{
				string[] currentParts = current.Split(';');
				foreach (string entryText in currentParts)
				{
					string entryTextTrimmed = entryText.Trim();
					if (entryTextTrimmed.Length != 0)
					{
						int matchIndex = entryTextTrimmed.IndexOf('=');
						string a = ((matchIndex > 0) ? entryTextTrimmed.Substring(0, matchIndex).Trim() : entryTextTrimmed);
						if (matchIndex <= 0 || !string.Equals(a, field, StringComparison.OrdinalIgnoreCase))
						{
							list.Add(entryTextTrimmed);
						}
					}
				}
			}
			if (list.Count == 0)
			{
				return "";
			}
			return string.Join(";", list.ToArray()) + ";";
		}

		public static string ReadField(string current, string field)
		{
			if (string.IsNullOrEmpty(current))
			{
				return null;
			}
			string[] currentParts = current.Split(';');
			foreach (string entryText in currentParts)
			{
				string entryTextTrimmed = entryText.Trim();
				int matchIndex = entryTextTrimmed.IndexOf('=');
				if (matchIndex > 0 && string.Equals(entryTextTrimmed.Substring(0, matchIndex).Trim(), field, StringComparison.OrdinalIgnoreCase))
				{
					return entryTextTrimmed.Substring(matchIndex + 1).Trim();
				}
			}
			return null;
		}

		public static string RestoreField(string current, string original, string field)
		{
			string field2 = ReadField(original, field);
			if (field2 != null)
			{
				return MergeField(current, field, field2);
			}
			return RemoveField(current, field);
		}
	}
}
