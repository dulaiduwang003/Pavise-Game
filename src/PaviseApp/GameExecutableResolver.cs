using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PaviseApp
{
	internal static class GameExecutableResolver
	{
		[ComImport]
		[Guid("00021401-0000-0000-C000-000000000046")]
		private class ShellLink
		{
		}

		[ComImport]
		[Guid("000214F9-0000-0000-C000-000000000046")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellLinkW
		{
			void GetPath([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, out Win32FindData data, uint flags);

			void GetIDList(out IntPtr idList);

			void SetIDList(IntPtr idList);

			void GetDescription([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

			void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

			void GetWorkingDirectory([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);

			void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

			void GetArguments([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);

			void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

			void GetHotkey(out short hotkey);

			void SetHotkey(short hotkey);

			void GetShowCmd(out int showCmd);

			void SetShowCmd(int showCmd);

			void GetIconLocation([Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathSize, out int iconIndex);

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

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
			public string FileName;

			[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
			public string AlternateFileName;
		}

		public static bool TryResolve(string selectedPath, out string executablePath, out string error)
		{
			string suggestedName;
			return TryResolve(selectedPath, out executablePath, out error, out suggestedName);
		}

		public static bool TryResolve(string selectedPath, out string executablePath, out string error, out string suggestedName)
		{
			executablePath = null;
			error = null;
			suggestedName = null;
			if (string.IsNullOrWhiteSpace(selectedPath))
			{
				error = Lang.T("t.gameexecutableresolver.1");
				return false;
			}
			string fullPath;
			try
			{
				fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(selectedPath.Trim().Trim('"')));
			}
			catch
			{
				error = Lang.T("t.gameexecutableresolver.2");
				return false;
			}
			if (Directory.Exists(fullPath))
			{
				string pickMainExecutableResult = ExecutableCandidateProbe.PickMainExecutable(fullPath);
				if (pickMainExecutableResult == null)
				{
					error = Lang.T("t.gameexecutableresolver.8");
					return false;
				}
				suggestedName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
				fullPath = pickMainExecutableResult;
			}
			if (!File.Exists(fullPath))
			{
				error = Lang.T("t.gameexecutableresolver.3");
				return false;
			}
			string extension = Path.GetExtension(fullPath);
			string executablePath2 = fullPath;
			if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
			{
				if (!TryResolveShortcut(fullPath, out executablePath2))
				{
					error = Lang.T("t.gameexecutableresolver.4");
					return false;
				}
			}
			else if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
			{
				if (!SteamShortcut.TryResolve(fullPath, out executablePath2, out error, out suggestedName))
				{
					return false;
				}
			}
			else if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
			{
				error = Lang.T("t.gameexecutableresolver.5");
				return false;
			}
			try
			{
				executablePath2 = Path.GetFullPath(Environment.ExpandEnvironmentVariables(executablePath2.Trim().Trim('"')));
			}
			catch
			{
				error = Lang.T("t.gameexecutableresolver.6");
				return false;
			}
			if (!Path.GetExtension(executablePath2).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executablePath2))
			{
				error = Lang.T("t.gameexecutableresolver.7");
				return false;
			}
			if (!IsPortableExecutable(executablePath2) && !IsUnreadable(executablePath2))
			{
				error = Lang.T("t.gameexecutableresolver.7");
				return false;
			}
			executablePath = executablePath2;
			return true;
		}

		internal static bool IsPortableExecutable(string path)
		{
			try
			{
				using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					using (BinaryReader binaryReader = new BinaryReader(fileStream))
					{
						if (fileStream.Length < 64 || binaryReader.ReadUInt16() != 23117)
						{
							return false;
						}
						fileStream.Position = 60L;
						int positionValue = binaryReader.ReadInt32();
						if (positionValue < 64 || positionValue > fileStream.Length - 4)
						{
							return false;
						}
						fileStream.Position = positionValue;
						return binaryReader.ReadUInt32() == 17744;
					}
				}
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsUnreadable(string path)
		{
			try
			{
				using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
				}
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return true;
			}
			catch (IOException)
			{
				return false;
			}
			catch
			{
				return false;
			}
		}

		public static string ResolveShortcut(string shortcutPath)
		{
			string target;
			if (!TryResolveShortcut(shortcutPath, out target))
			{
				return null;
			}
			return target;
		}

		private static bool TryResolveShortcut(string shortcutPath, out string target)
		{
			target = null;
			IShellLinkW shellLinkW = null;
			try
			{
				shellLinkW = (IShellLinkW)new ShellLink();
				((IPersistFile)shellLinkW).Load(shortcutPath, 0);
				StringBuilder stringBuilder = new StringBuilder(32768);
				Win32FindData data;
				shellLinkW.GetPath(stringBuilder, stringBuilder.Capacity, out data, 4u);
				target = stringBuilder.ToString();
				return !string.IsNullOrWhiteSpace(target);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (shellLinkW != null && Marshal.IsComObject(shellLinkW))
				{
					try
					{
						Marshal.FinalReleaseComObject(shellLinkW);
					}
					catch
					{
					}
				}
			}
		}
	}
}
