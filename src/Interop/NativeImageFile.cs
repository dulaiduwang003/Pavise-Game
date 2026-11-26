#if !PAVISE_EMBEDDED_NATIVE
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace PaviseApp
{
    // 测试构建里原生 ETW 组件的落盘与核对 正式构建把原生代码链进 exe 不编译这个类
    // Windows 只能从文件加载 DLL 或启动 EXE 所以首次使用时写到数据目录的 native\<哈希前 16 位>\<文件名>
    // 交给调用方的句柄只读且只允许他人读取 调用方加载或启动完成前文件不能被改写
    internal static class NativeImageFile
    {
        // 测试改用独立目录 不写真实数据目录
        internal static string RootForTest = null;

        internal static bool IsLocalCandidate(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string baseDir = RootForTest ?? AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir)) return false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullBase = Path.GetFullPath(baseDir);
                if (!fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    !fullBase.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    fullBase += Path.DirectorySeparatorChar;
                }
                return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static FileStream TryOpenSideBySide(string fileName, string sha256, out string path)
        {
            path = null;
            string baseDir = RootForTest ?? AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir)) return null;

            string[] candidates = new string[]
            {
                Path.Combine(baseDir, fileName),
                Path.Combine(baseDir, "native", fileName)
            };

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate) || Reparse(candidate)) continue;
                FileStream stream = null;
                try
                {
                    stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using (SHA256 sha = SHA256.Create())
                    {
                        byte[] digest = sha.ComputeHash(stream);
                        var text = new System.Text.StringBuilder(digest.Length * 2);
                        foreach (byte value in digest) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                        if (string.Equals(text.ToString(), sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            stream.Position = 0;
                            path = candidate;
                            FileStream held = stream;
                            stream = null;
                            return held;
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally
                {
                    if (stream != null) stream.Dispose();
                }
            }
            return null;
        }

        // 成功时返回已逐字节核对的只读句柄 调用方用完前一直持有 失败返回 null 并写明原因
        internal static FileStream Prepare(string base64, int length, string sha256, string fileName, out string path, out string reason)
        {
            FileStream sideBySide = TryOpenSideBySide(fileName, sha256, out path);
            if (sideBySide != null)
            {
                reason = "";
                return sideBySide;
            }

            path = null;
            byte[] image = Convert.FromBase64String(base64);
            string hash = Hash(image);
            if (image.Length != length || hash != sha256) { reason = NativeEtw.LoadReason("exe 内的原生组件镜像损坏", "native component image inside the exe is corrupt"); return null; }
            // 程序启动时总会初始化数据目录 只有不经启动流程的测试才会落到临时目录
            string root = RootForTest ?? Paths.Data ?? Path.Combine(Path.GetTempPath(), "PaviseAuto");
            string nativeRoot = Path.Combine(root, "native");
            string directory = Path.Combine(nativeRoot, hash.Substring(0, 16));
            Directory.CreateDirectory(directory);
            if (Reparse(root) || Reparse(nativeRoot) || Reparse(directory)) { reason = NativeEtw.LoadReason("原生组件目录含重解析点", "native component directory contains a reparse point"); return null; }
            string target = Path.Combine(directory, fileName);
            FileStream held = Hold(target, image);
            if (held == null) { reason = NativeEtw.LoadReason("原生组件写出或核对失败", "native component write or verification failed"); return null; }
            Prune(nativeRoot, directory, fileName);
            path = target;
            reason = "";
            return held;
        }

        private static string Hash(byte[] image)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(image);
                var text = new System.Text.StringBuilder(digest.Length * 2);
                foreach (byte value in digest) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static bool Reparse(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        // 内容和 exe 内的镜像逐字节一致才交出 不一致就重写一次
        // 加载器和进程创建都要以共享读方式打开文件 所以交出去的句柄不能带写权限
        private static FileStream Hold(string path, byte[] image)
        {
            FileStream held = Verified(path, image);
            if (held != null) return held;
            try
            {
                using (var written = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    written.Write(image, 0, image.Length);
                    written.Flush(true);
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            // 重新打开后再核对一次 写完到打开之间若被替换也能发现
            return Verified(path, image);
        }

        private static FileStream Verified(string path, byte[] image)
        {
            if (!File.Exists(path)) return null;
            FileStream stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!Same(stream, image) || Reparse(path)) return null;
                FileStream held = stream; stream = null; return held;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { if (stream != null) stream.Dispose(); }
        }

        private static bool Same(FileStream stream, byte[] image)
        {
            if (stream.Length != image.Length) return false;
            stream.Position = 0;
            var buffer = new byte[65536];
            int offset = 0;
            while (offset < image.Length)
            {
                int count = stream.Read(buffer, 0, Math.Min(buffer.Length, image.Length - offset));
                if (count <= 0) return false;
                for (int i = 0; i < count; i++) if (buffer[i] != image[offset + i]) return false;
                offset += count;
            }
            return true;
        }

        // 只清理同一组件留下的旧版本文件 正被占用的跳过 目录空了才删
        private static void Prune(string nativeRoot, string current, string fileName)
        {
            try
            {
                foreach (string directory in Directory.GetDirectories(nativeRoot))
                {
                    string name = Path.GetFileName(directory);
                    if (string.Equals(directory, current, StringComparison.OrdinalIgnoreCase) || !OldVersionName(name) || Reparse(directory)) continue;
                    try
                    {
                        string file = Path.Combine(directory, fileName);
                        if (File.Exists(file)) File.Delete(file);
                        if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory, false);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool OldVersionName(string name)
        {
            if (name == null || name.Length != 16) return false;
            foreach (char c in name) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
    }
}
#endif
