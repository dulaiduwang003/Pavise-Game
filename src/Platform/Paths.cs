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

        public static void Init()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);

            if (IsPortable(exeDir)) { Data = exeDir; return; }

            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pavise");
                Directory.CreateDirectory(appData);
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

    }

}
