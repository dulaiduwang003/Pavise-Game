using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal static class ExecutableCandidateProbe
	{
		private sealed class DirectoryWork
		{
			internal string Path;

			internal int Depth;
		}

		private sealed class Section
		{
			internal uint Address;

			internal uint VirtualSize;

			internal uint RawSize;

			internal uint RawPointer;
		}

		private sealed class PeReader
		{
			internal Stream Stream;

			internal long Length;

			internal int Remaining = 196608;

			internal uint HeaderSize;

			internal ulong ImageBase;

			internal readonly List<Section> Sections = new List<Section>();

			internal byte[] Read(long offset, int length)
			{
				if (length <= 0 || length > Remaining || offset < 0 || offset > Length || length > Length - offset)
				{
					return null;
				}
				Remaining -= length;
				byte[] rawBytes = new byte[length];
				Stream.Position = offset;
				int value;
				for (int i = 0; i < length; i += value)
				{
					value = Stream.Read(rawBytes, i, length - i);
					if (value <= 0)
					{
						return null;
					}
				}
				return rawBytes;
			}

			internal long FileOffset(uint rva, long length)
			{
				if (length <= 0)
				{
					return -1L;
				}
				ulong rvaOffset = (ulong)(rva + length);
				if (rvaOffset > 4294967296L)
				{
					return -1L;
				}
				long fileOffsetResult = -1L;
				if (rva < HeaderSize && rvaOffset <= HeaderSize && rvaOffset <= (ulong)Length)
				{
					fileOffsetResult = rva;
				}
				foreach (Section section in Sections)
				{
					if (rva < section.Address)
					{
						continue;
					}
					ulong remainingRva = (ulong)rva - (ulong)section.Address;
					if (remainingRva >= Math.Max(section.RawSize, section.VirtualSize) || (ulong)((long)remainingRva + length) > (ulong)section.RawSize)
					{
						continue;
					}
					ulong rawPointerOffset = section.RawPointer + remainingRva;
					if ((ulong)((long)rawPointerOffset + length) <= (ulong)Length)
					{
						if (fileOffsetResult >= 0)
						{
							return -1L;
						}
						fileOffsetResult = (long)rawPointerOffset;
					}
				}
				return fileOffsetResult;
			}

			internal string ImportName(uint rva)
			{
				StringBuilder stringBuilder = new StringBuilder();
				while (stringBuilder.Length < 260)
				{
					ulong rvaOffset = (ulong)rva + (ulong)(uint)stringBuilder.Length;
					if (rvaOffset > uint.MaxValue)
					{
						return null;
					}
					int remainingLength = 260 - stringBuilder.Length;
					long fileOffsetResult = FileOffset((uint)rvaOffset, remainingLength);
					while (fileOffsetResult < 0 && remainingLength > 1)
					{
						remainingLength /= 2;
						fileOffsetResult = FileOffset((uint)rvaOffset, remainingLength);
					}
					byte[] readResult = Read(fileOffsetResult, remainingLength);
					if (readResult == null)
					{
						return null;
					}
					byte[] rawBytes = readResult;
					foreach (byte b in rawBytes)
					{
						switch (b)
						{
						case 0:
							return (stringBuilder.Length == 0) ? null : stringBuilder.ToString();
						default:
							return null;
						case 32:
						case 33:
						case 34:
						case 35:
						case 36:
						case 37:
						case 38:
						case 39:
						case 40:
						case 41:
						case 42:
						case 43:
						case 44:
						case 45:
						case 46:
						case 47:
						case 48:
						case 49:
						case 50:
						case 51:
						case 52:
						case 53:
						case 54:
						case 55:
						case 56:
						case 57:
						case 58:
						case 59:
						case 60:
						case 61:
						case 62:
						case 63:
						case 64:
						case 65:
						case 66:
						case 67:
						case 68:
						case 69:
						case 70:
						case 71:
						case 72:
						case 73:
						case 74:
						case 75:
						case 76:
						case 77:
						case 78:
						case 79:
						case 80:
						case 81:
						case 82:
						case 83:
						case 84:
						case 85:
						case 86:
						case 87:
						case 88:
						case 89:
						case 90:
						case 91:
						case 92:
						case 93:
						case 94:
						case 95:
						case 96:
						case 97:
						case 98:
						case 99:
						case 100:
						case 101:
						case 102:
						case 103:
						case 104:
						case 105:
						case 106:
						case 107:
						case 108:
						case 109:
						case 110:
						case 111:
						case 112:
						case 113:
						case 114:
						case 115:
						case 116:
						case 117:
						case 118:
						case 119:
						case 120:
						case 121:
						case 122:
						case 123:
						case 124:
						case 125:
						case 126:
							break;
						}
						stringBuilder.Append((char)b);
					}
				}
				return null;
			}
		}

		internal const int MaxDirectories = 512;

		internal const int MaxExecutables = 400;

		internal const int MaxSections = 96;

		internal const int MaxImports = 256;

		internal const int MaxReadBytes = 196608;

		internal const int MaxDirectoryDepth = 8;

		private static bool GraphicsDependency(string module)
		{
			if (string.IsNullOrEmpty(module))
			{
				return false;
			}
			switch (module.ToLowerInvariant())
			{
			case "d3d8.dll":
			case "d3d9.dll":
			case "d3d10.dll":
			case "d3d10_1.dll":
			case "d3d11.dll":
			case "d3d12.dll":
			case "opengl32.dll":
			case "vulkan-1.dll":
				return true;
			default:
				return false;
			}
		}

		internal static ExecutableCandidateFacts ReadFacts(Stream stream)
		{
			bool readFailed;
			return ReadFacts(stream, out readFailed);
		}

		private static ExecutableCandidateFacts ReadFacts(Stream stream, out bool readFailed)
		{
			readFailed = false;
			ExecutableCandidateFacts executableCandidateFacts = new ExecutableCandidateFacts();
			try
			{
				if (stream == null || !stream.CanRead || !stream.CanSeek)
				{
					return executableCandidateFacts;
				}
				PeReader peReader = new PeReader();
				peReader.Stream = stream;
				peReader.Length = stream.Length;
				PeReader peReader2 = peReader;
				byte[] readResult = peReader2.Read(0L, 64);
				if (readResult == null || U16(readResult, 0) != 23117)
				{
					return executableCandidateFacts;
				}
				uint u32Result = U32(readResult, 60);
				if (u32Result < 64 || u32Result > 1048576)
				{
					return executableCandidateFacts;
				}
				byte[] readResult2 = peReader2.Read(u32Result, 24);
				if (readResult2 == null || U32(readResult2, 0) != 17744)
				{
					return executableCandidateFacts;
				}
				ushort u16Result = U16(readResult2, 6);
				ushort u16Result2 = U16(readResult2, 20);
				ushort u16Result3 = U16(readResult2, 22);
				if (u16Result == 0 || u16Result > 96 || u16Result2 > 4096 || (u16Result3 & 2) == 0 || (u16Result3 & 0x2000u) != 0)
				{
					return executableCandidateFacts;
				}
				byte[] readResult3 = peReader2.Read((long)u32Result + 24L, u16Result2);
				if (readResult3 == null || readResult3.Length < 96)
				{
					return executableCandidateFacts;
				}
				ushort u16Result4 = U16(readResult3, 0);
				int factsValue;
				switch (u16Result4)
				{
				case 267:
					factsValue = 96;
					break;
				case 523:
					factsValue = 112;
					break;
				default:
					return executableCandidateFacts;
				}
				if (readResult3.Length < factsValue)
				{
					return executableCandidateFacts;
				}
				uint u32Result2 = U32(readResult3, factsValue - 4);
				if (u32Result2 > (readResult3.Length - factsValue) / 8)
				{
					return executableCandidateFacts;
				}
				peReader2.HeaderSize = U32(readResult3, 60);
				peReader2.ImageBase = ((u16Result4 == 267) ? U32(readResult3, 28) : U64(readResult3, 24));
				long u16Result2Offset = (long)u32Result + 24L + u16Result2;
				for (int i = 0; i < u16Result; i++)
				{
					byte[] readResult4 = peReader2.Read(u16Result2Offset + i * 40, 40);
					if (readResult4 == null)
					{
						return executableCandidateFacts;
					}
					peReader2.Sections.Add(new Section
					{
						VirtualSize = U32(readResult4, 8),
						Address = U32(readResult4, 12),
						RawSize = U32(readResult4, 16),
						RawPointer = U32(readResult4, 20)
					});
				}
				bool graphics = false;
				if (u32Result2 > 1 && !ReadImports(peReader2, U32(readResult3, factsValue + 8), U32(readResult3, factsValue + 12), false, ref graphics))
				{
					return executableCandidateFacts;
				}
				if (u32Result2 > 13 && !ReadImports(peReader2, U32(readResult3, factsValue + 104), U32(readResult3, factsValue + 104 + 4), true, ref graphics))
				{
					return executableCandidateFacts;
				}
				executableCandidateFacts.Executable = true;
				executableCandidateFacts.Gui = U16(readResult3, 68) == 2;
				executableCandidateFacts.GraphicsImports = graphics;
				return executableCandidateFacts;
			}
			catch (IOException)
			{
				readFailed = true;
				return new ExecutableCandidateFacts();
			}
			catch (UnauthorizedAccessException)
			{
				readFailed = true;
				return new ExecutableCandidateFacts();
			}
			catch
			{
				return new ExecutableCandidateFacts();
			}
		}

		private static bool ReadImports(PeReader reader, uint address, uint size, bool delayed, ref bool graphics)
		{
			if (address == 0 && size == 0)
			{
				return true;
			}
			int importsValue = (delayed ? 32 : 20);
			if (address == 0 || size < importsValue || reader.FileOffset(address, size) < 0)
			{
				return false;
			}
			int upperBound = (int)Math.Min(256u, size / (uint)importsValue);
			for (int i = 0; i < upperBound; i++)
			{
				ulong addressOffset = (ulong)address + (ulong)(uint)(i * importsValue);
				if (addressOffset > uint.MaxValue)
				{
					return false;
				}
				byte[] readResult = reader.Read(reader.FileOffset((uint)addressOffset, importsValue), importsValue);
				if (readResult == null)
				{
					return false;
				}
				bool importsReady = true;
				byte[] rawBytes = readResult;
				for (int j = 0; j < rawBytes.Length; j++)
				{
					if (rawBytes[j] != 0)
					{
						importsReady = false;
						break;
					}
				}
				if (importsReady)
				{
					return true;
				}
				uint u32Result = U32(readResult, delayed ? 4 : 12);
				if (delayed && (U32(readResult, 0) & 1) == 0)
				{
					if (u32Result < reader.ImageBase || u32Result - reader.ImageBase > uint.MaxValue)
					{
						return false;
					}
					u32Result = (uint)(u32Result - reader.ImageBase);
				}
				string importNameResult = reader.ImportName(u32Result);
				if (importNameResult == null)
				{
					return false;
				}
				if (GraphicsDependency(importNameResult))
				{
					graphics = true;
				}
			}
			return false;
		}

		private static ushort U16(byte[] bytes, int at)
		{
			return BitConverter.ToUInt16(bytes, at);
		}

		private static uint U32(byte[] bytes, int at)
		{
			return BitConverter.ToUInt32(bytes, at);
		}

		private static ulong U64(byte[] bytes, int at)
		{
			return BitConverter.ToUInt64(bytes, at);
		}

		internal static string PickUnique(IList<ExecutableCandidateFacts> candidates, bool complete)
		{
			if (candidates == null || candidates.Count == 0 || candidates.Count > 400)
			{
				return null;
			}
			ExecutableCandidateFacts executableCandidateFacts = null;
			int pickUniqueValue = 0;
			bool pickUniqueReady = false;
			Dictionary<string, ExecutableCandidateFacts> dictionary = new Dictionary<string, ExecutableCandidateFacts>(StringComparer.OrdinalIgnoreCase);
			foreach (ExecutableCandidateFacts candidate in candidates)
			{
				if (candidate == null || !candidate.Executable || string.IsNullOrEmpty(candidate.Path))
				{
					continue;
				}
				ExecutableCandidateFacts value;
				if (dictionary.TryGetValue(candidate.Path, out value))
				{
					if (value.Gui != candidate.Gui || value.GraphicsImports != candidate.GraphicsImports || value.EngineDataPair != candidate.EngineDataPair)
					{
						return null;
					}
					continue;
				}
				dictionary.Add(candidate.Path, candidate);
				int pickUniqueValue2 = ((candidate.GraphicsImports || candidate.EngineDataPair) ? 2 : (candidate.Gui ? 1 : 0));
				if (pickUniqueValue2 != 0)
				{
					if (pickUniqueValue2 > pickUniqueValue)
					{
						executableCandidateFacts = candidate;
						pickUniqueValue = pickUniqueValue2;
						pickUniqueReady = false;
					}
					else if (pickUniqueValue2 == pickUniqueValue)
					{
						pickUniqueReady = true;
					}
				}
			}
			if (executableCandidateFacts != null && !pickUniqueReady && (pickUniqueValue >= 2 || (complete && dictionary.Count <= 1)))
			{
				return executableCandidateFacts.Path;
			}
			return null;
		}

		internal static string PickMainExecutable(string root)
		{
			bool complete;
			List<ExecutableCandidateFacts> list = CollectFacts(root, out complete);
			if (list != null)
			{
				return PickUnique(list, complete);
			}
			return null;
		}

		internal static int Rank(ExecutableCandidateFacts candidate)
		{
			if (candidate == null || !candidate.Executable)
			{
				return -1;
			}
			if (!candidate.GraphicsImports && !candidate.EngineDataPair)
			{
				if (!candidate.Gui)
				{
					return 0;
				}
				return 1;
			}
			return 2;
		}

		internal static List<ExecutableCandidateFacts> ListCandidates(string root, int max)
		{
			string recommended;
			return ListCandidates(root, max, out recommended);
		}

		internal static List<ExecutableCandidateFacts> ListCandidates(string root, int max, out string recommended)
		{
			bool complete;
			bool valid;
			List<ExecutableCandidateFacts> list = CollectFacts(root, true, out complete, out valid);
			recommended = ((valid && list != null) ? PickUnique(list, complete) : null);
			List<ExecutableCandidateFacts> list2 = new List<ExecutableCandidateFacts>();
			if (list == null)
			{
				return list2;
			}
			foreach (ExecutableCandidateFacts item in list)
			{
				if (item != null && item.Executable && !string.IsNullOrEmpty(item.Path))
				{
					list2.Add(item);
				}
			}
			list2.Sort(delegate(ExecutableCandidateFacts a, ExecutableCandidateFacts b)
			{
				int rankResult = Rank(a);
				int rankResult2 = Rank(b);
				return (rankResult != rankResult2) ? (rankResult2 - rankResult) : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
			});
			if (max > 0 && list2.Count > max)
			{
				list2.RemoveRange(max, list2.Count - max);
			}
			return list2;
		}

		internal static List<ExecutableCandidateFacts> CollectFacts(string root, out bool complete)
		{
			bool valid;
			return CollectFacts(root, false, out complete, out valid);
		}

		private static List<ExecutableCandidateFacts> CollectFacts(string root, bool keepReadable, out bool complete, out bool valid)
		{
			complete = false;
			valid = false;
			if (string.IsNullOrWhiteSpace(root))
			{
				return null;
			}
			try
			{
				root = Path.GetFullPath(root.Trim().Trim('"'));
				List<string> paths;
				valid = CollectPaths(root, (string directory) => Directory.EnumerateFiles(directory, "*.exe"), Directory.EnumerateDirectories, File.GetAttributes, out paths, out complete);
				if (!valid && !keepReadable)
				{
					return null;
				}
				List<ExecutableCandidateFacts> list = new List<ExecutableCandidateFacts>();
				foreach (string item in paths)
				{
					try
					{
						string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item);
						if (GameSessionDetector.ElectionVetoed(fileNameWithoutExtension, item))
						{
							continue;
						}
						FileInfo fileInfo = new FileInfo(item);
						long length = fileInfo.Length;
						DateTime lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
						ExecutableCandidateFacts executableCandidateFacts;
						bool readFailed;
						using (FileStream stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
						{
							executableCandidateFacts = ReadFacts(stream, out readFailed);
						}
						FileInfo fileInfo2 = new FileInfo(item);
						if (readFailed || fileInfo2.Length != length || fileInfo2.LastWriteTimeUtc != lastWriteTimeUtc)
						{
							valid = false;
							complete = false;
							if (!keepReadable)
							{
								return null;
							}
						}
						else
						{
							executableCandidateFacts.Path = item;
							string directoryName = Path.GetDirectoryName(item);
							executableCandidateFacts.EngineDataPair = executableCandidateFacts.Executable && File.Exists(Path.Combine(directoryName, "UnityPlayer.dll")) && Directory.Exists(Path.Combine(directoryName, fileNameWithoutExtension + "_Data"));
							list.Add(executableCandidateFacts);
						}
					}
					catch
					{
						valid = false;
						complete = false;
						if (!keepReadable)
						{
							return null;
						}
					}
				}
				return list;
			}
			catch
			{
				complete = false;
				valid = false;
				return null;
			}
		}

		internal static bool CollectPaths(string root, Func<string, IEnumerable<string>> files, Func<string, IEnumerable<string>> directories, Func<string, FileAttributes> attributes, out List<string> paths, out bool complete)
		{
			paths = new List<string>();
			complete = true;
			if (string.IsNullOrEmpty(root) || files == null || directories == null || attributes == null)
			{
				complete = false;
				return false;
			}
			bool result = true;
			try
			{
				Queue<DirectoryWork> queue = new Queue<DirectoryWork>();
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				int processedCount = 0;
				int processedCount2 = 1;
				hashSet.Add(root);
				queue.Enqueue(new DirectoryWork
				{
					Path = root,
					Depth = 0
				});
				while (queue.Count > 0)
				{
					DirectoryWork directoryWork = queue.Dequeue();
					try
					{
						if ((attributes(directoryWork.Path) & FileAttributes.ReparsePoint) != 0)
						{
							complete = false;
							result = false;
							continue;
						}
					}
					catch
					{
						complete = false;
						result = false;
						continue;
					}
					try
					{
						foreach (string item in files(directoryWork.Path))
						{
							if (processedCount >= 400)
							{
								complete = false;
								return result;
							}
							processedCount++;
							try
							{
								if ((attributes(item) & FileAttributes.ReparsePoint) != 0)
								{
									complete = false;
									result = false;
								}
								else
								{
									paths.Add(item);
								}
							}
							catch
							{
								complete = false;
								result = false;
							}
						}
					}
					catch
					{
						complete = false;
						result = false;
					}
					try
					{
						foreach (string item2 in directories(directoryWork.Path))
						{
							if (directoryWork.Depth >= 8 || processedCount2 >= 512)
							{
								complete = false;
								break;
							}
							processedCount2++;
							if (hashSet.Add(item2))
							{
								queue.Enqueue(new DirectoryWork
								{
									Path = item2,
									Depth = directoryWork.Depth + 1
								});
							}
						}
					}
					catch
					{
						complete = false;
						result = false;
					}
				}
				return result;
			}
			catch
			{
				complete = false;
				return false;
			}
		}
	}
}
