using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class CacheSweep
	{
		internal sealed class Result
		{
			public long FreedBytes;

			public int FailedFiles;
		}

		public static long MeasureDir(string path)
		{
			try
			{
				DirectoryInfo directoryInfo = new DirectoryInfo(path);
				bool exists;
				if (TryDirectoryChain(directoryInfo, out exists) && exists)
				{
					return SizeOf(directoryInfo);
				}
			}
			catch
			{
			}
			return 0L;
		}

		public static Result CleanDir(string path)
		{
			Result result = new Result();
			try
			{
				DirectoryInfo directoryInfo = new DirectoryInfo(path);
				bool exists;
				if (!TryDirectoryChain(directoryInfo, out exists))
				{
					result.FailedFiles++;
				}
				else if (exists)
				{
					CleanInner(directoryInfo, result, false);
				}
			}
			catch
			{
				result.FailedFiles++;
			}
			return result;
		}

		private static bool TryDirectoryChain(DirectoryInfo directory, out bool exists)
		{
			exists = false;
			try
			{
				Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
				for (DirectoryInfo directoryInfo = directory; directoryInfo != null; directoryInfo = directoryInfo.Parent)
				{
					stack.Push(directoryInfo);
				}
				while (stack.Count > 0)
				{
					FileAttributes attributes = File.GetAttributes(stack.Pop().FullName);
					if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
					{
						return false;
					}
				}
				exists = true;
				return true;
			}
			catch (FileNotFoundException)
			{
				return true;
			}
			catch (DirectoryNotFoundException)
			{
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsReparse(FileSystemInfo fi)
		{
			try
			{
				return (fi.Attributes & FileAttributes.ReparsePoint) != 0;
			}
			catch
			{
				return true;
			}
		}

		internal static long SizeOf(DirectoryInfo di)
		{
			bool exists;
			if (!TryDirectoryChain(di, out exists) || !exists)
			{
				return 0L;
			}
			long num = 0L;
			FileInfo[] array = null;
			DirectoryInfo[] array2 = null;
			try
			{
				array = di.GetFiles();
				array2 = di.GetDirectories();
			}
			catch
			{
			}
			if (array != null)
			{
				FileInfo[] array3 = array;
				foreach (FileInfo fileInfo in array3)
				{
					try
					{
						if (!IsReparse(fileInfo))
						{
							num += fileInfo.Length;
						}
					}
					catch
					{
					}
				}
			}
			if (array2 != null)
			{
				DirectoryInfo[] array4 = array2;
				foreach (DirectoryInfo directoryInfo in array4)
				{
					if (!IsReparse(directoryInfo))
					{
						num += SizeOf(directoryInfo);
					}
				}
			}
			return num;
		}

		internal static void CleanInner(DirectoryInfo di, Result r, bool deleteSelf)
		{
			bool exists;
			if (!TryDirectoryChain(di, out exists))
			{
				r.FailedFiles++;
			}
			else
			{
				if (!exists)
				{
					return;
				}
				FileInfo[] array = null;
				DirectoryInfo[] array2 = null;
				try
				{
					array = di.GetFiles();
					array2 = di.GetDirectories();
				}
				catch
				{
					r.FailedFiles++;
				}
				if (array != null)
				{
					FileInfo[] array3 = array;
					foreach (FileInfo fileInfo in array3)
					{
						if (!TryDirectoryChain(di, out exists))
						{
							r.FailedFiles++;
							return;
						}
						if (!exists)
						{
							return;
						}
						fileInfo.Refresh();
						if (IsReparse(fileInfo))
						{
							r.FailedFiles++;
							continue;
						}
						long num = 0L;
						try
						{
							num = fileInfo.Length;
							fileInfo.Delete();
							r.FreedBytes += num;
						}
						catch
						{
							try
							{
								fileInfo.Attributes = FileAttributes.Normal;
								fileInfo.Delete();
								r.FreedBytes += num;
							}
							catch
							{
								r.FailedFiles++;
							}
						}
					}
				}
				if (array2 != null)
				{
					DirectoryInfo[] array4 = array2;
					foreach (DirectoryInfo directoryInfo in array4)
					{
						directoryInfo.Refresh();
						if (!IsReparse(directoryInfo))
						{
							CleanInner(directoryInfo, r, true);
						}
						else
						{
							r.FailedFiles++;
						}
					}
				}
				if (!deleteSelf)
				{
					return;
				}
				if (!TryDirectoryChain(di, out exists))
				{
					r.FailedFiles++;
				}
				else if (exists)
				{
					try
					{
						di.Delete(false);
					}
					catch
					{
					}
				}
			}
		}

		public static string FmtBytes(long b)
		{
			if (b >= 1073741824)
			{
				return ((double)b / 1073741824.0).ToString("0.00") + " GB";
			}
			if (b >= 1048576)
			{
				return ((double)b / 1048576.0).ToString("0.0") + " MB";
			}
			if (b >= 1024)
			{
				return b / 1024 + " KB";
			}
			return b + " B";
		}
	}
}
