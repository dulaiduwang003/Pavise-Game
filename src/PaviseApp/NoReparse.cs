using System.IO;

namespace PaviseApp
{
	internal static class NoReparse
	{
		internal static void CheckPath(string path)
		{
			string text = Path.GetFullPath(path);
			while (!string.IsNullOrEmpty(text))
			{
				if ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
				{
					throw new InvalidDataException("Path crosses a reparse point");
				}
				text = Path.GetDirectoryName(text);
			}
		}

		internal static byte[] ReadBytes(string path, int maximum)
		{
			CheckPath(path);
			using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				if (fileStream.Length > maximum)
				{
					throw new InvalidDataException("File too large");
				}
				byte[] array = new byte[(int)fileStream.Length];
				int num;
				for (int i = 0; i < array.Length; i += num)
				{
					num = fileStream.Read(array, i, array.Length - i);
					if (num <= 0)
					{
						throw new EndOfStreamException();
					}
				}
				return array;
			}
		}
	}
}
