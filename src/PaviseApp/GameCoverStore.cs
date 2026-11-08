using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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

		private static readonly object Gate = new object();

		private static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

		public static Bitmap Load(GameProfile profile)
		{
			string coverPathResult = CoverPath(profile);
			if (coverPathResult == null)
			{
				return null;
			}
			lock (Gate)
			{
				Bitmap value;
				if (Cache.TryGetValue(coverPathResult, out value))
				{
					return value;
				}
				try
				{
					if (File.Exists(coverPathResult))
					{
						value = ReadCover(coverPathResult);
					}
				}
				catch (Exception error)
				{
					if (!IsFileOrImageError(error))
					{
						throw;
					}
					value = null;
				}
				Cache[coverPathResult] = value;
				return value;
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
					Invalidate(coverPathResult);
					Cache[coverPathResult] = bitmap;
					bitmap = null;
				}
				return true;
			}
			catch (Exception ex)
			{
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
				foreach (Bitmap value in Cache.Values)
				{
					if (value != null)
					{
						value.Dispose();
					}
				}
				Cache.Clear();
			}
		}

		private static void Invalidate(string path)
		{
			Bitmap value;
			if (Cache.TryGetValue(path, out value))
			{
				Cache.Remove(path);
				if (value != null)
				{
					value.Dispose();
				}
			}
		}

		private static string CoverPath(GameProfile profile)
		{
			if (profile == null || string.IsNullOrEmpty(profile.Id) || string.IsNullOrEmpty(Paths.Data))
			{
				return null;
			}
			byte[] hash;
			using (SHA256 sHA = SHA256.Create())
			{
				hash = sHA.ComputeHash(Encoding.UTF8.GetBytes(profile.Id));
			}
			StringBuilder stringBuilder = new StringBuilder(64);
			byte[] rawBytes = hash;
			foreach (byte b in rawBytes)
			{
				stringBuilder.Append(b.ToString("x2"));
			}
			return Path.Combine(Paths.Data, "GameCovers", stringBuilder.ToString() + ".png");
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
