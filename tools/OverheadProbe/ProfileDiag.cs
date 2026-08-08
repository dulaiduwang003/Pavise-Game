// @author bdth 2074055628@qq.com
// 文件用途 复刻 Pavise 的档案解析与成员判定 定位测试游戏未被选举的断点

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseProfileDiag
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint pid, out uint session);

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static int Main(string[] args)
        {
            string target = args.Length > 0 ? args[0] : "FrameBench";
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Pavise", "Pavise.profiles.dat");

            Console.WriteLine("profile file: " + path);
            if (!File.Exists(path)) { Console.WriteLine("MISSING"); return 1; }

            byte[] raw = File.ReadAllBytes(path);
            Console.WriteLine("first bytes: " + raw[0] + "," + raw[1] + "," + raw[2] + ","
                + raw[3] + (raw[0] == 0xEF ? "   <-- HAS UTF8 BOM (Pavise will reject!)" : "   (no BOM)"));

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            Console.WriteLine("lines: " + lines.Length);
            Console.WriteLine("header raw: '" + lines[0] + "'  matchesV4="
                + (lines[0] == "PAVISE_PROFILES_V4"));
            if (lines[0] != "PAVISE_PROFILES_V4")
            {
                Console.WriteLine(">>> Pavise would load ZERO profiles from this file.");
                return 1;
            }

            var profiles = new List<string[]>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] a = lines[i].Split('|');
                if (a.Length == 0 || a[0] != "P") continue;
                if (a.Length != 6 && a.Length != 7) { Console.WriteLine("  line " + i + " field count " + a.Length + " -> skipped"); continue; }
                profiles.Add(a);
            }
            Console.WriteLine("parsed profiles: " + profiles.Count);
            Console.WriteLine();

            uint ownSession;
            ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out ownSession);
            Console.WriteLine("this diag session = " + ownSession);
            Console.WriteLine();

            var live = new List<KeyValuePair<int, string>>();
            foreach (Process p in Process.GetProcessesByName(target))
            {
                string imagePath = ImagePathOf(p.Id);
                uint session;
                ProcessIdToSessionId((uint)p.Id, out session);
                live.Add(new KeyValuePair<int, string>(p.Id, imagePath));
                Console.WriteLine("live '" + target + "' pid=" + p.Id + " session=" + session);
                Console.WriteLine("   image path = " + (imagePath ?? "<NULL - cannot read>"));
                p.Dispose();
            }
            if (live.Count == 0) Console.WriteLine("live '" + target + "': none running");
            Console.WriteLine();

            foreach (string[] a in profiles)
            {
                string name = Un64(a[2]);
                string root = NormalizeRoot(Un64(a[3]));
                string exe = NormalizePath(Un64(a[4]));
                Console.WriteLine("profile '" + name + "'");
                Console.WriteLine("   root = " + (root ?? "<null>"));
                Console.WriteLine("   exe  = " + (exe ?? "<null>"));
                Console.WriteLine("   entries = " + Un64(a[5]).Replace("\n", " , "));
                foreach (KeyValuePair<int, string> pair in live)
                {
                    if (pair.Value == null) continue;
                    string ip = NormalizePath(pair.Value);
                    bool sameExe = Same(exe, ip);
                    bool underRoot = ContainsPath(root, ip);
                    Console.WriteLine("   vs pid " + pair.Key + ": sameExe=" + sameExe
                        + " underRoot=" + underRoot
                        + " -> isDirectMember=" + (sameExe || underRoot));
                }
                Console.WriteLine();
            }
            return 0;
        }

        private static bool Same(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsPath(string root, string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string prefix = root.TrimEnd('\\') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRoot(string v)
        {
            if (string.IsNullOrEmpty(v) || v.Trim().Length == 0) return null;
            try { return Path.GetFullPath(v.Trim().Trim('"')).TrimEnd('\\'); }
            catch { return null; }
        }

        private static string NormalizePath(string v)
        {
            if (string.IsNullOrEmpty(v) || v.Trim().Length == 0) return null;
            try { return Path.GetFullPath(v.Trim().Trim('"')); }
            catch { return null; }
        }

        private static string Un64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return ""; }
        }

        private static string ImagePathOf(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                int cap = 1024;
                var sb = new StringBuilder(cap);
                return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }
}
