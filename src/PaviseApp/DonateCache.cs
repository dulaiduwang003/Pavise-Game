using System;
using System.Drawing;
using System.IO;

namespace PaviseApp
{
	internal static class DonateCache
	{
		private const string IdKey = "DonateImageId";

		public const int MaxImageBytes = 2097152;

		public static volatile DonateInfo Latest;

		public static readonly DonateInfo Default = new DonateInfo
		{
			Id = "20260911-wechat",
			Url = "https://pavise.club/assets/wechat.png"
		};

		public static DonateInfo Effective
		{
			get
			{
				return Latest ?? Default;
			}
		}

		public static string CachedId
		{
			get
			{
				return Settings.LoadStr("DonateImageId", "");
			}
		}

		public static string ImagePath
		{
			get
			{
				return Path.Combine(Paths.Data, "donate.png");
			}
		}

		public static bool HasImage
		{
			get
			{
				try
				{
					return CachedId.Length > 0 && File.Exists(ImagePath);
				}
				catch
				{
					return false;
				}
			}
		}

		public static bool NeedsRefresh(string cachedId, string manifestId, bool hasImage)
		{
			if (string.IsNullOrEmpty(manifestId))
			{
				return false;
			}
			if (hasImage)
			{
				return !string.Equals(cachedId, manifestId, StringComparison.Ordinal);
			}
			return true;
		}

		public static Bitmap LoadCached()
		{
			try
			{
				return Decode(File.ReadAllBytes(ImagePath));
			}
			catch
			{
				return null;
			}
		}

		public static Bitmap Decode(byte[] bytes)
		{
			if (bytes == null || bytes.Length == 0 || bytes.Length > 2097152)
			{
				return null;
			}
			try
			{
				using (MemoryStream stream = new MemoryStream(bytes))
				{
					using (Image original = Image.FromStream(stream))
					{
						return new Bitmap(original);
					}
				}
			}
			catch
			{
				return null;
			}
		}

		public static bool Store(byte[] bytes, string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return false;
			}
			Bitmap bitmap = Decode(bytes);
			if (bitmap == null)
			{
				return false;
			}
			bitmap.Dispose();
			try
			{
				string imagePathOffset = ImagePath + ".tmp";
				File.WriteAllBytes(imagePathOffset, bytes);
				if (File.Exists(ImagePath))
				{
					File.Delete(ImagePath);
				}
				File.Move(imagePathOffset, ImagePath);
				Settings.SaveStr("DonateImageId", id);
				return true;
			}
			catch
			{
				return false;
			}
		}
	}
}
