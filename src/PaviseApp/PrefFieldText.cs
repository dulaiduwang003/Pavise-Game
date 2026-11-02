using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class PrefFieldText
	{
		public static string MergeField(string current, string field, string value)
		{
			List<string> list = new List<string>();
			bool flag = false;
			if (!string.IsNullOrEmpty(current))
			{
				string[] array = current.Split(';');
				foreach (string text in array)
				{
					string text2 = text.Trim();
					if (text2.Length == 0)
					{
						continue;
					}
					int num = text2.IndexOf('=');
					string a = ((num > 0) ? text2.Substring(0, num).Trim() : text2);
					if (num > 0 && string.Equals(a, field, StringComparison.OrdinalIgnoreCase))
					{
						if (!flag)
						{
							list.Add(field + "=" + value);
							flag = true;
						}
					}
					else
					{
						list.Add(text2);
					}
				}
			}
			if (!flag)
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
				string[] array = current.Split(';');
				foreach (string text in array)
				{
					string text2 = text.Trim();
					if (text2.Length != 0)
					{
						int num = text2.IndexOf('=');
						string a = ((num > 0) ? text2.Substring(0, num).Trim() : text2);
						if (num <= 0 || !string.Equals(a, field, StringComparison.OrdinalIgnoreCase))
						{
							list.Add(text2);
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
			string[] array = current.Split(';');
			foreach (string text in array)
			{
				string text2 = text.Trim();
				int num = text2.IndexOf('=');
				if (num > 0 && string.Equals(text2.Substring(0, num).Trim(), field, StringComparison.OrdinalIgnoreCase))
				{
					return text2.Substring(num + 1).Trim();
				}
			}
			return null;
		}

		public static string RestoreField(string current, string original, string field)
		{
			string text = ReadField(original, field);
			if (text != null)
			{
				return MergeField(current, field, text);
			}
			return RemoveField(current, field);
		}
	}
}
