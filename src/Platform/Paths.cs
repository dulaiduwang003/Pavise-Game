// 作者：bdth 2074055628@qq.com
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
        { Init(null); }

        public static void Init(string startupData)
        {
            if (startupData != null)
            {
                if (!IsAbsoluteStartupDataPath(startupData))
                    throw new ArgumentException("Startup data directory must be absolute.");
                string directory = Path.GetFullPath(startupData);
                if (string.Equals(directory.TrimEnd('\\', '/'), Path.GetPathRoot(directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Startup data directory cannot be a drive root.");
                Directory.CreateDirectory(directory);
                Data = directory;
                return;
            }
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);

            if (IsPortable(exeDir))
            {
                Data = Path.Combine(exeDir, "PaviseAuto.Data"); Directory.CreateDirectory(Data); return;
            }

            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaviseAuto");
                Directory.CreateDirectory(appData);
                Data = appData;
            }
            catch
            {
                Data = Path.Combine(exeDir, "PaviseAuto.Data");
                Directory.CreateDirectory(Data);
            }
        }

        internal static string ResolveStartupDataArgument(string[] args)
        {
            string directory = null;
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], TaskHelper.AutostartDataArgument, StringComparison.OrdinalIgnoreCase)) continue;
                if (directory != null || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException("Startup data directory argument is invalid.");
                directory = args[++i];
            }
            return directory;
        }

        internal static bool IsAbsoluteStartupDataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/')
                || path.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetPathRoot(path).Length > 2;
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
