// @author bdth 2074055628@qq.com
// File purpose Local cache of the donate QR code, untouched while the manifest's donateId is unchanged, refetched from the official website only when it changes
using System;
using System.Drawing;
using System.IO;

namespace PaviseApp
{
    internal static class DonateCache
    {
        private const string IdKey = "DonateImageId";
        public const int MaxImageBytes = 2 * 1024 * 1024;

        // Latest one brought back by the manifest, updated by the startup check and by opening Donate, only remembered, never downloaded proactively
        public static volatile DonateInfo Latest;

        // Used while the manifest lacks these two fields, the client can fetch the code without waiting for a manifest re-issue, once the manifest supplies an id the manifest wins
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

        // Without an id from the manifest there is nothing to judge, keep the cache, fetch only when an id exists and the local image is missing or the id mismatches
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

        // Decode into an image before accepting, do not persist a non-image pulled from the network, copy the decoded bitmap so it still paints after the stream closes
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
