using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal static class GameCoverStore
    {
        internal const long MaximumSourceBytes = 20971520L;

        internal const long MaximumSourcePixels = 24000000L;

        internal const int MaximumSourceDimension = 8192;

        internal const int CoverWidth = 960;

        internal const int CoverHeight = 540;

        internal const int MaximumCachedCovers = 24;

        private static readonly object Gate = new object();

        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly LinkedList<string> Recency = new LinkedList<string>();

        private sealed class CachedCoverPath
        {
            internal string Id, DataRoot, Path;
        }

        // 游戏库刷新会替换配置实例；弱引用缓存不能延长旧配置及其派生路径的生命周期。
        private static readonly ConditionalWeakTable<GameProfile, CachedCoverPath> CoverPaths = new ConditionalWeakTable<GameProfile, CachedCoverPath>();
        private static readonly ConditionalWeakTable<GameProfile, CachedCoverPath>.CreateValueCallback CreateCoverPath = delegate { return new CachedCoverPath(); };

        internal sealed class CacheEntry
        {
            internal Bitmap Image;
            internal LinkedListNode<string> Position;
            internal int Borrowers;
            internal bool Cached;
        }

        // 缓存淘汰与绘制结束可以并发；只在缓存和最后一个绘制借用均已释放后销毁图片。
        internal sealed class Lease : IDisposable
        {
            private CacheEntry entry;

            internal Lease(CacheEntry entry) { this.entry = entry; }

            internal Bitmap Image
            {
                get { lock (Gate) return entry == null ? null : entry.Image; }
            }

            public void Dispose()
            {
                lock (Gate)
                {
                    if (entry == null) return;
                    CacheEntry previous = entry;
                    entry = null;
                    previous.Borrowers--;
                    DisposeUnowned(previous);
                }
            }
        }

        public static Lease Acquire(GameProfile profile)
        {
            string coverPathResult = CoverPath(profile);
            if (coverPathResult == null)
            {
                return null;
            }
            lock (Gate)
            {
                CacheEntry entry;
                if (Cache.TryGetValue(coverPathResult, out entry))
                {
                    Recency.Remove(entry.Position);
                    Recency.AddFirst(entry.Position);
                }
                else
                {
                    Bitmap value = null;
                    try
                    {
                        try
                        {
                            if (File.Exists(coverPathResult)) value = ReadCover(coverPathResult);
                        }
                        catch (Exception error)
                        {
                            if (!IsFileOrImageError(error)) throw;
                        }
                        entry = AddCached(coverPathResult, value);
                        value = null;
                    }
                    finally
                    {
                        if (value != null) value.Dispose();
                    }
                }
                if (entry.Image == null) return null;
                var lease = new Lease(entry);
                entry.Borrowers++;
                return lease;
            }
        }

        public static bool HasCustom(GameProfile profile)
        {
            string coverPathResult = CoverPath(profile);
            if (coverPathResult != null)
            {
                return File.Exists(coverPathResult);
            }
            return false;
        }

        public static bool TrySet(GameProfile profile, string sourceFile, out string error)
        {
            error = null;
            string coverPathResult = CoverPath(profile);
            if (coverPathResult == null)
            {
                error = "游戏封面存储目录不可用。";
                return false;
            }
            Bitmap bitmap = null;
            string combinedPath = null;
            bool persisted = false;
            try
            {
                bitmap = ReadCover(sourceFile);
                lock (Gate)
                {
                    string directoryName = Path.GetDirectoryName(coverPathResult);
                    Directory.CreateDirectory(directoryName);
                    combinedPath = Path.Combine(directoryName, "." + Guid.NewGuid().ToString("N") + ".tmp");
                    using (FileStream fileStream = new FileStream(combinedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        bitmap.Save(fileStream, ImageFormat.Png);
                        fileStream.Flush(true);
                    }
                    if (File.Exists(coverPathResult))
                    {
                        File.Replace(combinedPath, coverPathResult, null);
                    }
                    else
                    {
                        File.Move(combinedPath, coverPathResult);
                    }
                    combinedPath = null;
                    persisted = true;
                    Invalidate(coverPathResult);
                    AddCached(coverPathResult, bitmap);
                    bitmap = null;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (persisted)
                {
                    // 文件已经提交，缓存不足不改变保存结果；未移交图片由 finally 释放，下次借图重新读取。
                    if (ex is OutOfMemoryException || ex is ExternalException) return true;
                    throw;
                }
                if (!IsFileOrImageError(ex))
                {
                    throw;
                }
                error = ((ex is InvalidDataException) ? ex.Message : "无法保存封面，请选择有效的 PNG、JPG 或 BMP 图片。");
                return false;
            }
            finally
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                }
                if (combinedPath != null)
                {
                    try
                    {
                        File.Delete(combinedPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        public static bool TryReset(GameProfile profile, out string error)
        {
            error = null;
            string coverPathResult = CoverPath(profile);
            if (coverPathResult == null)
            {
                return true;
            }
            lock (Gate)
            {
                try
                {
                    File.Delete(coverPathResult);
                    Invalidate(coverPathResult);
                    return true;
                }
                catch (Exception error2)
                {
                    if (!IsFileOrImageError(error2))
                    {
                        throw;
                    }
                    error = "无法恢复默认封面，请稍后重试。";
                    return false;
                }
            }
        }

        public static void Reset(GameProfile profile)
        {
            string error;
            if (!TryReset(profile, out error))
            {
                throw new IOException(error);
            }
        }

        public static void DropCache(GameProfile profile)
        {
            string coverPathResult = CoverPath(profile);
            if (coverPathResult == null)
            {
                return;
            }
            lock (Gate)
            {
                Invalidate(coverPathResult);
            }
        }

        public static void DropCache()
        {
            lock (Gate)
            {
                foreach (CacheEntry entry in Cache.Values)
                {
                    entry.Cached = false;
                    entry.Position = null;
                    DisposeUnowned(entry);
                }
                Cache.Clear();
                Recency.Clear();
            }
        }

        private static void Invalidate(string path)
        {
            CacheEntry entry;
            if (Cache.TryGetValue(path, out entry))
            {
                Cache.Remove(path);
                Recency.Remove(entry.Position);
                entry.Position = null;
                entry.Cached = false;
                DisposeUnowned(entry);
            }
        }

        private static CacheEntry AddCached(string path, Bitmap image)
        {
            var entry = new CacheEntry { Image = image };
            try
            {
                entry.Position = Recency.AddFirst(path);
                Cache.Add(path, entry);
                entry.Cached = true;
                // 不存在或损坏的封面也占一个条目；异常遗留节点必须逐个剥离，避免持锁空转。
                while (Cache.Count > MaximumCachedCovers)
                {
                    LinkedListNode<string> oldest = Recency.Last;
                    if (oldest == null) throw new InvalidOperationException("封面缓存缺少淘汰索引。");
                    CacheEntry candidate;
                    if (!Cache.TryGetValue(oldest.Value, out candidate) || !object.ReferenceEquals(candidate.Position, oldest))
                    {
                        Recency.Remove(oldest);
                        continue;
                    }
                    Invalidate(oldest.Value);
                }
                return entry;
            }
            catch
            {
                CacheEntry published;
                if (Cache.TryGetValue(path, out published) && object.ReferenceEquals(published, entry)) Cache.Remove(path);
                if (entry.Position != null && object.ReferenceEquals(entry.Position.List, Recency)) Recency.Remove(entry.Position);
                entry.Position = null;
                entry.Cached = false;
                // 失败不转移传入图片的所有权，调用方负责释放；已经借出的其他图片保持原租约。
                entry.Image = null;
                throw;
            }
        }

        private static void DisposeUnowned(CacheEntry entry)
        {
            if (!entry.Cached && entry.Borrowers == 0 && entry.Image != null)
            {
                entry.Image.Dispose();
                entry.Image = null;
            }
        }

        private static string CoverPath(GameProfile profile)
        {
            if (profile == null) return null;
            string id = profile.Id;
            string dataRoot = Paths.Data;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(dataRoot))
            {
                return null;
            }
            CachedCoverPath cached = CoverPaths.GetValue(profile, CreateCoverPath);
            lock (cached)
            {
                if (string.Equals(cached.Id, id, StringComparison.Ordinal) && string.Equals(cached.DataRoot, dataRoot, StringComparison.Ordinal))
                    return cached.Path;
                byte[] hash;
                using (SHA256 sHA = SHA256.Create())
                {
                    hash = sHA.ComputeHash(Encoding.UTF8.GetBytes(id));
                }
                StringBuilder stringBuilder = new StringBuilder(64);
                foreach (byte b in hash) stringBuilder.Append(b.ToString("x2"));
                string path = Path.Combine(dataRoot, "GameCovers", stringBuilder.ToString() + ".png");
                cached.Id = id;
                cached.DataRoot = dataRoot;
                cached.Path = path;
                return path;
            }
        }

        private static Bitmap ReadCover(string path)
        {
            byte[] rawBytes;
            using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fileStream.Length < 10)
                {
                    throw new InvalidDataException("封面图片不完整。");
                }
                if (fileStream.Length > 20971520)
                {
                    throw new InvalidDataException("封面图片大小不得超过 20 MB。");
                }
                rawBytes = new byte[(int)fileStream.Length];
                int coverValue;
                for (int i = 0; i < rawBytes.Length; i += coverValue)
                {
                    coverValue = fileStream.Read(rawBytes, i, rawBytes.Length - i);
                    if (coverValue == 0)
                    {
                        throw new InvalidDataException("封面图片不完整。");
                    }
                }
            }
            Size size = ReadDimensions(rawBytes);
            if (size.Width <= 0 || size.Height <= 0 || size.Width > 8192 || size.Height > 8192 || (long)size.Width * (long)size.Height > 24000000)
            {
                throw new InvalidDataException("封面图片尺寸过大，单边最多 8192 像素，总像素最多 2400 万。");
            }
            using (MemoryStream stream = new MemoryStream(rawBytes, false))
            {
                using (Image image = Image.FromStream(stream, false, true))
                {
                    if (image.Width != size.Width || image.Height != size.Height)
                    {
                        throw new InvalidDataException("封面图片尺寸信息无效。");
                    }
                    Bitmap bitmap = new Bitmap(960, 540, PixelFormat.Format32bppPArgb);
                    try
                    {
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                        {
                            using (ImageAttributes imageAttributes = new ImageAttributes())
                            {
                                graphics.Clear(Color.FromArgb(12, 14, 18));
                                graphics.CompositingQuality = CompositingQuality.HighQuality;
                                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                imageAttributes.SetWrapMode(WrapMode.TileFlipXY);
                                float lowerBound = Math.Max(960f / (float)size.Width, 540f / (float)size.Height);
                                float lowerBoundRatio = 960f / lowerBound;
                                float lowerBoundRatio2 = 540f / lowerBound;
                                graphics.DrawImage(image, new Rectangle(0, 0, 960, 540), ((float)size.Width - lowerBoundRatio) / 2f, ((float)size.Height - lowerBoundRatio2) / 2f, lowerBoundRatio, lowerBoundRatio2, GraphicsUnit.Pixel, imageAttributes);
                            }
                        }
                        return bitmap;
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                }
            }
        }

        private static Size ReadDimensions(byte[] bytes)
        {
            if (bytes.Length >= 33 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10 && ReadBigEndian(bytes, 8) == 13 && bytes[12] == 73 && bytes[13] == 72 && bytes[14] == 68 && bytes[15] == 82)
            {
                return new Size(ReadBigEndian(bytes, 16), ReadBigEndian(bytes, 20));
            }
            if (bytes.Length >= 54 && bytes[0] == 66 && bytes[1] == 77 && BitConverter.ToUInt32(bytes, 14) >= 40)
            {
                int toInt32Result = BitConverter.ToInt32(bytes, 22);
                return new Size(BitConverter.ToInt32(bytes, 18), (toInt32Result != int.MinValue) ? Math.Abs(toInt32Result) : 0);
            }
            if (bytes[0] == byte.MaxValue && bytes[1] == 216)
            {
                int i = 2;
                while (i < bytes.Length && bytes[i++] == byte.MaxValue)
                {
                    for (; i < bytes.Length && bytes[i] == byte.MaxValue; i++)
                    {
                    }
                    if (i >= bytes.Length)
                    {
                        break;
                    }
                    int dimensionsValue = bytes[i++];
                    switch (dimensionsValue)
                    {
                    default:
                    {
                        if (i + 2 > bytes.Length)
                        {
                            break;
                        }
                        int dimensionsValue2 = bytes[i] * 256 + bytes[i + 1];
                        if (dimensionsValue2 >= 2 && i + dimensionsValue2 <= bytes.Length)
                        {
                            if (dimensionsValue >= 192 && dimensionsValue <= 207 && dimensionsValue != 196 && dimensionsValue != 200 && dimensionsValue != 204 && dimensionsValue2 >= 8)
                            {
                                return new Size(bytes[i + 5] * 256 + bytes[i + 6], bytes[i + 3] * 256 + bytes[i + 4]);
                            }
                            i += dimensionsValue2;
                            continue;
                        }
                        break;
                    }
                    case 1:
                    case 208:
                    case 209:
                    case 210:
                    case 211:
                    case 212:
                    case 213:
                    case 214:
                    case 215:
                        continue;
                    case 217:
                    case 218:
                        break;
                    }
                    break;
                }
            }
            throw new InvalidDataException("请选择有效的 PNG、JPG 或 BMP 封面图片。");
        }

        private static int ReadBigEndian(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static bool IsFileOrImageError(Exception error)
        {
            if (!(error is IOException) && !(error is InvalidDataException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is NotSupportedException) && !(error is ExternalException) && !(error is OutOfMemoryException))
            {
                return error is SecurityException;
            }
            return true;
        }
    }
}
