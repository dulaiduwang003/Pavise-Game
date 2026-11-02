using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class UninstallFinalizer
	{
		private sealed class Plan
		{
			internal int Pid;

			internal long Started;

			internal string Executable;

			internal string Hash;

			internal string ManagedRoot;

			internal List<string> Managed = new List<string>();
		}

		private const string Prefix = "Pavise-Uninstall-";

		private const string HelperName = "Pavise.Uninstall.exe";

		private const string ManifestName = "cleanup.dat";

		private const string Signature = "Pavise uninstall 1";

		internal static bool Launch(string executable, out string failure)
		{
			failure = null;
			string text = null;
			try
			{
				string fileName = Process.GetCurrentProcess().MainModule.FileName;
				string fullPath = Path.GetFullPath(executable);
				if (!Same(fullPath, fileName) || !SafeFile(fullPath))
				{
					throw new IOException("Executable changed");
				}
				Plan plan = new Plan();
				plan.Executable = fullPath;
				using (Process process = Process.GetCurrentProcess())
				{
					plan.Pid = process.Id;
					plan.Started = process.StartTime.ToUniversalTime().Ticks;
				}
				plan.ManagedRoot = Path.GetFullPath(StartupPayload.ManagedRoot);
				plan.Managed = SnapshotManaged(plan.ManagedRoot);
				text = Path.Combine(Path.GetTempPath(), "Pavise-Uninstall-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(text);
				RejectRedirects(text);
				string text2 = Path.Combine(text, "Pavise.Uninstall.exe");
				using (FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					plan.Hash = Hash(fileStream);
					fileStream.Position = 0L;
					using (FileStream fileStream2 = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					{
						fileStream.CopyTo(fileStream2);
						fileStream2.Flush(true);
					}
				}
				if (!string.Equals(HashFile(text2), plan.Hash, StringComparison.OrdinalIgnoreCase))
				{
					throw new IOException("Helper copy failed verification");
				}
				string text3 = Path.Combine(text, "cleanup.dat");
				WritePlan(text3, plan);
				ProcessStartInfo processStartInfo = new ProcessStartInfo(text2, "--finish-uninstall " + Quote(text3));
				processStartInfo.UseShellExecute = false;
				processStartInfo.CreateNoWindow = true;
				processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
				processStartInfo.WorkingDirectory = text;
				using (Process process2 = Process.Start(processStartInfo))
				{
					if (process2 == null)
					{
						throw new IOException("Helper did not start");
					}
				}
				return true;
			}
			catch (Exception ex)
			{
				failure = ex.GetType().Name;
				if (text != null)
				{
					RemoveLaunchFiles(text);
				}
				return false;
			}
		}

		internal static bool Run(string manifest)
		{
			string text = null;
			bool flag = false;
			try
			{
				string fileName = Process.GetCurrentProcess().MainModule.FileName;
				text = Path.GetDirectoryName(Path.GetFullPath(fileName));
				if (!IsHelperDirectory(text) || !Same(fileName, Path.Combine(text, "Pavise.Uninstall.exe")) || !Same(manifest, Path.Combine(text, "cleanup.dat")))
				{
					return false;
				}
				RejectRedirects(text);
				RejectRedirects(manifest);
				RejectRedirects(fileName);
				Plan plan = ReadPlan(manifest);
				if (!ValidPlan(plan, fileName) || !WaitForParent(plan))
				{
					return false;
				}
				flag = DeleteVerified(plan.Executable, plan.Hash, 40, 100);
				foreach (string item in plan.Managed)
				{
					string fileName2 = Path.GetFileName(Path.GetDirectoryName(item));
					if (!DeleteVerified(item, fileName2, 2, 100))
					{
						flag = false;
					}
					TryRemoveEmpty(Path.GetDirectoryName(item));
				}
				if (plan.Managed.Count != 0)
				{
					TryRemoveEmpty(plan.ManagedRoot);
				}
				return flag;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (text != null && IsHelperDirectory(text))
				{
					try
					{
						File.WriteAllText(Path.Combine(text, "result.txt"), flag ? "removed" : "skipped");
					}
					catch
					{
					}
					QueueHelperCleanup(text);
				}
			}
		}

		private static bool ValidPlan(Plan plan, string helper)
		{
			if (plan.Pid <= 0 || plan.Started <= 0 || !IsHash(plan.Hash) || Same(plan.Executable, helper) || !SafeFile(plan.Executable) || !string.Equals(plan.Hash, HashFile(helper), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaviseAuto", "Autostart");
			if (!Same(text, plan.ManagedRoot))
			{
				return false;
			}
			foreach (string item in plan.Managed)
			{
				if (!StartupPayload.IsManagedExecutable(item, text))
				{
					return false;
				}
			}
			return true;
		}

		private static bool WaitForParent(Plan plan)
		{
			Process processById;
			try
			{
				processById = Process.GetProcessById(plan.Pid);
			}
			catch (ArgumentException)
			{
				return true;
			}
			using (processById)
			{
				try
				{
					if (processById.StartTime.ToUniversalTime().Ticks != plan.Started || !Same(processById.MainModule.FileName, plan.Executable))
					{
						return false;
					}
					processById.WaitForExit();
					return true;
				}
				catch (InvalidOperationException)
				{
					return true;
				}
				catch
				{
					return false;
				}
			}
		}

		private static List<string> SnapshotManaged(string root)
		{
			List<string> list = new List<string>();
			try
			{
				RejectRedirects(root);
				if (!Directory.Exists(root))
				{
					return list;
				}
				string[] directories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
				foreach (string path in directories)
				{
					string text = Path.Combine(path, "PaviseAuto.exe");
					if (StartupPayload.IsVerifiedExecutable(text, root))
					{
						list.Add(text);
					}
				}
			}
			catch
			{
			}
			return list;
		}

		private static bool DeleteVerified(string target, string expectedHash, int attempts, int delay)
		{
			for (int i = 0; i < attempts; i++)
			{
				try
				{
					RejectRedirects(target);
					if (!File.Exists(target))
					{
						return true;
					}
					if (!SafeFile(target) || !string.Equals(HashFile(target), expectedHash, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					FileAttributes attributes = File.GetAttributes(target);
					if ((attributes & FileAttributes.ReadOnly) != 0)
					{
						File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
					}
					File.Delete(target);
					if (!File.Exists(target))
					{
						return true;
					}
				}
				catch
				{
				}
				if (i + 1 < attempts)
				{
					Thread.Sleep(delay);
				}
			}
			return false;
		}

		private static bool SafeFile(string path)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				RejectRedirects(path);
				return !Directory.Exists(path);
			}
			catch
			{
				return false;
			}
		}

		private static void RejectRedirects(string path)
		{
			string text = Path.GetFullPath(path);
			while (!string.IsNullOrEmpty(text))
			{
				if ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("Redirected cleanup path");
				}
				text = Path.GetDirectoryName(text);
			}
		}

		private static bool IsHelperDirectory(string directory)
		{
			try
			{
				string fileName = Path.GetFileName(directory);
				Guid result;
				return fileName.StartsWith("Pavise-Uninstall-", StringComparison.Ordinal) && Guid.TryParseExact(fileName.Substring("Pavise-Uninstall-".Length), "N", out result) && Same(Path.GetDirectoryName(directory), Path.GetTempPath());
			}
			catch
			{
				return false;
			}
		}

		private static void WritePlan(string file, Plan plan)
		{
			using (FileStream output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				using (BinaryWriter binaryWriter = new BinaryWriter(output, new UTF8Encoding(false)))
				{
					binaryWriter.Write("Pavise uninstall 1");
					binaryWriter.Write(plan.Pid);
					binaryWriter.Write(plan.Started);
					binaryWriter.Write(plan.Executable);
					binaryWriter.Write(plan.Hash);
					binaryWriter.Write(plan.ManagedRoot);
					binaryWriter.Write(plan.Managed.Count);
					foreach (string item in plan.Managed)
					{
						binaryWriter.Write(item);
					}
				}
			}
		}

		private static Plan ReadPlan(string file)
		{
			using (FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				using (BinaryReader binaryReader = new BinaryReader(fileStream, new UTF8Encoding(false)))
				{
					if (fileStream.Length > 65536 || binaryReader.ReadString() != "Pavise uninstall 1")
					{
						throw new IOException("Invalid cleanup plan");
					}
					Plan plan = new Plan();
					plan.Pid = binaryReader.ReadInt32();
					plan.Started = binaryReader.ReadInt64();
					plan.Executable = binaryReader.ReadString();
					plan.Hash = binaryReader.ReadString();
					plan.ManagedRoot = binaryReader.ReadString();
					int num = binaryReader.ReadInt32();
					if (num < 0 || num > 64)
					{
						throw new IOException("Invalid managed payload count");
					}
					for (int i = 0; i < num; i++)
					{
						plan.Managed.Add(binaryReader.ReadString());
					}
					if (fileStream.Position != fileStream.Length)
					{
						throw new IOException("Unexpected cleanup plan data");
					}
					return plan;
				}
			}
		}

		private static string HashFile(string path)
		{
			using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				return Hash(input);
			}
		}

		private static string Hash(Stream input)
		{
			using (SHA256 sHA = SHA256.Create())
			{
				byte[] array = sHA.ComputeHash(input);
				StringBuilder stringBuilder = new StringBuilder(array.Length * 2);
				byte[] array2 = array;
				foreach (byte b in array2)
				{
					stringBuilder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
				}
				return stringBuilder.ToString();
			}
		}

		private static bool IsHash(string text)
		{
			if (text == null || text.Length != 64)
			{
				return false;
			}
			foreach (char character in text)
			{
				if (!Uri.IsHexDigit(character))
				{
					return false;
				}
			}
			return true;
		}

		private static bool Same(string first, string second)
		{
			try
			{
				return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static bool IsUnder(string path, string root)
		{
			try
			{
				return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		private static string Quote(string value)
		{
			return "\"" + value.Replace("\"", "\\\"") + "\"";
		}

		private static void TryRemoveEmpty(string path)
		{
			try
			{
				RejectRedirects(path);
				if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
				{
					Directory.Delete(path, false);
				}
			}
			catch
			{
			}
		}

		private static void RemoveLaunchFiles(string directory)
		{
			if (!IsHelperDirectory(directory))
			{
				return;
			}
			string[] array = new string[2] { "cleanup.dat", "Pavise.Uninstall.exe" };
			foreach (string path in array)
			{
				try
				{
					RejectRedirects(Path.Combine(directory, path));
					File.Delete(Path.Combine(directory, path));
				}
				catch
				{
				}
			}
			TryRemoveEmpty(directory);
		}

		private static void QueueHelperCleanup(string directory)
		{
			try
			{
				RejectRedirects(directory);
				int id = Process.GetCurrentProcess().Id;
				string s = "$p=Get-Process -Id " + id.ToString(CultureInfo.InvariantCulture) + " -ErrorAction SilentlyContinue;if($p){$p.WaitForExit()};for($i=0;$i -lt 40;$i++){@('Pavise.Uninstall.exe','cleanup.dat','result.txt')|ForEach-Object{$f=Join-Path '" + directory.Replace("'", "''") + "' $_;if(Test-Path -LiteralPath $f -PathType Leaf){Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue}};Remove-Item -LiteralPath '" + directory.Replace("'", "''") + "' -ErrorAction SilentlyContinue;if(!(Test-Path -LiteralPath '" + directory.Replace("'", "''") + "')){break};Start-Sleep -Milliseconds 100}";
				string text = Convert.ToBase64String(Encoding.Unicode.GetBytes(s));
				string fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
				ProcessStartInfo processStartInfo = new ProcessStartInfo(fileName, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + text);
				processStartInfo.UseShellExecute = false;
				processStartInfo.CreateNoWindow = true;
				processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
				processStartInfo.WorkingDirectory = Path.GetTempPath();
				using (Process.Start(processStartInfo))
				{
				}
			}
			catch
			{
			}
		}
	}
}
