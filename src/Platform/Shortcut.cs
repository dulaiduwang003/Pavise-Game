// @author bdth 2074055628@qq.com
// 文件用途 解析快捷方式指向的真实可执行文件

using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class Shortcut
    {
        public static bool IsLnk(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveTarget(string lnkPath)
        {
            string target;
            string arguments;
            return TryResolve(lnkPath, out target, out arguments) ? target : null;
        }

        public static bool TryResolve(string lnkPath, out string target, out string arguments)
        {
            target = null;
            arguments = "";
            if (!IsLnk(lnkPath)) return false;
            object shell = null, sc = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                shell = Activator.CreateInstance(shellType);
                sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnkPath });
                if (sc == null) return false;
                target = sc.GetType().InvokeMember(
                    "TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                arguments = sc.GetType().InvokeMember(
                    "Arguments", BindingFlags.GetProperty, null, sc, null) as string ?? "";
                return !string.IsNullOrEmpty(target);
            }
            catch { target = null; arguments = ""; return false; }
            finally
            {
                if (sc != null) try { Marshal.ReleaseComObject(sc); } catch { }
                if (shell != null) try { Marshal.ReleaseComObject(shell); } catch { }
            }
        }
    }
}
