// @author bdth 2074055628@qq.com
// 文件用途 从设置页安全定位并启动随程序发布的一键卸载脚本
using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static class UninstallLauncher
    {
        internal const string ScriptFileName = "Pavise-Uninstall.cmd";
        internal const string ConfirmedArgument = "--from-app";

        internal static string FindScript(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath)) return null;
            try
            {
                string exe = Path.GetFullPath(executablePath);
                string directory = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(directory)) return null;

                string adjacent = Path.Combine(directory, ScriptFileName);
                if (File.Exists(adjacent)) return Path.GetFullPath(adjacent);

                // 开发构建位于仓库 build 目录，脚本仍在项目根目录。
                // 只认这个明确布局，不向任意父目录搜索同名脚本。
                var build = new DirectoryInfo(directory);
                if (build.Name.Equals("build", StringComparison.OrdinalIgnoreCase)
                    && build.Parent != null)
                {
                    string repository = Path.Combine(build.Parent.FullName, ScriptFileName);
                    if (File.Exists(repository)) return Path.GetFullPath(repository);
                }
            }
            catch { }
            return null;
        }

        internal static ProcessStartInfo CreateStartInfo(string scriptPath)
        {
            string script = Path.GetFullPath(scriptPath);
            return new ProcessStartInfo
            {
                FileName = script,
                Arguments = ConfirmedArgument,
                WorkingDirectory = Path.GetDirectoryName(script),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
        }

        internal static bool TryStart(string executablePath, out string failure)
        {
            failure = null;
            string script = FindScript(executablePath);
            if (script == null)
            {
                failure = Lang.T("uninstall.scriptmissing");
                return false;
            }
            try
            {
                using (Process process = Process.Start(CreateStartInfo(script)))
                {
                    if (process == null)
                    {
                        failure = Lang.T("uninstall.startfailed");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = Lang.F("uninstall.startfailed.detail", ex.GetType().Name);
                return false;
            }
        }
    }
}
