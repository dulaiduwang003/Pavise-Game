// @author bdth 2074055628@qq.com
// 文件用途 测量并清理限定目录中的缓存内容

using System;
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
                var di = new DirectoryInfo(path);
                if (di.Exists && !IsReparse(di)) return SizeOf(di);
            }
            catch { }
            return 0;
        }

        public static Result CleanDir(string path)
        {
            var r = new Result();
            try
            {
                var di = new DirectoryInfo(path);
                if (di.Exists)
                {
                    di.Refresh();
                    if (IsReparse(di)) r.FailedFiles++;
                    else CleanInner(di, r, false);
                }
            }
            catch { r.FailedFiles++; }
            return r;
        }

        internal static bool IsReparse(FileSystemInfo fi)
        {
            try { return (fi.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        internal static long SizeOf(DirectoryInfo di)
        {
            long sum = 0;
            FileInfo[] files = null;
            DirectoryInfo[] subs = null;
            try { files = di.GetFiles(); subs = di.GetDirectories(); } catch { }
            if (files != null)
                foreach (FileInfo f in files)
                    try { if (!IsReparse(f)) sum += f.Length; } catch { }
            if (subs != null)
                foreach (DirectoryInfo sub in subs)
                    if (!IsReparse(sub)) sum += SizeOf(sub);
            return sum;
        }

        internal static void CleanInner(DirectoryInfo di, Result r, bool deleteSelf)
        {
            FileInfo[] files = null;
            DirectoryInfo[] subs = null;
            try { files = di.GetFiles(); subs = di.GetDirectories(); } catch { r.FailedFiles++; }
            if (files != null)
                foreach (FileInfo f in files)
                {
                    f.Refresh();
                    if (IsReparse(f)) continue;
                    long len = 0;
                    try { len = f.Length; f.Delete(); r.FreedBytes += len; }
                    catch
                    {
                        try { f.Attributes = FileAttributes.Normal; f.Delete(); r.FreedBytes += len; }
                        catch { r.FailedFiles++; }
                    }
                }
            if (subs != null)
                foreach (DirectoryInfo sub in subs)
                {
                    sub.Refresh();
                    if (!IsReparse(sub)) CleanInner(sub, r, true);
                }
            if (deleteSelf) { try { di.Delete(false); } catch { } }
        }

        public static string FmtBytes(long b)
        {
            if (b >= (1L << 30)) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
            if (b >= (1L << 20)) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            if (b >= (1L << 10)) return (b / (1L << 10)) + " KB";
            return b + " B";
        }

    }
}
