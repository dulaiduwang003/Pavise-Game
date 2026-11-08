using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
	internal static class LolAddonCleaner
	{
		internal sealed class CandidateInfo
		{
			public string RelativePath;

			public string FullPath;

			public bool Exists;

			public bool IsSafe;

			public long Bytes;

			public int FileCount;

			public string Error;
		}

		internal sealed class Inspection
		{
			public string RootPath;

			public bool IsValidRoot;

			public readonly List<CandidateInfo> Candidates = new List<CandidateInfo>();

			public long CandidateBytes;

			public int CandidateCount;

			public bool IsBlocked;

			public readonly List<string> BlockingProcesses = new List<string>();

			public string Error;

			public bool CanDelete
			{
				get
				{
					if (IsValidRoot && string.IsNullOrEmpty(Error) && !IsBlocked)
					{
						return CandidateCount > 0;
					}
					return false;
				}
			}
		}

		internal sealed class OperationItem
		{
			public string RelativePath;

			public string SourcePath;

			public long Bytes;

			public bool Success;

			public string Message;
		}

		internal sealed class OperationResult
		{
			public bool Success;

			public bool Changed;

			public long Bytes;

			public int DeletedCount;

			public int FailedCount;

			public string Message;

			public readonly List<OperationItem> Items = new List<OperationItem>();
		}

		private static readonly object Gate = new object();

		private static readonly string[] CandidatePaths = new string[1] { "Cross" };

		public static bool TryResolveRoot(string input, out string root, out string error)
		{
			root = null;
			error = null;
			if (string.IsNullOrWhiteSpace(input))
			{
				error = Lang.T("lolq.err.noinput");
				return false;
			}
			string path;
			try
			{
				path = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
				path = (File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : Path.GetFullPath(path));
			}
			catch
			{
				error = Lang.T("lolq.err.badformat");
				return false;
			}
			for (int i = 0; i < 12; i++)
			{
				if (string.IsNullOrEmpty(path))
				{
					break;
				}
				string error2;
				if (IsLolRootShape(path, out error2))
				{
					root = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					return true;
				}
				string fullName;
				try
				{
					fullName = ((Directory.GetParent(path) == null) ? null : Directory.GetParent(path).FullName);
				}
				catch
				{
					fullName = null;
				}
				if (string.IsNullOrEmpty(fullName) || SamePath(fullName, path))
				{
					break;
				}
				path = fullName;
			}
			error = Lang.T("lolq.err.notroot");
			return false;
		}

		public static Inspection Inspect(string root)
		{
			return Inspect(root, true);
		}

		public static Inspection InspectAvailability(string root)
		{
			return Inspect(root, false);
		}

		private static Inspection Inspect(string root, bool fullInspection)
		{
			Inspection inspection = new Inspection();
			string root2;
			string error;
			if (!TryResolveRoot(root, out root2, out error))
			{
				inspection.Error = error;
				return inspection;
			}
			inspection.RootPath = root2;
			inspection.IsValidRoot = true;
			if (fullInspection)
			{
				List<string> names;
				inspection.IsBlocked = HasBlockingProcesses(root2, out names);
				inspection.BlockingProcesses.AddRange(names);
			}
			string[] candidatePaths = CandidatePaths;
			foreach (string relativePath in candidatePaths)
			{
				CandidateInfo candidateInfo = ScanCandidate(root2, relativePath, fullInspection);
				inspection.Candidates.Add(candidateInfo);
				if (candidateInfo.Exists)
				{
					inspection.CandidateCount++;
					inspection.CandidateBytes = AddSaturated(inspection.CandidateBytes, candidateInfo.Bytes);
					if (!candidateInfo.IsSafe)
					{
						inspection.Error = JoinError(inspection.Error, candidateInfo.RelativePath + " " + candidateInfo.Error);
					}
				}
			}
			return inspection;
		}

		public static bool HasBlockingProcesses(string root, out List<string> names)
		{
			names = new List<string>();
			string root2;
			string error;
			if (!TryResolveRoot(root, out root2, out error))
			{
				names.Add(Lang.T("lolq.err.rootunresolved"));
				return true;
			}
			string rootPrefix = EnsureSeparator(root2);
			Process[] processes;
			try
			{
				processes = Process.GetProcesses();
			}
			catch
			{
				names.Add(Lang.T("lolq.err.enumproc"));
				return true;
			}
			try
			{
				Process[] processesSnapshot = processes;
				foreach (Process process in processesSnapshot)
				{
					string processName = null;
					int hasBlockingProcessesValue = 0;
					string path = null;
					try
					{
						processName = process.ProcessName;
					}
					catch
					{
					}
					try
					{
						hasBlockingProcessesValue = process.Id;
					}
					catch
					{
					}
					try
					{
						path = ProcessPath(process);
					}
					catch
					{
					}
					if (IsBlockingProcess(processName, path, rootPrefix))
					{
						names.Add((string.IsNullOrEmpty(processName) ? Lang.T("lolq.proc.unknown") : processName) + " PID " + hasBlockingProcessesValue);
					}
				}
			}
			finally
			{
				Process[] processesSnapshot2 = processes;
				foreach (Process process2 in processesSnapshot2)
				{
					try
					{
						process2.Dispose();
					}
					catch
					{
					}
				}
			}
			return names.Count > 0;
		}

		public static OperationResult Delete(string root)
		{
			lock (Gate)
			{
				Inspection inspection = Inspect(root);
				OperationResult operationResult = RejectDelete(inspection);
				if (operationResult != null)
				{
					return operationResult;
				}
				OperationResult operationResult2 = new OperationResult();
				foreach (CandidateInfo candidate in inspection.Candidates)
				{
					if (!candidate.Exists)
					{
						continue;
					}
					OperationItem operationItem = new OperationItem();
					operationItem.RelativePath = candidate.RelativePath;
					operationItem.SourcePath = candidate.FullPath;
					operationItem.Bytes = candidate.Bytes;
					operationResult2.Items.Add(operationItem);
					List<string> names;
					if (HasBlockingProcesses(inspection.RootPath, out names))
					{
						operationItem.Message = Lang.F("lolq.err.clientrunning", string.Join(" ", names.ToArray()));
						operationResult2.FailedCount++;
						continue;
					}
					if (File.Exists(operationItem.SourcePath))
					{
						operationItem.Message = Lang.T("lolq.item.notdir");
						operationResult2.FailedCount++;
						continue;
					}
					if (!Directory.Exists(operationItem.SourcePath))
					{
						operationItem.Message = Lang.T("lolq.item.vanished");
						operationResult2.FailedCount++;
						continue;
					}
					try
					{
						EnsureSafeParent(inspection.RootPath, Path.GetDirectoryName(operationItem.SourcePath));
						if (!SafeDirectory(operationItem.SourcePath))
						{
							throw new IOException(Lang.T("lolq.err.srcreparse"));
						}
						DeleteTree(new DirectoryInfo(operationItem.SourcePath));
						if (Directory.Exists(operationItem.SourcePath) || File.Exists(operationItem.SourcePath))
						{
							throw new IOException(Lang.T("lolq.err.deleteincomplete"));
						}
						operationItem.Success = true;
						operationItem.Message = Lang.T("lolq.item.deleted");
						operationResult2.DeletedCount++;
						operationResult2.Bytes = AddSaturated(operationResult2.Bytes, candidate.Bytes);
						operationResult2.Changed = true;
					}
					catch (Exception ex)
					{
						operationItem.Message = Lang.F("lolq.item.deletefail", ex.Message);
						operationResult2.FailedCount++;
					}
				}
				operationResult2.Success = operationResult2.Changed && operationResult2.FailedCount == 0;
				operationResult2.Message = (operationResult2.Success ? Lang.T("lolq.msg.deldone") : (operationResult2.Changed ? Lang.T("lolq.msg.delpartial") : Lang.T("lolq.msg.delnone")));
				return operationResult2;
			}
		}

		private static OperationResult RejectDelete(Inspection inspection)
		{
			if (!inspection.IsValidRoot)
			{
				return Failure(inspection.Error);
			}
			if (!string.IsNullOrEmpty(inspection.Error))
			{
				return Failure(inspection.Error);
			}
			if (inspection.IsBlocked)
			{
				return Failure(Lang.F("lolq.err.closefirst", string.Join(" ", inspection.BlockingProcesses.ToArray())));
			}
			if (inspection.CandidateCount == 0)
			{
				return Failure(Lang.T("lolq.err.nocandidate"));
			}
			return null;
		}

		private static OperationResult Failure(string message)
		{
			OperationResult operationResult = new OperationResult();
			operationResult.Message = (string.IsNullOrEmpty(message) ? Lang.T("lolq.err.generic") : message);
			operationResult.FailedCount = 1;
			return operationResult;
		}

		private static bool IsLolRootShape(string path, out string error)
		{
			error = null;
			try
			{
				DirectoryInfo directoryInfo = new DirectoryInfo(path);
				if (!directoryInfo.Exists)
				{
					return false;
				}
				directoryInfo.Refresh();
				if (IsReparse(directoryInfo))
				{
					return false;
				}
				if (SamePath(directoryInfo.FullName, Path.GetPathRoot(directoryInfo.FullName)))
				{
					return false;
				}
				string path2 = Path.Combine(directoryInfo.FullName, "Game");
				string leagueClientPath = Path.Combine(directoryInfo.FullName, "LeagueClient");
				if (!SafeDirectory(path2) || !SafeDirectory(leagueClientPath))
				{
					return false;
				}
				if (!File.Exists(Path.Combine(leagueClientPath, "LeagueClient.exe")))
				{
					return false;
				}
				return File.Exists(Path.Combine(directoryInfo.FullName, "TCLS\\Client.exe")) || File.Exists(Path.Combine(directoryInfo.FullName, "Launcher\\Client.exe")) || File.Exists(Path.Combine(directoryInfo.FullName, "WeGameLauncher\\launcher.exe")) || File.Exists(Path.Combine(directoryInfo.FullName, "Riot Client\\RiotClientServices.exe"));
			}
			catch
			{
				return false;
			}
		}

		private static CandidateInfo ScanCandidate(string root, string relativePath, bool fullInspection)
		{
			CandidateInfo candidateInfo = new CandidateInfo();
			candidateInfo.RelativePath = relativePath;
			candidateInfo.FullPath = CombineRelative(root, relativePath);
			candidateInfo.IsSafe = true;
			try
			{
				if (File.Exists(candidateInfo.FullPath))
				{
					candidateInfo.Exists = true;
					candidateInfo.IsSafe = false;
					candidateInfo.Error = Lang.T("lolq.err.candnotdir");
					return candidateInfo;
				}
				DirectoryInfo directoryInfo = new DirectoryInfo(candidateInfo.FullPath);
				if (!directoryInfo.Exists)
				{
					return candidateInfo;
				}
				EnsureSafeParent(root, Path.GetDirectoryName(candidateInfo.FullPath));
				candidateInfo.Exists = true;
				directoryInfo.Refresh();
				if (IsReparse(directoryInfo))
				{
					candidateInfo.IsSafe = false;
					candidateInfo.Error = Lang.T("lolq.err.candreparse");
					return candidateInfo;
				}
				if (!fullInspection)
				{
					return candidateInfo;
				}
				string error;
				if (!MeasureDirectory(directoryInfo, out candidateInfo.Bytes, out candidateInfo.FileCount, out error))
				{
					candidateInfo.IsSafe = false;
					candidateInfo.Error = error;
				}
			}
			catch (Exception ex)
			{
				candidateInfo.Exists = true;
				candidateInfo.IsSafe = false;
				candidateInfo.Error = ex.Message;
			}
			return candidateInfo;
		}

		private static bool MeasureDirectory(DirectoryInfo root, out long bytes, out int fileCount, out string error)
		{
			bytes = 0L;
			fileCount = 0;
			error = null;
			Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				DirectoryInfo directoryInfo = stack.Pop();
				FileInfo[] files;
				DirectoryInfo[] directories;
				try
				{
					files = directoryInfo.GetFiles();
					directories = directoryInfo.GetDirectories();
				}
				catch (Exception ex)
				{
					error = Lang.F("lolq.err.scanfail", ex.Message);
					return false;
				}
				FileInfo[] filesSnapshot = files;
				foreach (FileInfo fileInfo in filesSnapshot)
				{
					try
					{
						fileInfo.Refresh();
						if (IsReparse(fileInfo))
						{
							error = Lang.F("lolq.err.filereparse", fileInfo.Name);
							return false;
						}
						bytes = AddSaturated(bytes, fileInfo.Length);
						if (fileCount < int.MaxValue)
						{
							fileCount++;
						}
					}
					catch (Exception ex2)
					{
						error = Lang.F("lolq.err.readfile", ex2.Message);
						return false;
					}
				}
				DirectoryInfo[] directoriesSnapshot = directories;
				foreach (DirectoryInfo directoryInfo2 in directoriesSnapshot)
				{
					try
					{
						directoryInfo2.Refresh();
						if (IsReparse(directoryInfo2))
						{
							error = Lang.F("lolq.err.dirreparse", directoryInfo2.Name);
							return false;
						}
						stack.Push(directoryInfo2);
					}
					catch (Exception ex3)
					{
						error = Lang.F("lolq.err.readdir", ex3.Message);
						return false;
					}
				}
			}
			return true;
		}

		private static bool DeleteTree(DirectoryInfo directory)
		{
			bool result = true;
			FileInfo[] files;
			DirectoryInfo[] directories;
			try
			{
				directory.Refresh();
				if (!directory.Exists)
				{
					return true;
				}
				if (IsReparse(directory))
				{
					directory.Delete(false);
					return true;
				}
				files = directory.GetFiles();
				directories = directory.GetDirectories();
			}
			catch
			{
				return false;
			}
			FileInfo[] filesSnapshot = files;
			foreach (FileInfo fileInfo in filesSnapshot)
			{
				try
				{
					fileInfo.Refresh();
					if ((fileInfo.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
					{
						fileInfo.Attributes = FileAttributes.Normal;
					}
					fileInfo.Delete();
				}
				catch
				{
					result = false;
				}
			}
			DirectoryInfo[] directoriesSnapshot = directories;
			foreach (DirectoryInfo directory2 in directoriesSnapshot)
			{
				if (!DeleteTree(directory2))
				{
					result = false;
				}
			}
			try
			{
				directory.Refresh();
				if (!directory.Exists)
				{
					return result;
				}
				if ((directory.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
				{
					directory.Attributes = FileAttributes.Normal;
				}
				directory.Delete(false);
			}
			catch
			{
				result = false;
			}
			return result;
		}

		private static bool IsBlockingProcess(string name, string path, string rootPrefix)
		{
			string toLowerInvariantResult = (string.IsNullOrEmpty(name) ? "" : name.Trim().ToLowerInvariant());
			if (toLowerInvariantResult.StartsWith("wegame", StringComparison.Ordinal))
			{
				goto MarkProcessAlive;
			}
			switch (toLowerInvariantResult)
			{
			case "pallas":
			case "rail":
			case "tcls_core":
			case "riotclientservices":
			case "leagueclient":
			case "leagueclientux":
			case "leagueclientuxrender":
			case "league of legends":
			case "crossproxy":
			case "lolaicoach":
			case "aicoachapp":
			case "icreatelol":
			case "tqmcenter":
			case "yxqxunyou":
				goto MarkProcessAlive;
			}
			int isBlockingProcessValue = ((toLowerInvariantResult == "sguard64") ? 1 : 0);
			goto InspectProcessPath;
			InspectProcessPath:
			bool matchesIsBlockingProcessValue = (byte)isBlockingProcessValue != 0;
			string fullPath = null;
			if (!string.IsNullOrEmpty(path))
			{
				try
				{
					fullPath = Path.GetFullPath(path);
				}
				catch
				{
					fullPath = path;
				}
			}
			if (fullPath != null && fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (matchesIsBlockingProcessValue)
			{
				return true;
			}
			if (fullPath == null)
			{
				return false;
			}
			string replaceResult = fullPath.Replace('/', '\\');
			if (replaceResult.IndexOf("\\WeGame\\", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				switch (toLowerInvariantResult)
				{
				default:
					return toLowerInvariantResult == "tgp_daemon";
				case "browser":
				case "teniodl":
				case "crashpad_handler":
					return true;
				}
			}
			return false;
			MarkProcessAlive:
			isBlockingProcessValue = 1;
			goto InspectProcessPath;
		}

		private static string ProcessPath(Process process)
		{
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				intPtr = Native.OpenProcess(4096, false, process.Id);
				if (intPtr != IntPtr.Zero)
				{
					string imagePathResult = Native.ImagePath(intPtr);
					if (!string.IsNullOrEmpty(imagePathResult))
					{
						return imagePathResult;
					}
				}
			}
			catch
			{
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Native.CloseHandle(intPtr);
				}
			}
			try
			{
				return (process.MainModule == null) ? null : process.MainModule.FileName;
			}
			catch
			{
				return null;
			}
		}

		private static string CombineRelative(string root, string relativePath)
		{
			string getFullPathTrimmed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string fullPath = Path.GetFullPath(Path.Combine(getFullPathTrimmed, relativePath));
			if (!fullPath.StartsWith(EnsureSeparator(getFullPathTrimmed), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(Lang.T("lolq.err.outsideroot"));
			}
			return fullPath;
		}

		private static void EnsureSafeParent(string root, string parent)
		{
			string getFullPathTrimmed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string fullPath = Path.GetFullPath(parent);
			if (!fullPath.StartsWith(EnsureSeparator(getFullPathTrimmed), StringComparison.OrdinalIgnoreCase) && !SamePath(fullPath, getFullPathTrimmed))
			{
				throw new InvalidOperationException(Lang.T("lolq.err.outsiderootr"));
			}
			while (!SamePath(fullPath, getFullPathTrimmed))
			{
				DirectoryInfo directoryInfo = new DirectoryInfo(fullPath);
				if (directoryInfo.Exists && IsReparse(directoryInfo))
				{
					throw new IOException(Lang.T("lolq.err.pathreparse"));
				}
				DirectoryInfo parent2 = directoryInfo.Parent;
				if (parent2 == null)
				{
					throw new IOException(Lang.T("lolq.err.pathinvalid"));
				}
				fullPath = parent2.FullName;
			}
		}

		private static bool SafeDirectory(string path)
		{
			try
			{
				DirectoryInfo directoryInfo = new DirectoryInfo(path);
				if (!directoryInfo.Exists)
				{
					return false;
				}
				directoryInfo.Refresh();
				return !IsReparse(directoryInfo);
			}
			catch
			{
				return false;
			}
		}

		private static bool IsReparse(FileSystemInfo info)
		{
			try
			{
				return (info.Attributes & FileAttributes.ReparsePoint) != 0;
			}
			catch
			{
				return true;
			}
		}

		private static string EnsureSeparator(string path)
		{
			return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		}

		private static bool SamePath(string left, string right)
		{
			if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
			{
				return false;
			}
			try
			{
				left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			}
			catch
			{
				return false;
			}
			return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
		}

		private static long AddSaturated(long left, long right)
		{
			if (right <= 0)
			{
				return left;
			}
			if (left <= long.MaxValue - right)
			{
				return left + right;
			}
			return long.MaxValue;
		}

		private static string JoinError(string current, string next)
		{
			if (string.IsNullOrEmpty(next))
			{
				return current;
			}
			if (!string.IsNullOrEmpty(current))
			{
				return current + " " + next;
			}
			return next;
		}
	}
}
