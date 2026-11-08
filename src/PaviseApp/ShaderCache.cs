using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class ShaderCache
	{
		private static List<string> CacheDirs()
		{
			List<string> list = new List<string>();
			string folderPath = null;
			string folderPath2 = null;
			try
			{
				folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			}
			catch
			{
			}
			try
			{
				folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
			}
			catch
			{
			}
			if (!string.IsNullOrEmpty(folderPath))
			{
				list.Add(Path.Combine(folderPath, "NVIDIA\\DXCache"));
				list.Add(Path.Combine(folderPath, "NVIDIA\\GLCache"));
				list.Add(Path.Combine(folderPath, "Temp\\NVIDIA Corporation\\NV_Cache"));
				list.Add(Path.Combine(folderPath, "D3DSCache"));
				list.Add(Path.Combine(folderPath, "AMD\\DxCache"));
				list.Add(Path.Combine(folderPath, "AMD\\DxcCache"));
				list.Add(Path.Combine(folderPath, "AMD\\Dx9Cache"));
				list.Add(Path.Combine(folderPath, "AMD\\GLCache"));
				list.Add(Path.Combine(folderPath, "AMD\\VkCache"));
				list.Add(Path.Combine(folderPath, "Intel\\ShaderCache"));
			}
			if (!string.IsNullOrEmpty(folderPath2))
			{
				list.Add(Path.Combine(folderPath2, "NVIDIA Corporation\\NV_Cache"));
			}
			return list;
		}

		public static long MeasureBytes()
		{
			long measureBytesResult = 0L;
			foreach (string item in CacheDirs())
			{
				measureBytesResult += CacheSweep.MeasureDir(item);
			}
			return measureBytesResult;
		}

		public static CacheSweep.Result Clean()
		{
			CacheSweep.Result result = new CacheSweep.Result();
			foreach (string item in CacheDirs())
			{
				CacheSweep.Result result2 = CacheSweep.CleanDir(item);
				result.FreedBytes += result2.FreedBytes;
				result.FailedFiles += result2.FailedFiles;
			}
			return result;
		}
	}
}
