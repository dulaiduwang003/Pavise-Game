// @author bdth 2074055628@qq.com
// 文件用途 定位 测量并清理显卡着色器缓存

using System;
using System.Collections.Generic;
using System.IO;

namespace AegisApp
{
    internal static class ShaderCache
    {
        private static List<string> CacheDirs()
        {
            var dirs = new List<string>();
            string local = null, progData = null;
            try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }
            try { progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } catch { }
            if (!string.IsNullOrEmpty(local))
            {
                dirs.Add(Path.Combine(local, @"NVIDIA\DXCache"));
                dirs.Add(Path.Combine(local, @"NVIDIA\GLCache"));
                dirs.Add(Path.Combine(local, @"Temp\NVIDIA Corporation\NV_Cache"));
                dirs.Add(Path.Combine(local, "D3DSCache"));
                dirs.Add(Path.Combine(local, @"AMD\DxCache"));
                dirs.Add(Path.Combine(local, @"AMD\DxcCache"));
                dirs.Add(Path.Combine(local, @"AMD\Dx9Cache"));
                dirs.Add(Path.Combine(local, @"AMD\GLCache"));
                dirs.Add(Path.Combine(local, @"AMD\VkCache"));
                dirs.Add(Path.Combine(local, @"Intel\ShaderCache"));
            }
            if (!string.IsNullOrEmpty(progData))
                dirs.Add(Path.Combine(progData, @"NVIDIA Corporation\NV_Cache"));
            return dirs;
        }

        public static long MeasureBytes()
        {
            long total = 0;
            foreach (string d in CacheDirs()) total += CacheSweep.MeasureDir(d);
            return total;
        }

        public static CacheSweep.Result Clean()
        {
            var r = new CacheSweep.Result();
            foreach (string d in CacheDirs())
            {
                CacheSweep.Result one = CacheSweep.CleanDir(d);
                r.FreedBytes += one.FreedBytes;
                r.FailedFiles += one.FailedFiles;
            }
            return r;
        }

    }
}
