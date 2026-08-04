// @author bdth 2074055628@qq.com
// 文件用途 提供程序数据 日志和配置文件路径

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class Paths
    {
        public static string Data;

        private static readonly string[] DataFiles =
        {
            "Pavise.games.txt", GameProfileStore.FileName, "Pavise.whitelist.txt", "Pavise.targets.txt",
            SessionReportStore.FileName, "Pavise.log", "crash.log",
            LegacyFreezeRecovery.StateFileName, SuppressionCore.StateFileName
        };

        public static void Init()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);

            if (IsPortable(exeDir)) { Data = exeDir; return; }

            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pavise");
                Directory.CreateDirectory(appData);
                Migrate(exeDir, appData);
                Data = appData;
            }
            catch
            {
                Data = exeDir;
            }
        }

        private static bool IsPortable(string exeDir)
        {
            try
            {
                if (!File.Exists(Path.Combine(exeDir, "Pavise.portable"))) return false;
                string probe = Path.Combine(exeDir, ".w" + Process.GetCurrentProcess().Id);
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static void Migrate(string exeDir, string appData)
        {
            if (string.Equals(exeDir.TrimEnd('\\'), appData.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;
            foreach (string name in DataFiles)
            {
                try
                {
                    string src = Path.Combine(exeDir, name);
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(appData, name);
                    if (!File.Exists(dst)) { File.Move(src, dst); continue; }
                    if (File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
                        File.Copy(src, dst, true);
                    File.Delete(src);
                }
                catch { }
            }
        }
    }

}
