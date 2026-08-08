// @author bdth 2074055628@qq.com
// 文件用途 量化把游戏文件预读进系统缓存后 随机读的真实收益与预热本身的成本

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const int PrewarmFileMb = 32;
        private const int PrewarmFilesPerGroup = 8;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint bytes,
            out uint written, IntPtr overlapped);

        private static void WriteUncached(string path, int mb, Random rnd)
        {
            const uint GenericWrite = 0x40000000;
            const uint CreateAlways = 2;
            const uint NoBuffering = 0x20000000;
            const uint WriteThrough = 0x80000000;
            IntPtr h = CreateFileW(path, GenericWrite, 0, IntPtr.Zero, CreateAlways,
                NoBuffering | WriteThrough, IntPtr.Zero);
            if (h == (IntPtr)(-1)) throw new IOException("CreateFile 失败 " + Marshal.GetLastWin32Error());
            try
            {
                var block = new byte[1024 * 1024];
                rnd.NextBytes(block);
                for (int i = 0; i < mb; i++)
                {
                    uint written;
                    if (!WriteFile(h, block, (uint)block.Length, out written, IntPtr.Zero))
                        throw new IOException("WriteFile 失败 " + Marshal.GetLastWin32Error());
                }
            }
            finally { Native.CloseHandle(h); }
        }

        private static double RandomReadMs(string[] files, int reads, Random rnd)
        {
            var streams = new FileStream[files.Length];
            for (int i = 0; i < files.Length; i++)
                streams[i] = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var buf = new byte[4096];
            var sw = Stopwatch.StartNew();
            try
            {
                long fileBytes = (long)PrewarmFileMb * 1024 * 1024;
                for (int i = 0; i < reads; i++)
                {
                    FileStream fs = streams[rnd.Next(streams.Length)];
                    long off = (long)rnd.Next((int)(fileBytes / 4096)) * 4096;
                    fs.Seek(off, SeekOrigin.Begin);
                    fs.Read(buf, 0, buf.Length);
                }
            }
            finally
            {
                sw.Stop();
                foreach (FileStream fs in streams) fs.Dispose();
            }
            return sw.Elapsed.TotalMilliseconds;
        }

        private static double SequentialWarmMs(string[] files)
        {
            var buf = new byte[1024 * 1024];
            var sw = Stopwatch.StartNew();
            foreach (string f in files)
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, buf.Length,
                    FileOptions.SequentialScan))
                    while (fs.Read(buf, 0, buf.Length) > 0) { }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static void RunPrewarmLab(string output, string readsArg)
        {
            int reads;
            if (!int.TryParse(readsArg ?? "", out reads) || reads < 200) reads = 3000;
            var sb = new StringBuilder();
            sb.AppendLine("=== 缓存预热台架 ===");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  每组 " + PrewarmFilesPerGroup + "×" + PrewarmFileMb + "MB  随机读 " + reads + "×4KB");
            string dir = Path.Combine(Path.GetTempPath(), "PavisePrewarm_" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            try
            {
                var rnd = new Random(23);
                var g1 = new string[PrewarmFilesPerGroup];
                var g2 = new string[PrewarmFilesPerGroup];
                for (int i = 0; i < PrewarmFilesPerGroup; i++)
                {
                    g1[i] = Path.Combine(dir, "a" + i + ".bin");
                    g2[i] = Path.Combine(dir, "b" + i + ".bin");
                    WriteUncached(g1[i], PrewarmFileMb, rnd);
                    WriteUncached(g2[i], PrewarmFileMb, rnd);
                }
                sb.AppendLine("两组文件已用无缓冲写入生成（不在系统缓存中）");

                double noWarm = RandomReadMs(g1, reads, new Random(31));
                double warmCost = SequentialWarmMs(g2);
                double warmed = RandomReadMs(g2, reads, new Random(31));

                sb.AppendLine();
                sb.AppendLine("未预热随机读：" + noWarm.ToString("0") + " ms（"
                    + (reads * 4.0 / 1024 / (noWarm / 1000)).ToString("0.0") + " MB/s）");
                sb.AppendLine("预热成本（顺序读 " + PrewarmFilesPerGroup * PrewarmFileMb + "MB）："
                    + warmCost.ToString("0") + " ms");
                sb.AppendLine("预热后随机读：" + warmed.ToString("0") + " ms（"
                    + (reads * 4.0 / 1024 / (warmed / 1000)).ToString("0.0") + " MB/s）");
                sb.AppendLine();
                double gain = warmed > 0 ? noWarm / warmed : 0;
                sb.AppendLine("随机读提速 " + gain.ToString("F1") + " 倍");
                bool worth = gain >= 3;
                sb.AppendLine("结论：" + (worth
                    ? "预热收益显著，值得实现（对局开始时低优先级预读游戏热点文件）"
                    : "本机（快盘）预热收益不足 3 倍门槛，机械盘机型另测"));
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR=" + ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            }
        }
    }
}
