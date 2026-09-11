// @author bdth 2074055628@qq.com
// 文件用途 捐赠二维码的本地缓存 清单里的 donateId 没变就不碰 OSS 变了才重新拉一次
using System;
using System.Drawing;
using System.IO;

namespace PaviseApp
{
    internal static class DonateCache
    {
        private const string IdKey = "DonateImageId";
        public const int MaxImageBytes = 2 * 1024 * 1024;

        // 最近一次清单带回来的 启动检查和点公告都会更新 只是记着 不主动下载
        public static volatile DonateInfo Latest;

        // 清单还没带这两个字段时用这份 客户端不必等清单重发就能拉到码 清单一旦给了 id 以清单为准
        public static readonly DonateInfo Default = new DonateInfo
        {
            Id = "20260911-wechat",
            Url = "https://paivse.oss-cn-shanghai.aliyuncs.com/version/donate.png"
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

        // 清单没给 id 就无从判断 沿用缓存 有 id 而本地没图或 id 对不上才拉
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

        // 先解成图再收 网上拉回来的不是图片就不落盘 解出来的位图复制一份 流关了照样能画
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
