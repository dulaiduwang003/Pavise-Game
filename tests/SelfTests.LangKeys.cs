// @author bdth 2074055628@qq.com
// 文件用途 扫描全部源码 确保引用到的文案键都有定义

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static readonly Regex LangCall =
            new Regex("Lang\\.[TF]\\(\\s*\"([^\"]+)\"\\s*[,)]", RegexOptions.CultureInvariant);

        private static void TestEveryLangKeyIsDefined()
        {
            string src = LocateSourceRoot();
            if (src == null) throw new TestSkippedException("找不到源码目录，发布构建下跳过");

            var missing = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(file, Encoding.UTF8); }
                catch { continue; }
                foreach (Match m in LangCall.Matches(text))
                {
                    string key = m.Groups[1].Value;
                    if (!seen.Add(key)) continue;
                    if (Lang.HasKey(key)) continue;
                    missing.Add(key + "  ←  " + Path.GetFileName(file));
                }
            }
            if (missing.Count > 0)
                throw new Exception("这些文案键被引用但没有定义：" + Environment.NewLine
                    + "   " + string.Join(Environment.NewLine + "   ", missing.ToArray()));
        }

        private static string LocateSourceRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(dir); depth++)
            {
                string candidate = Path.Combine(dir, "src");
                if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Ui")))
                    return candidate;
                DirectoryInfo parent = Directory.GetParent(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }
}
