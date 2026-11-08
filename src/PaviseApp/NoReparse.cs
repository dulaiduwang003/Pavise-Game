using System.IO;

namespace PaviseApp
{
	internal static class NoReparse
	{
		internal static void CheckPath(string path)
		{
			string fullPath = Path.GetFullPath(path);
			while (!string.IsNullOrEmpty(fullPath))
			{
				if ((File.Exists(fullPath) || Directory.Exists(fullPath)) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
				{
					throw new InvalidDataException("Path crosses a reparse point");
				}
				fullPath = Path.GetDirectoryName(fullPath);
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
				byte[] rawBytes = new byte[(int)fileStream.Length];
				int bytesValue;
				for (int i = 0; i < rawBytes.Length; i += bytesValue)
				{
					bytesValue = fileStream.Read(rawBytes, i, rawBytes.Length - i);
					if (bytesValue <= 0)
					{
						throw new EndOfStreamException();
					}
				}
				return rawBytes;
			}
		}
	}
}
