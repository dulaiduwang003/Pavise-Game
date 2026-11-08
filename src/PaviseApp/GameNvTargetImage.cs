using System;
using System.IO;
using System.Security.Cryptography;

namespace PaviseApp
{
	internal sealed class GameNvTargetImage : IDisposable
	{
		private FileStream original;

		internal readonly string Executable;

		internal readonly string Hash;

		private GameNvTargetImage(string path, FileStream source, string hash)
		{
			Executable = path;
			original = source;
			Hash = hash;
		}

		internal static GameNvTargetImage Open(string executable, string expectedHash = null)
		{
			if (!Path.IsPathRooted(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Exact game image required");
			}
			string fullPath = Path.GetFullPath(executable);
			NoReparse.CheckPath(fullPath);
			if (!GamePerformanceTargetPolicy.AllowsGameTarget(fullPath))
			{
				throw new GameNvPreparationPending(GameNvPending.Target);
			}
			FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			try
			{
				if (fileStream.Length < 2 || fileStream.Length > 2147483648u)
				{
					throw new InvalidDataException("Game image outside its hash budget");
				}
				string toLowerInvariantResult;
				using (SHA256 sHA = SHA256.Create())
				{
					toLowerInvariantResult = BitConverter.ToString(sHA.ComputeHash(fileStream)).Replace("-", "").ToLowerInvariant();
				}
				fileStream.Position = 0L;
				if (expectedHash != null && expectedHash != toLowerInvariantResult)
				{
					throw new GameNvPreparationPending(GameNvPending.Target);
				}
				return new GameNvTargetImage(fullPath, fileStream, toLowerInvariantResult);
			}
			catch
			{
				fileStream.Dispose();
				throw;
			}
		}

		internal void RequireCurrent()
		{
			if (original == null || !original.CanRead)
			{
				throw new ObjectDisposedException("Original game image");
			}
			NoReparse.CheckPath(Executable);
		}

		public void Dispose()
		{
			if (original != null)
			{
				original.Dispose();
				original = null;
			}
		}
	}
}
