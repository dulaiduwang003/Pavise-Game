// @author bdth 2074055628@qq.com
// 文件用途 以先写临时文件再替换的方式保证配置和日志不会被写坏

using System;
using System.IO;
using System.Text;

namespace AegisApp
{
    // 直接 File.WriteAllLines 是先截断再写：中途崩溃/断电会留下 0 字节或半截文件。
    // 这些文件里存的是"系统被改成了什么样"和用户配置，写坏就等于永久失去还原依据。
    internal static class AtomicFile
    {
        public static bool WriteLines(string path, string[] lines, string label)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllLines(tmp, lines ?? new string[0], new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                // File.Replace 在 FAT32/exFAT/部分网络位置上不受支持，退回复制覆盖：
                // 原子性弱一些，但比彻底写不进去强
                try
                {
                    if (File.Exists(tmp)) { File.Copy(tmp, path, true); File.Delete(tmp); return true; }
                }
                catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                Logger.LogFailure(label + "写入失败", ex);
                return false;
            }
        }
    }
}
