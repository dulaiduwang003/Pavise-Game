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
				byte[] array = new byte[length];
				Stream.Position = offset;
				int num;
				for (int i = 0; i < length; i += num)
				{
					num = Stream.Read(array, i, length - i);
					if (num <= 0)
					{
						return null;
					}
				}
				return array;
			}

			internal long FileOffset(uint rva, long length)
			{
				if (length <= 0)
				{
					return -1L;
				}
				ulong num = (ulong)(rva + length);
				if (num > 4294967296L)
				{
					return -1L;
				}
				long num2 = -1L;
				if (rva < HeaderSize && num <= HeaderSize && num <= (ulong)Length)
				{
					num2 = rva;
				}
				foreach (Section section in Sections)
				{
					if (rva < section.Address)
					{
						continue;
					}
					ulong num3 = (ulong)rva - (ulong)section.Address;
					if (num3 >= Math.Max(section.RawSize, section.VirtualSize) || (ulong)((long)num3 + length) > (ulong)section.RawSize)
					{
						continue;
					}
					ulong num4 = section.RawPointer + num3;
					if ((ulong)((long)num4 + length) <= (ulong)Length)
					{
						if (num2 >= 0)
						{
							return -1L;
						}
						num2 = (long)num4;
					}
				}
				return num2;
			}

			internal string ImportName(uint rva)
			{
				StringBuilder stringBuilder = new StringBuilder();
				while (stringBuilder.Length < 260)
				{
					ulong num = (ulong)rva + (ulong)(uint)stringBuilder.Length;
					if (num > uint.MaxValue)
					{
						return null;
					}
					int num2 = 260 - stringBuilder.Length;
					long num3 = FileOffset((uint)num, num2);
					while (num3 < 0 && num2 > 1)
					{
						num2 /= 2;
						num3 = FileOffset((uint)num, num2);
					}
					byte[] array = Read(num3, num2);
					if (array == null)
					{
						return null;
					}
					byte[] array2 = array;
					foreach (byte b in array2)
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
				byte[] array = peReader2.Read(0L, 64);
				if (array == null || U16(array, 0) != 23117)
				{
					return executableCandidateFacts;
				}
				uint num = U32(array, 60);
				if (num < 64 || num > 1048576)
				{
					return executableCandidateFacts;
				}
				byte[] array2 = peReader2.Read(num, 24);
				if (array2 == null || U32(array2, 0) != 17744)
				{
					return executableCandidateFacts;
				}
				ushort num2 = U16(array2, 6);
				ushort num3 = U16(array2, 20);
				ushort num4 = U16(array2, 22);
				if (num2 == 0 || num2 > 96 || num3 > 4096 || (num4 & 2) == 0 || (num4 & 0x2000u) != 0)
				{
					return executableCandidateFacts;
				}
				byte[] array3 = peReader2.Read((long)num + 24L, num3);
				if (array3 == null || array3.Length < 96)
				{
					return executableCandidateFacts;
				}
				ushort num5 = U16(array3, 0);
				int num6;
				switch (num5)
				{
				case 267:
					num6 = 96;
					break;
				case 523:
					num6 = 112;
					break;
				default:
					return executableCandidateFacts;
				}
				if (array3.Length < num6)
				{
					return executableCandidateFacts;
				}
				uint num7 = U32(array3, num6 - 4);
				if (num7 > (array3.Length - num6) / 8)
				{
					return executableCandidateFacts;
				}
				peReader2.HeaderSize = U32(array3, 60);
				peReader2.ImageBase = ((num5 == 267) ? U32(array3, 28) : U64(array3, 24));
				long num8 = (long)num + 24L + num3;
				for (int i = 0; i < num2; i++)
				{
					byte[] array4 = peReader2.Read(num8 + i * 40, 40);
					if (array4 == null)
					{
						return executableCandidateFacts;
					}
					peReader2.Sections.Add(new Section
					{
						VirtualSize = U32(array4, 8),
						Address = U32(array4, 12),
						RawSize = U32(array4, 16),
						RawPointer = U32(array4, 20)
					});
				}
				bool graphics = false;
				if (num7 > 1 && !ReadImports(peReader2, U32(array3, num6 + 8), U32(array3, num6 + 12), false, ref graphics))
				{
					return executableCandidateFacts;
				}
				if (num7 > 13 && !ReadImports(peReader2, U32(array3, num6 + 104), U32(array3, num6 + 104 + 4), true, ref graphics))
				{
					return executableCandidateFacts;
				}
				executableCandidateFacts.Executable = true;
				executableCandidateFacts.Gui = U16(array3, 68) == 2;
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
			int num = (delayed ? 32 : 20);
			if (address == 0 || size < num || reader.FileOffset(address, size) < 0)
			{
				return false;
			}
			int num2 = (int)Math.Min(256u, size / (uint)num);
			for (int i = 0; i < num2; i++)
			{
				ulong num3 = (ulong)address + (ulong)(uint)(i * num);
				if (num3 > uint.MaxValue)
				{
					return false;
				}
				byte[] array = reader.Read(reader.FileOffset((uint)num3, num), num);
				if (array == null)
				{
					return false;
				}
				bool flag = true;
				byte[] array2 = array;
				for (int j = 0; j < array2.Length; j++)
				{
					if (array2[j] != 0)
					{
						flag = false;
						break;
					}
				}
				if (flag)
				{
					return true;
				}
				uint num4 = U32(array, delayed ? 4 : 12);
				if (delayed && (U32(array, 0) & 1) == 0)
				{
					if (num4 < reader.ImageBase || num4 - reader.ImageBase > uint.MaxValue)
					{
						return false;
					}
					num4 = (uint)(num4 - reader.ImageBase);
				}
				string text = reader.ImportName(num4);
				if (text == null)
				{
					return false;
				}
				if (GraphicsDependency(text))
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
			int num = 0;
			bool flag = false;
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
				int num2 = ((candidate.GraphicsImports || candidate.EngineDataPair) ? 2 : (candidate.Gui ? 1 : 0));
				if (num2 != 0)
				{
					if (num2 > num)
					{
						executableCandidateFacts = candidate;
						num = num2;
						flag = false;
					}
					else if (num2 == num)
					{
						flag = true;
					}
				}
			}
			if (executableCandidateFacts != null && !flag && (num >= 2 || (complete && dictionary.Count <= 1)))
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
				int num = Rank(a);
				int num2 = Rank(b);
				return (num != num2) ? (num2 - num) : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
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
				int num = 0;
				int num2 = 1;
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
							if (num >= 400)
							{
								complete = false;
								return result;
							}
							num++;
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
							if (directoryWork.Depth >= 8 || num2 >= 512)
							{
								complete = false;
								break;
							}
							num2++;
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
