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
			string text = path;
			while (!string.IsNullOrEmpty(text))
			{
				if ((File.Exists(text) || Directory.Exists(text)) && (File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("SPI ownership path crosses a reparse point");
				}
				text = Path.GetDirectoryName(text);
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
				byte[] array = new byte[(int)fileStream.Length];
				int num;
				for (int i = 0; i < array.Length; i += num)
				{
					num = fileStream.Read(array, i, array.Length - i);
					if (num <= 0)
					{
						throw new EndOfStreamException();
					}
				}
				return array;
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
			string text = LegacyPath(name);
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768)
			{
				throw new InvalidDataException("Invalid SPI migration metadata size");
			}
			byte[] array = ReadFile(text);
			if (array != null)
			{
				if (!SpiOwnerRecord.Equal(array, bytes))
				{
					throw new InvalidDataException("Immutable SPI migration metadata differs");
				}
				return SpiOwnerRecord.Digest(bytes);
			}
			string text2 = Path.Combine(Path.GetDirectoryName(text), ".spi-legacy-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool flag = false;
			try
			{
				CheckPath(text2);
				using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					flag = true;
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				CheckPath(text);
				File.Move(text2, text);
				return SpiOwnerRecord.Digest(bytes);
			}
			finally
			{
				if (flag && File.Exists(text2))
				{
					File.Delete(text2);
				}
			}
		}

		internal string PublishInitialization(byte[] bytes)
		{
			string text = Path.Combine(Path.GetDirectoryName(PathName), "initialization.tsv");
			if (bytes == null || bytes.Length == 0 || bytes.Length > 32768 || ReadFile(text) != null)
			{
				throw new InvalidDataException("SPI initialization evidence is absent, invalid or already exists");
			}
			string text2 = Path.Combine(Path.GetDirectoryName(text), ".spi-init-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool flag = false;
			try
			{
				CheckPath(text2);
				using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					flag = true;
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				CheckPath(text);
				File.Move(text2, text);
				return SpiOwnerRecord.Digest(bytes);
			}
			finally
			{
				if (flag && File.Exists(text2))
				{
					File.Delete(text2);
				}
			}
		}

		internal void ValidateInitialization(string sid, SpiOwnerRecord record)
		{
			byte[] array = ReadInitialization();
			if (array == null)
			{
				throw new InvalidDataException("SPI owner initialization evidence is missing");
			}
			string @string = new UTF8Encoding(false, true).GetString(array);
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
			string text = list[3].Substring(15);
			if (text != "none")
			{
				SpiOwnerIdentity.Hash(text);
				byte[] array2 = ReadLegacy("legacy-close.tsv");
				if (array2 == null || SpiOwnerRecord.Digest(array2) != text)
				{
					throw new InvalidDataException("SPI legacy closure attachment is missing or changed");
				}
			}
			if (record.Generation == 0 && SpiOwnerRecord.Digest(array) != record.ClosureDigest)
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
			string text = ClosurePath(stamp);
			CheckPath(text);
			byte[] array = ReadFile(text);
			if (array != null)
			{
				if (!SpiOwnerRecord.Equal(array, bytes))
				{
					throw new InvalidDataException("Immutable SPI closure manifest differs");
				}
				return SpiOwnerRecord.Digest(bytes);
			}
			string text2 = Path.Combine(Path.GetDirectoryName(text), ".spi-close-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool flag = false;
			try
			{
				CheckPath(text2);
				using (FileStream fileStream = new FileStream(text2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					flag = true;
					fileStream.Write(bytes, 0, bytes.Length);
					fileStream.Flush(true);
				}
				CheckPath(text);
				File.Move(text2, text);
				return SpiOwnerRecord.Digest(bytes);
			}
			finally
			{
				if (flag && File.Exists(text2))
				{
					File.Delete(text2);
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
			string text = Path.Combine(directoryName, ".spi-owner-" + Guid.NewGuid().ToString("N") + ".tmp");
			bool flag = false;
			try
			{
				CheckPath(text);
				using (FileStream fileStream = new FileStream(text, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					flag = true;
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
					File.Move(text, PathName);
				}
				else
				{
					File.Replace(text, PathName, null);
				}
			}
			finally
			{
				if (flag && File.Exists(text))
				{
					File.Delete(text);
				}
			}
		}
	}
}
