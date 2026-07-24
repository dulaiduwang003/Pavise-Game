// @author bdth 2074055628@qq.com
// 文件用途 解析快捷方式指向的真实可执行文件

using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AegisApp
{
    internal static class Shortcut
    {
        public static bool IsLnk(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveTarget(string lnkPath)
        {
            if (!IsLnk(lnkPath)) return null;
            object shell = null, sc = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnkPath });
                if (sc == null) return null;
                string target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                if (sc != null) try { Marshal.ReleaseComObject(sc); } catch { }
                if (shell != null) try { Marshal.ReleaseComObject(shell); } catch { }
            }
        }
    }
}
