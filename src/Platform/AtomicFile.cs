// @author bdth 2074055628@qq.com
// 文件用途 以先写临时文件再替换的方式保证配置和日志不会被写坏

using System;
using System.IO;
using System.Text;

namespace PaviseApp
{

    internal static class AtomicFile
    {
        public static bool WriteLines(string path, string[] lines, string label)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string tmp = path + ".tmp";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            bool tmpComplete = false;
            try
            {
                File.WriteAllLines(tmp, lines ?? new string[0], new UTF8Encoding(false));
                tmpComplete = true;
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {

                try
                {
                    if (tmpComplete && File.Exists(tmp))
                    {
                        File.Copy(tmp, path, true);
                        try { File.Delete(tmp); } catch { }
                        return true;
                    }
                }
                catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                Logger.LogFailure(label + "写入失败", ex);
                return false;
            }
        }
    }
}
