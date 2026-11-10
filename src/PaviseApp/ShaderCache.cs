using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class ShaderCache
    {
        private static List<string> CacheDirs()
        {
            List<string> combines = new List<string>();
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
                combines.Add(Path.Combine(folderPath, "NVIDIA\\DXCache"));
                combines.Add(Path.Combine(folderPath, "NVIDIA\\GLCache"));
                combines.Add(Path.Combine(folderPath, "Temp\\NVIDIA Corporation\\NV_Cache"));
                combines.Add(Path.Combine(folderPath, "D3DSCache"));
                combines.Add(Path.Combine(folderPath, "AMD\\DxCache"));
                combines.Add(Path.Combine(folderPath, "AMD\\DxcCache"));
                combines.Add(Path.Combine(folderPath, "AMD\\Dx9Cache"));
                combines.Add(Path.Combine(folderPath, "AMD\\GLCache"));
                combines.Add(Path.Combine(folderPath, "AMD\\VkCache"));
                combines.Add(Path.Combine(folderPath, "Intel\\ShaderCache"));
            }
            if (!string.IsNullOrEmpty(folderPath2))
            {
                combines.Add(Path.Combine(folderPath2, "NVIDIA Corporation\\NV_Cache"));
            }
            return combines;
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
                CacheSweep.Result cleanDir = CacheSweep.CleanDir(item);
                result.FreedBytes += cleanDir.FreedBytes;
                result.FailedFiles += cleanDir.FailedFiles;
            }
            return result;
        }
    }
}
