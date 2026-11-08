using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PaviseApp
{
	internal static class IcoWriter
	{
		public static byte[] Build(int[] sizes)
		{
			return Build(sizes, PerformancePreset.Standard, true);
		}

		public static byte[] Build(int[] sizes, PerformancePreset mode, bool enabled)
		{
			List<byte[]> list = new List<byte[]>();
			foreach (int px in sizes)
			{
				using (Bitmap bitmap = IconArt.Render(px, mode, enabled))
				{
					using (MemoryStream memoryStream = new MemoryStream())
					{
						bitmap.Save(memoryStream, ImageFormat.Png);
						list.Add(memoryStream.ToArray());
					}
				}
			}
			using (MemoryStream memoryStream2 = new MemoryStream())
			{
				using (BinaryWriter binaryWriter = new BinaryWriter(memoryStream2))
				{
					binaryWriter.Write((short)0);
					binaryWriter.Write((short)1);
					binaryWriter.Write((short)list.Count);
					int value = 6 + 16 * list.Count;
					for (int j = 0; j < list.Count; j++)
					{
						int value2 = sizes[j];
						binaryWriter.Write((byte)((value2 < 256) ? ((uint)value2) : 0u));
						binaryWriter.Write((byte)((value2 < 256) ? ((uint)value2) : 0u));
						binaryWriter.Write((byte)0);
						binaryWriter.Write((byte)0);
						binaryWriter.Write((short)1);
						binaryWriter.Write((short)32);
						binaryWriter.Write(list[j].Length);
						binaryWriter.Write(value);
						value += list[j].Length;
					}
					foreach (byte[] item in list)
					{
						binaryWriter.Write(item);
					}
					binaryWriter.Flush();
					return memoryStream2.ToArray();
				}
			}
		}

		public static void Save(string path, int[] sizes)
		{
			File.WriteAllBytes(path, Build(sizes));
		}
	}
}
