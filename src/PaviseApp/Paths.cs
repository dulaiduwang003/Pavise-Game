using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class Paths
	{
		public static string Data;

		public static void Init()
		{
			Init(null);
		}

		public static void Init(string startupData)
		{
			if (startupData != null)
			{
				if (!IsAbsoluteStartupDataPath(startupData))
				{
					throw new ArgumentException("Startup data directory must be absolute.");
				}
				string fullPath = Path.GetFullPath(startupData);
				if (string.Equals(fullPath.TrimEnd('\\', '/'), Path.GetPathRoot(fullPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
				{
					throw new ArgumentException("Startup data directory cannot be a drive root.");
				}
				Directory.CreateDirectory(fullPath);
				Data = fullPath;
				return;
			}
			string directoryName = Path.GetDirectoryName(Application.ExecutablePath);
			if (IsPortable(directoryName))
			{
				Data = Path.Combine(directoryName, "PaviseAuto.Data");
				Directory.CreateDirectory(Data);
				return;
			}
			try
			{
				string paviseAutoPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaviseAuto");
				Directory.CreateDirectory(paviseAutoPath);
				Data = paviseAutoPath;
			}
			catch
			{
				Data = Path.Combine(directoryName, "PaviseAuto.Data");
				Directory.CreateDirectory(Data);
			}
		}

		internal static string ResolveStartupDataArgument(string[] args)
		{
			string startupDataArgumentResult = null;
			if (args == null)
			{
				return null;
			}
			for (int i = 0; i < args.Length; i++)
			{
				if (string.Equals(args[i], "--autostart-data", StringComparison.OrdinalIgnoreCase))
				{
					if (startupDataArgumentResult != null || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
					{
						throw new ArgumentException("Startup data directory argument is invalid.");
					}
					startupDataArgumentResult = args[++i];
				}
			}
			return startupDataArgumentResult;
		}

		internal static bool IsAbsoluteStartupDataPath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return false;
			}
			if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
			{
				if (path.StartsWith("\\\\", StringComparison.Ordinal))
				{
					return Path.GetPathRoot(path).Length > 2;
				}
				return false;
			}
			return true;
		}

		private static bool IsPortable(string exeDir)
		{
			try
			{
				if (!File.Exists(Path.Combine(exeDir, "Pavise.portable")))
				{
					return false;
				}
				string path = Path.Combine(exeDir, ".w" + Process.GetCurrentProcess().Id);
				File.WriteAllText(path, "");
				File.Delete(path);
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
