// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.IO;

namespace PaviseApp
{
    internal static class DonateCache
    {
        private const string IdKey = "DonateImageId";
        public const int MaxImageBytes = 2 * 1024 * 1024;

        public static volatile DonateInfo Latest;

        public static readonly DonateInfo Default = new DonateInfo
        {
            Id = "20260911-wechat",
            Url = App.WebsiteUrl + "assets/wechat.png"
        };

        public static DonateInfo Effective { get { return Latest ?? Default; } }

        public static string CachedId { get { return Settings.LoadStr(IdKey, ""); } }
        public static string ImagePath { get { return Path.Combine(Paths.Data, "donate.png"); } }

        public static bool HasImage
        {
            get
            {
                try { return CachedId.Length > 0 && File.Exists(ImagePath); }
                catch { return false; }
            }
        }

        public static bool NeedsRefresh(string cachedId, string manifestId, bool hasImage)
        {
            if (string.IsNullOrEmpty(manifestId)) return false;
            return !hasImage || !string.Equals(cachedId, manifestId, StringComparison.Ordinal);
        }

        public static Bitmap LoadCached()
        {
            try { return Decode(File.ReadAllBytes(ImagePath)); }
            catch { return null; }
        }

        // 确认图片可解码后才写缓存。
        public static Bitmap Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        public static bool Store(byte[] bytes, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            Bitmap probe = Decode(bytes);
            if (probe == null) return false;
            probe.Dispose();
            try
            {
                string tmp = ImagePath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(ImagePath)) File.Delete(ImagePath);
                File.Move(tmp, ImagePath);
                Settings.SaveStr(IdKey, id);
                return true;
            }
            catch { return false; }
        }
    }
}
