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
            List<byte[]> toArraies = new List<byte[]>();
            foreach (int px in sizes)
            {
                using (Bitmap bitmap = IconArt.Render(px, mode, enabled))
                {
                    using (MemoryStream memoryStream = new MemoryStream())
                    {
                        bitmap.Save(memoryStream, ImageFormat.Png);
                        toArraies.Add(memoryStream.ToArray());
                    }
                }
            }
            using (MemoryStream currentMemoryStream = new MemoryStream())
            {
                using (BinaryWriter binaryWriter = new BinaryWriter(currentMemoryStream))
                {
                    binaryWriter.Write((short)0);
                    binaryWriter.Write((short)1);
                    binaryWriter.Write((short)toArraies.Count);
                    int imageDataOffset = 6 + 16 * toArraies.Count;
                    for (int j = 0; j < toArraies.Count; j++)
                    {
                        int size = sizes[j];
                        binaryWriter.Write((byte)((size < 256) ? ((uint)size) : 0u));
                        binaryWriter.Write((byte)((size < 256) ? ((uint)size) : 0u));
                        binaryWriter.Write((byte)0);
                        binaryWriter.Write((byte)0);
                        binaryWriter.Write((short)1);
                        binaryWriter.Write((short)32);
                        binaryWriter.Write(toArraies[j].Length);
                        binaryWriter.Write(imageDataOffset);
                        imageDataOffset += toArraies[j].Length;
                    }
                    foreach (byte[] toArray in toArraies)
                    {
                        binaryWriter.Write(toArray);
                    }
                    binaryWriter.Flush();
                    return currentMemoryStream.ToArray();
                }
            }
        }

        public static void Save(string path, int[] sizes)
        {
            File.WriteAllBytes(path, Build(sizes));
        }
    }
}
