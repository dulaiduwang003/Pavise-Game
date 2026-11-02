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
			string text = null;
			string text2 = null;
			try
			{
				text = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			}
			catch
			{
			}
			try
			{
				text2 = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
			}
			catch
			{
			}
			if (!string.IsNullOrEmpty(text))
			{
				list.Add(Path.Combine(text, "NVIDIA\\DXCache"));
				list.Add(Path.Combine(text, "NVIDIA\\GLCache"));
				list.Add(Path.Combine(text, "Temp\\NVIDIA Corporation\\NV_Cache"));
				list.Add(Path.Combine(text, "D3DSCache"));
				list.Add(Path.Combine(text, "AMD\\DxCache"));
				list.Add(Path.Combine(text, "AMD\\DxcCache"));
				list.Add(Path.Combine(text, "AMD\\Dx9Cache"));
				list.Add(Path.Combine(text, "AMD\\GLCache"));
				list.Add(Path.Combine(text, "AMD\\VkCache"));
				list.Add(Path.Combine(text, "Intel\\ShaderCache"));
			}
			if (!string.IsNullOrEmpty(text2))
			{
				list.Add(Path.Combine(text2, "NVIDIA Corporation\\NV_Cache"));
			}
			return list;
		}

		public static long MeasureBytes()
		{
			long num = 0L;
			foreach (string item in CacheDirs())
			{
				num += CacheSweep.MeasureDir(item);
			}
			return num;
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
