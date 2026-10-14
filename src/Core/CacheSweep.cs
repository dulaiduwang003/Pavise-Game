// 作者：bdth 2074055628@qq.com
using System;
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
                var di = new DirectoryInfo(path);
                bool exists;
                if (TryDirectoryChain(di, out exists) && exists) return SizeOf(di);
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
                bool exists;
                if (!TryDirectoryChain(di, out exists)) r.FailedFiles++;
                else if (exists) CleanInner(di, r, false);
            }
            catch { r.FailedFiles++; }
            return r;
        }

        // 枚举缓存前逐级检查目录链接。
        private static bool TryDirectoryChain(DirectoryInfo directory, out bool exists)
        {
            exists = false;
            try
            {
                var parents = new Stack<DirectoryInfo>();
                for (DirectoryInfo current = directory; current != null; current = current.Parent)
                    parents.Push(current);
                while (parents.Count > 0)
                {
                    FileAttributes attributes = File.GetAttributes(parents.Pop().FullName);
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || (attributes & FileAttributes.Directory) == 0) return false;
                }
                exists = true;
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { return false; }
        }

        internal static bool IsReparse(FileSystemInfo fi)
        {
            try { return (fi.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        internal static long SizeOf(DirectoryInfo di)
        {
            bool exists;
            if (!TryDirectoryChain(di, out exists) || !exists) return 0;
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
            bool exists;
            if (!TryDirectoryChain(di, out exists)) { r.FailedFiles++; return; }
            if (!exists) return;
            FileInfo[] files = null;
            DirectoryInfo[] subs = null;
            try { files = di.GetFiles(); subs = di.GetDirectories(); } catch { r.FailedFiles++; }
            if (files != null)
                foreach (FileInfo f in files)
                {
                    if (!TryDirectoryChain(di, out exists)) { r.FailedFiles++; return; }
                    if (!exists) return;
                    f.Refresh();
                    if (IsReparse(f)) { r.FailedFiles++; continue; }
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
                    else r.FailedFiles++;
                }
            if (deleteSelf)
            {
                if (!TryDirectoryChain(di, out exists)) { r.FailedFiles++; return; }
                if (exists) { try { di.Delete(false); } catch { } }
            }
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
