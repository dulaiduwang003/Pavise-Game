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
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    foreach (string line in lines ?? new string[0]) sw.WriteLine(line);
                    sw.Flush();
                    fs.Flush(true);
                }
                tmpComplete = true;
                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    return true;
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return true;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 2) throw;
                        System.Threading.Thread.Sleep(80);
                    }
                }
            }
            catch (Exception ex)
            {

                try
                {
                    if (tmpComplete && File.Exists(tmp))
                    {
                        try { File.Copy(path, path + ".stale.bak", true); } catch { }
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
