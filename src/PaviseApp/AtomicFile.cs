using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class AtomicFile
	{
		public static bool WriteLines(string path, string[] lines, string label)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string text = path + ".tmp";
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
			bool flag = false;
			try
			{
				using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false)))
					{
						string[] array = lines ?? new string[0];
						foreach (string value in array)
						{
							streamWriter.WriteLine(value);
						}
						streamWriter.Flush();
						fileStream.Flush(true);
					}
				}
				flag = true;
				if (!File.Exists(path))
				{
					File.Move(text, path);
					return true;
				}
				int num = 0;
				while (true)
				{
					try
					{
						File.Replace(text, path, null);
						return true;
					}
					catch (IOException)
					{
						if (num >= 2)
						{
							throw;
						}
						Thread.Sleep(80);
					}
					num++;
				}
			}
			catch (Exception error)
			{
				try
				{
					if (flag && File.Exists(text))
					{
						try
						{
							File.Copy(path, path + ".stale.bak", true);
						}
						catch
						{
						}
						File.Copy(text, path, true);
						try
						{
							File.Delete(text);
						}
						catch
						{
						}
						return true;
					}
				}
				catch
				{
				}
				try
				{
					if (File.Exists(text))
					{
						File.Delete(text);
					}
				}
				catch
				{
				}
				Logger.LogFailure(label + Lang.T("log.atomicfile.1"), error);
				return false;
			}
		}
	}
}
