using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class SpiOwnerFileStore
	{
		internal const string InitializationSchema = "pavise-spi-initialization-3";

		internal readonly string PathName;

		internal SpiOwnerFileStore(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
			{
				throw new ArgumentException("SPI owner path must be absolute");
			}
			PathName = Path.GetFullPath(path);
			CheckPath(PathName);
		}

		internal static string CanonicalPath(string sid)
		{
			SpiOwnerIdentity.Sid(sid);
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (string.IsNullOrEmpty(folderPath) || !Path.IsPathRooted(folderPath))
			{
				throw new IOException("Canonical SPI ownership storage unavailable");
			}
			return Path.Combine(folderPath, "PaviseAuto", "SharedSpi", sid, "visual-effects-owner.tsv");
		}

		private static void CheckPath(string path)
		{
			string pathSnapshot = path;
			while (!string.IsNullOrEmpty(pathSnapshot))
			{
				if ((File.Exists(pathSnapshot) || Directory.Exists(pathSnapshot)) && (File.GetAttributes(pathSnapshot) & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("SPI ownership path crosses a reparse point");
				}
				pathSnapshot = Path.GetDirectoryName(pathSnapshot);
			}
		}

		internal byte[] Read()
		{
			return ReadFile(PathName);
		}

		private static byte[] ReadFile(string path)
		{
			CheckPath(path);
			if (!File.Exists(path))
			{
				return null;
			}
			using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
			{
				if (fileStream.Length <= 0 || fileStream.Length > 32768)
				{
					throw new InvalidDataException("Invalid SPI owner snapshot size");
				}
				byte[] rawBytes = new byte[(int)fileStream.Length];
				int fileValue;
				for (int i = 0; i < rawBytes.Length; i += fileValue)
				{
					fileValue = fileStream.Read(rawBytes, i, rawBytes.Length - i);
					if (fileValue <= 0)
					{
						throw new EndOfStreamException();
					}
				}
				return rawBytes;
			}
		}

		private string ClosurePath(SpiOwnerStamp stamp)
		{
			if (stamp == null)
			{
				throw new ArgumentNullException("stamp");
			}
			return Path.Combine(Path.GetDirectoryName(PathName), "close-" + stamp.Generation.ToString(CultureInfo.InvariantCulture) + "-" + stamp.Token + ".tsv");
		}

		internal byte[] ReadClosure(SpiOwnerStamp stamp)
		{
			return ReadFile(ClosurePath(stamp));
		}

		internal byte[] ReadInitialization()
		{
			return ReadFile(Path.Combine(Path.GetDirectoryName(PathName), "initialization.tsv"));
		}

		private string LegacyPath(string name)
		{
			if (name != "legacy-migration.tsv" && name != "legacy-close.tsv")
			{
				throw new InvalidDataException("Unknown SPI migration metadata");
			}
			return Path.Combine(Path.GetDirectoryName(PathName), name);
		}

		internal byte[] ReadLegacy(string name)
		{
			return ReadFile(LegacyPath(name));
		}

		internal string PublishLegacy(string name, byte[] bytes)
		{
			string legacyPathResult = LegacyPath(name);
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768)
			{
				throw new InvalidDataException("Invalid SPI migration metadata size");
			}
			byte[] file = ReadFile(legacyPathResult);
			if (file != null)
			{
				if (!SpiOwnerRecord.Equal(file, bytes))
				{
					throw new InvalidDataException("Immutable SPI migration metadata differs");
				}
				return SpiOwnerRecord.Digest(bytes);
			}
			string combinedPath = Path.Combine(Path.GetDirectoryName(legacyPathResult), ".spi-legacy-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool publishLegacyReady = false;
			try
			{
				CheckPath(combinedPath);
				using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					publishLegacyReady = true;
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				CheckPath(legacyPathResult);
				File.Move(combinedPath, legacyPathResult);
				return SpiOwnerRecord.Digest(bytes);
			}
			finally
			{
				if (publishLegacyReady && File.Exists(combinedPath))
				{
					File.Delete(combinedPath);
				}
			}
		}

		internal string PublishInitialization(byte[] bytes)
		{
			string initializationPath = Path.Combine(Path.GetDirectoryName(PathName), "initialization.tsv");
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768 || ReadFile(initializationPath) != null)
			{
				throw new InvalidDataException("SPI initialization evidence is absent, invalid or already exists");
			}
			string combinedPath = Path.Combine(Path.GetDirectoryName(initializationPath), ".spi-init-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool publishInitializationReady = false;
			try
			{
				CheckPath(combinedPath);
				using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					publishInitializationReady = true;
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				CheckPath(initializationPath);
				File.Move(combinedPath, initializationPath);
				return SpiOwnerRecord.Digest(bytes);
			}
			finally
			{
				if (publishInitializationReady && File.Exists(combinedPath))
				{
					File.Delete(combinedPath);
				}
			}
		}

		internal void ValidateInitialization(string sid, SpiOwnerRecord record)
		{
			byte[] initialization = ReadInitialization();
			if (initialization == null)
			{
				throw new InvalidDataException("SPI owner initialization evidence is missing");
			}
			string @string = new UTF8Encoding(false, true).GetString(initialization);
			List<string> list = new List<string>(@string.Split('\n'));
			if (list.Count == 8 && list[0] == "pavise-spi-initialization-2")
			{
				if (!list[3].StartsWith("known_root64\t", StringComparison.Ordinal) || !list[4].StartsWith("root_ledger\t", StringComparison.Ordinal))
				{
					throw new InvalidDataException("Invalid SPI owner initialization evidence");
				}
				list.RemoveRange(3, 2);
				list[0] = "pavise-spi-initialization-3";
			}
			if (list.Count != 6 || list[0] != "pavise-spi-initialization-3" || list[1] != "user_sid\t" + sid || !list[2].StartsWith("nonce\t", StringComparison.Ordinal) || !list[3].StartsWith("legacy_closure\t", StringComparison.Ordinal) || list[4] != "main\toriginal-single-instance-lease-v1" || list[5] != "")
			{
				throw new InvalidDataException("Invalid SPI owner initialization evidence");
			}
			SpiOwnerIdentity.Nonce(list[2].Substring(6));
			string substringResult = list[3].Substring(15);
			if (substringResult != "none")
			{
				SpiOwnerIdentity.Hash(substringResult);
				byte[] legacy = ReadLegacy("legacy-close.tsv");
				if (legacy == null || SpiOwnerRecord.Digest(legacy) != substringResult)
				{
					throw new InvalidDataException("SPI legacy closure attachment is missing or changed");
				}
			}
			if (record.Generation == 0 && SpiOwnerRecord.Digest(initialization) != record.ClosureDigest)
			{
				throw new InvalidDataException("Initial closed slot differs from bootstrap evidence");
			}
		}

		internal string PublishClosure(SpiOwnerStamp stamp, byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768)
			{
				throw new InvalidDataException("Invalid SPI closure manifest size");
			}
			string closurePathResult = ClosurePath(stamp);
			CheckPath(closurePathResult);
			byte[] file = ReadFile(closurePathResult);
			if (file != null)
			{
				if (!SpiOwnerRecord.Equal(file, bytes))
				{
					throw new InvalidDataException("Immutable SPI closure manifest differs");
				}
				return SpiOwnerRecord.Digest(bytes);
			}
			string combinedPath = Path.Combine(Path.GetDirectoryName(closurePathResult), ".spi-close-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool publishClosureReady = false;
			try
			{
				CheckPath(combinedPath);
				using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					publishClosureReady = true;
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				CheckPath(closurePathResult);
				File.Move(combinedPath, closurePathResult);
				return SpiOwnerRecord.Digest(bytes);
			}
			finally
			{
				if (publishClosureReady && File.Exists(combinedPath))
				{
					File.Delete(combinedPath);
				}
			}
		}

		internal void Publish(byte[] expected, byte[] after)
		{
			CheckPath(PathName);
			SpiOwnerRecord.Parse(after);
			if (!SpiOwnerRecord.Equal(expected, Read()))
			{
				throw new InvalidOperationException("SPI owner snapshot changed before publication");
			}
			string directoryName = Path.GetDirectoryName(PathName);
			CheckPath(directoryName);
			if (!Directory.Exists(directoryName))
			{
				throw new DirectoryNotFoundException("SPI ownership directory must be explicitly initialized");
			}
			string combinedPath = Path.Combine(directoryName, ".spi-owner-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool publishReady = false;
			try
			{
				CheckPath(combinedPath);
				using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					publishReady = true;
					fileStream.Write(after, 0, after.Length);
					fileStream.Flush(true);
				}
				CheckPath(PathName);
				if (!SpiOwnerRecord.Equal(expected, Read()))
				{
					throw new InvalidOperationException("SPI owner snapshot changed during publication");
				}
				if (expected == null)
				{
					File.Move(combinedPath, PathName);
				}
				else
				{
					File.Replace(combinedPath, PathName, null);
				}
			}
			finally
			{
				if (publishReady && File.Exists(combinedPath))
				{
					File.Delete(combinedPath);
				}
			}
		}
	}
}
