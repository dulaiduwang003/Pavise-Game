using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
	internal static class StartupPayload
	{
		internal const string ExecutableName = "PaviseAuto.exe";

		private const string StagePrefix = ".PaviseAuto.";

		private const string StageSuffix = ".tmp";

		internal static string ManagedRoot
		{
			get
			{
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
				if (string.IsNullOrEmpty(folderPath))
				{
					throw new IOException("Local application data is unavailable");
				}
				return Path.Combine(folderPath, "PaviseAuto", "Autostart");
			}
		}

		internal static string Ensure(string sourceExecutable)
		{
			return Ensure(sourceExecutable, ManagedRoot);
		}

		internal static string Ensure(string sourceExecutable, string managedRoot)
		{
			string text = NormalizeRoot(managedRoot);
			string fullPath = Path.GetFullPath(sourceExecutable);
			using (FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				string text2 = Hash(fileStream);
				string text3 = Path.Combine(text, text2);
				string text4 = Path.Combine(text3, "PaviseAuto.exe");
				RejectReparseChain(text);
				RejectReparse(text3);
				RejectReparse(text4);
				if (File.Exists(text4))
				{
					Verify(text4, text2);
					return text4;
				}
				Directory.CreateDirectory(text3);
				RejectReparseChain(text);
				RejectReparse(text3);
				string text5 = Path.Combine(text3, ".PaviseAuto." + Guid.NewGuid().ToString("N") + ".tmp");
				try
				{
					fileStream.Position = 0L;
					using (FileStream fileStream2 = new FileStream(text5, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					{
						fileStream.CopyTo(fileStream2);
						fileStream2.Flush(true);
					}
					Verify(text5, text2);
					try
					{
						File.Move(text5, text4);
					}
					catch (IOException)
					{
						if (!File.Exists(text4))
						{
							throw;
						}
						Verify(text4, text2);
					}
					Verify(text4, text2);
					return text4;
				}
				finally
				{
					try
					{
						if (File.Exists(text5))
						{
							File.Delete(text5);
						}
					}
					catch
					{
					}
				}
			}
		}

		internal static string GetExpectedExecutable(string sourceExecutable)
		{
			return GetExpectedExecutable(sourceExecutable, ManagedRoot);
		}

		internal static string GetExpectedExecutable(string sourceExecutable, string managedRoot)
		{
			string path = NormalizeRoot(managedRoot);
			using (FileStream stream = new FileStream(Path.GetFullPath(sourceExecutable), FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				return Path.Combine(path, Hash(stream), "PaviseAuto.exe");
			}
		}

		internal static bool IsManagedExecutable(string executable)
		{
			return IsManagedExecutable(executable, ManagedRoot);
		}

		internal static bool IsManagedExecutable(string executable, string managedRoot)
		{
			try
			{
				string second = NormalizeRoot(managedRoot);
				string fullPath = Path.GetFullPath(executable);
				string directoryName = Path.GetDirectoryName(fullPath);
				return string.Equals(Path.GetFileName(fullPath), "PaviseAuto.exe", StringComparison.OrdinalIgnoreCase) && IsHash(Path.GetFileName(directoryName)) && SamePath(Path.GetDirectoryName(directoryName), second);
			}
			catch
			{
				return false;
			}
		}

		internal static bool IsVerifiedExecutable(string executable, string managedRoot)
		{
			if (!IsManagedExecutable(executable, managedRoot))
			{
				return false;
			}
			try
			{
				RejectReparseChain(NormalizeRoot(managedRoot));
				RejectReparse(Path.GetDirectoryName(executable));
				RejectReparse(executable);
				Verify(executable, Path.GetFileName(Path.GetDirectoryName(executable)));
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static int Cleanup(string managedRoot, string currentExecutable)
		{
			return Cleanup(managedRoot, currentExecutable, null);
		}

		internal static int Cleanup(string managedRoot, string keep, string alsoKeep)
		{
			string text;
			try
			{
				text = NormalizeRoot(managedRoot);
				RejectReparseChain(text);
			}
			catch
			{
				return 0;
			}
			if (!Directory.Exists(text))
			{
				return 0;
			}
			int num = 0;
			string[] directories;
			try
			{
				directories = Directory.GetDirectories(text, "*", SearchOption.TopDirectoryOnly);
			}
			catch
			{
				return 0;
			}
			string[] array = directories;
			foreach (string text2 in array)
			{
				try
				{
					if (!IsHash(Path.GetFileName(text2)) || !SamePath(Path.GetDirectoryName(text2), text))
					{
						continue;
					}
					RejectReparse(text2);
					string[] files = Directory.GetFiles(text2, "*", SearchOption.TopDirectoryOnly);
					foreach (string text3 in files)
					{
						string fileName = Path.GetFileName(text3);
						if ((string.Equals(fileName, "PaviseAuto.exe", StringComparison.OrdinalIgnoreCase) || IsStageName(fileName)) && !SamePath(text3, keep) && !SamePath(text3, alsoKeep))
						{
							try
							{
								RejectReparse(text3);
								File.Delete(text3);
								num++;
							}
							catch
							{
							}
						}
					}
					TryRemoveEmptyDirectory(text2);
				}
				catch
				{
				}
			}
			TryRemoveEmptyDirectory(text);
			return num;
		}

		private static void TryRemoveEmptyDirectory(string directory)
		{
			try
			{
				RejectReparse(directory);
				if (Directory.GetFileSystemEntries(directory).Length == 0)
				{
					Directory.Delete(directory, false);
				}
			}
			catch
			{
			}
		}

		private static bool IsStageName(string name)
		{
			if (name.Length != ".PaviseAuto.".Length + 32 + ".tmp".Length || !name.StartsWith(".PaviseAuto.", StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal))
			{
				return false;
			}
			string text = name.Substring(".PaviseAuto.".Length, name.Length - ".PaviseAuto.".Length - ".tmp".Length);
			if (text.Length != 32)
			{
				return false;
			}
			string text2 = text;
			foreach (char c in text2)
			{
				if (!IsHex(c))
				{
					return false;
				}
			}
			return true;
		}

		private static string NormalizeRoot(string root)
		{
			if (string.IsNullOrWhiteSpace(root))
			{
				throw new ArgumentException("Startup payload root is required");
			}
			string text = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(text, Path.GetPathRoot(text).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("A drive root cannot hold startup payloads");
			}
			return text;
		}

		private static void RejectReparse(string path)
		{
			if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("Startup payload path is redirected");
			}
		}

		private static void RejectReparseChain(string path)
		{
			string text = Path.GetFullPath(path);
			while (!string.IsNullOrEmpty(text))
			{
				RejectReparse(text);
				text = Path.GetDirectoryName(text);
			}
		}

		private static bool SamePath(string first, string second)
		{
			if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
			{
				return false;
			}
			try
			{
				return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static bool IsHash(string name)
		{
			if (string.IsNullOrEmpty(name) || name.Length != 64)
			{
				return false;
			}
			foreach (char c in name)
			{
				if (!IsHex(c))
				{
					return false;
				}
			}
			return true;
		}

		private static bool IsHex(char c)
		{
			if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
			{
				if (c >= 'A')
				{
					return c <= 'F';
				}
				return false;
			}
			return true;
		}

		private static string Hash(Stream stream)
		{
			using (SHA256 sHA = SHA256.Create())
			{
				byte[] array = sHA.ComputeHash(stream);
				StringBuilder stringBuilder = new StringBuilder(array.Length * 2);
				byte[] array2 = array;
				foreach (byte b in array2)
				{
					stringBuilder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
				}
				return stringBuilder.ToString();
			}
		}

		private static void Verify(string path, string expected)
		{
			using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				if (!string.Equals(Hash(stream), expected, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("Startup payload hash mismatch");
				}
			}
		}
	}
}
