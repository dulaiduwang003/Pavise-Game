// @author bdth 2074055628@qq.com
// 文件用途 解析游戏程序和快捷方式的真实路径

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AegisApp
{
    internal static class GameExecutableResolver
    {
        public static bool TryResolve(string selectedPath, out string executablePath, out string error)
        {
            string ignored;
            return TryResolve(selectedPath, out executablePath, out error, out ignored);
        }

        public static bool TryResolve(string selectedPath, out string executablePath, out string error,
            out string suggestedName)
        {
            executablePath = null;
            error = null;
            suggestedName = null;
            if (string.IsNullOrWhiteSpace(selectedPath)) { error = "未选择文件"; return false; }

            string source;
            try { source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(selectedPath.Trim().Trim('"'))); }
            catch { error = "文件路径无效"; return false; }
            if (!File.Exists(source)) { error = "文件不存在"; return false; }

            string extension = Path.GetExtension(source);
            string target = source;
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveShortcut(source, out target)) { error = "快捷方式没有指向有效的 EXE"; return false; }
            }
            else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                if (!SteamShortcut.TryResolve(source, out target, out error, out suggestedName)) return false;
            }
            else if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "只支持 EXE、Windows 快捷方式（LNK）和 Steam 桌面快捷方式（URL）";
                return false;
            }

            try { target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'))); }
            catch { error = "快捷方式目标路径无效"; return false; }
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)
                || !IsPortableExecutable(target))
            {
                error = "目标必须是本机存在的有效 EXE 文件";
                return false;
            }
            executablePath = target;
            return true;
        }

        internal static bool IsPortableExecutable(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return false;
                    stream.Position = 0x3C;
                    int peOffset = reader.ReadInt32();
                    if (peOffset < 0x40 || peOffset > stream.Length - 4) return false;
                    stream.Position = peOffset;
                    return reader.ReadUInt32() == 0x00004550;
                }
            }
            catch { return false; }
        }

        private static bool TryResolveShortcut(string shortcutPath, out string target)
        {
            target = null;
            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new ShellLink();
                ((IPersistFile)link).Load(shortcutPath, 0);
                var buffer = new StringBuilder(32768);
                Win32FindData data;
                link.GetPath(buffer, buffer.Capacity, out data, 4 );
                target = buffer.ToString();
                return !string.IsNullOrWhiteSpace(target);
            }
            catch { return false; }
            finally
            {
                if (link != null && Marshal.IsComObject(link))
                    try { Marshal.FinalReleaseComObject(link); } catch { }
            }
        }

#if AEGIS_SELFTEST
        internal static bool CreateShortcutForTest(string shortcutPath, string executablePath)
        {
            IShellLinkW link = null;
            try
            {
                link = (IShellLinkW)new ShellLink();
                link.SetPath(executablePath);
                ((IPersistFile)link).Save(shortcutPath, true);
                return File.Exists(shortcutPath);
            }
            catch { return false; }
            finally
            {
                if (link != null && Marshal.IsComObject(link))
                    try { Marshal.FinalReleaseComObject(link); } catch { }
            }
        }
#endif

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath,
                out Win32FindData data, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathSize, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }
    }
}
