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
			string root = NormalizeRoot(managedRoot);
			string fullPath = Path.GetFullPath(sourceExecutable);
			using (FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				string hashResult = Hash(fileStream);
				string combinedPath = Path.Combine(root, hashResult);
				string paviseAutoPath = Path.Combine(combinedPath, "PaviseAuto.exe");
				RejectReparseChain(root);
				RejectReparse(combinedPath);
				RejectReparse(paviseAutoPath);
				if (File.Exists(paviseAutoPath))
				{
					Verify(paviseAutoPath, hashResult);
					return paviseAutoPath;
				}
				Directory.CreateDirectory(combinedPath);
				RejectReparseChain(root);
				RejectReparse(combinedPath);
				string combinedPath2 = Path.Combine(combinedPath, ".PaviseAuto." + Guid.NewGuid().ToString("N") + ".tmp");
				try
				{
					fileStream.Position = 0L;
					using (FileStream fileStream2 = new FileStream(combinedPath2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					{
						fileStream.CopyTo(fileStream2);
						fileStream2.Flush(true);
					}
					Verify(combinedPath2, hashResult);
					try
					{
						File.Move(combinedPath2, paviseAutoPath);
					}
					catch (IOException)
					{
						if (!File.Exists(paviseAutoPath))
						{
							throw;
						}
						Verify(paviseAutoPath, hashResult);
					}
					Verify(paviseAutoPath, hashResult);
					return paviseAutoPath;
				}
				finally
				{
					try
					{
						if (File.Exists(combinedPath2))
						{
							File.Delete(combinedPath2);
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
			string root;
			try
			{
				root = NormalizeRoot(managedRoot);
				RejectReparseChain(root);
			}
			catch
			{
				return 0;
			}
			if (!Directory.Exists(root))
			{
				return 0;
			}
			int processedCount = 0;
			string[] directories;
			try
			{
				directories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
			}
			catch
			{
				return 0;
			}
			string[] directoriesSnapshot = directories;
			foreach (string entryText in directoriesSnapshot)
			{
				try
				{
					if (!IsHash(Path.GetFileName(entryText)) || !SamePath(Path.GetDirectoryName(entryText), root))
					{
						continue;
					}
					RejectReparse(entryText);
					string[] files = Directory.GetFiles(entryText, "*", SearchOption.TopDirectoryOnly);
					foreach (string filePath in files)
					{
						string fileName = Path.GetFileName(filePath);
						if ((string.Equals(fileName, "PaviseAuto.exe", StringComparison.OrdinalIgnoreCase) || IsStageName(fileName)) && !SamePath(filePath, keep) && !SamePath(filePath, alsoKeep))
						{
							try
							{
								RejectReparse(filePath);
								File.Delete(filePath);
								processedCount++;
							}
							catch
							{
							}
						}
					}
					TryRemoveEmptyDirectory(entryText);
				}
				catch
				{
				}
			}
			TryRemoveEmptyDirectory(root);
			return processedCount;
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
			string substringResult = name.Substring(".PaviseAuto.".Length, name.Length - ".PaviseAuto.".Length - ".tmp".Length);
			if (substringResult.Length != 32)
			{
				return false;
			}
			string message = substringResult;
			foreach (char c in message)
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
			string getFullPathTrimmed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(getFullPathTrimmed, Path.GetPathRoot(getFullPathTrimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("A drive root cannot hold startup payloads");
			}
			return getFullPathTrimmed;
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
			string fullPath = Path.GetFullPath(path);
			while (!string.IsNullOrEmpty(fullPath))
			{
				RejectReparse(fullPath);
				fullPath = Path.GetDirectoryName(fullPath);
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
				byte[] hash = sHA.ComputeHash(stream);
				StringBuilder stringBuilder = new StringBuilder(hash.Length * 2);
				byte[] rawBytes = hash;
				foreach (byte b in rawBytes)
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
