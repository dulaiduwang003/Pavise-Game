using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal sealed class SelfUpdatePackage
    {
        internal string Version, Sha256, ManifestText;
        internal long Size;
        internal string[] DownloadUrls;
    }

    internal sealed class SelfUpdateProgress
    {
        internal string Stage;
        internal long Received, Total;
    }

    // The signature covers the decoded payload bytes exactly. JSON formatting is not normalized.
    internal static class SelfUpdateManifest
    {
        internal const int UpdaterProtocol = 1;
        internal const int MaxManifestBytes = 65536;
        internal const long MaxExecutableBytes = 134217728;
        internal const string OfficialHost = "pavise.club";
        internal const string FallbackHost = "pavise-website.2074055628.workers.dev";
        internal const string ReleaseRepository = "dulaiduwang003/Pavise-Game";
        internal const string TrustedPublicKeyXml = "<RSAKeyValue><Modulus>2eecXkt7H/peJYi+xVPhb/RO0zTRL//AADq2621Xhx6+I5Ydna2gz4djuNUbPaXjqMvuQwQoRBgRlSfqhwGE0AdajQsSDx+dUxG/qsQ2QLYK3s8O+aoAJwuEeH9ecaDfmiqmtNt6iKmgwe76NAQxkd5E2KQUZopqgc/wcbx73Rc8HVUb1CoDPWjBJla4DS0zflvOSiR+TOmwH8/QByHsFq3yGHg1htleYVnm3DGEcS89k7RBIVm/p5v7Ep6EwUtdvjfAjWvGRPEMcYxCvcs9+jtMoUaGIgx2v1twVBbq/AArvlJuIW6Tik8LPllmobjufBO13DUTooxz6PJuiBG0fvkUfj3E6WE+SD2J6C4hEWa/puZaLt5Hrq7CmNQL/a+/qiBfc7nLfnJo4XI4XW5e80rAQ01DX4bUB9WYjko1Q+iOI7k2kLwaCg2L88A7miCdOxx4cuaVap/lBhwzeFqs4LGIMPGNqHeoCWGbXnL48J3K+s+HFIqzDz3AYdqUG1R1</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

#if PAVISE_SELFTEST
        // Isolated fixtures use an ephemeral signing key. This hook is absent from release builds.
        internal static string FixturePublicKeyXml;
#endif

        internal static SelfUpdatePackage ParseAndVerify(string manifestText)
        {
#if PAVISE_SELFTEST
            if (FixturePublicKeyXml != null) return ParseAndVerify(manifestText, FixturePublicKeyXml, DateTime.UtcNow);
#endif
            return ParseAndVerify(manifestText, TrustedPublicKeyXml, DateTime.UtcNow);
        }

#if PAVISE_SELFTEST
        internal
#else
        private
#endif
        static SelfUpdatePackage ParseAndVerify(string manifestText, string publicKey, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(manifestText) || Encoding.UTF8.GetByteCount(manifestText) > MaxManifestBytes)
                throw Invalid("更新清单为空或超过大小限制。");
            Dictionary<string, object> envelope = JsonObject(manifestText);
            RequireFields(envelope, new string[] { "payload", "signature" });
            byte[] payload = Base64(Text(envelope, "payload"), "更新清单载荷编码无效。");
            byte[] signature = Base64(Text(envelope, "signature"), "更新清单签名编码无效。");
            if (payload.Length == 0 || payload.Length > MaxManifestBytes / 2 || signature.Length != 384)
                throw Invalid("更新清单载荷或签名长度无效。");
            try
            {
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKey);
                    if (rsa.KeySize != 3072 || !rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                        throw Invalid("更新清单签名校验失败，请使用官方备用下载。");
                }
            }
            catch (CryptographicException) { throw Invalid("更新清单签名校验失败，请使用官方备用下载。"); }
            string payloadText;
            try { payloadText = new UTF8Encoding(false, true).GetString(payload); }
            catch (DecoderFallbackException) { throw Invalid("更新清单不是有效的 UTF-8。"); }
            Dictionary<string, object> values = JsonObject(payloadText);
            RequireFields(values, new string[] { "schema", "version", "size", "sha256", "minUpdater", "expiresUtc", "urls" });
            if (Number(values, "schema") != 1) throw Invalid("不支持此更新清单格式。");
            long minUpdater = Number(values, "minUpdater");
            if (minUpdater < 1 || minUpdater > UpdaterProtocol) throw Invalid("当前更新器无法安装此版本，请使用官方备用下载。");
            string version = Text(values, "version");
            if (!IsCanonicalVersion(version)) throw Invalid("更新版本号无效。");
            long size = Number(values, "size");
            if (size < 1 || size > MaxExecutableBytes) throw Invalid("更新程序大小无效或超过限制。");
            string hash = Text(values, "sha256");
            if (!IsHash(hash)) throw Invalid("更新程序校验值无效。");
            DateTime expires;
            if (!DateTime.TryParseExact(Text(values, "expiresUtc"), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out expires) || expires <= utcNow.ToUniversalTime())
                throw Invalid("更新清单已过期或有效期无效，请稍后重试或使用官方备用下载。");
            List<object> entries = values["urls"] as List<object>;
            if (entries == null || entries.Count < 1 || entries.Count > 6) throw Invalid("更新下载地址数量无效。");
            List<string> urls = new List<string>();
            foreach (object entry in entries)
            {
                string url = entry as string;
                if (!IsAllowedPackageUrl(url, version) || urls.Contains(url)) throw Invalid("更新下载地址无效或不受信任。");
                urls.Add(url);
            }
            return new SelfUpdatePackage { Version = version, Size = size, Sha256 = hash, DownloadUrls = urls.ToArray(), ManifestText = manifestText };
        }

        internal static bool IsCanonicalVersion(string value)
        {
            Version version;
            return value != null && System.Version.TryParse(value, out version) && version.Revision >= 0 && version.ToString(4) == value;
        }

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        internal static bool IsHttpsUrl(string value, out Uri uri)
        {
            uri = null;
            if (value == null || value != value.Trim()) return false;
            foreach (char c in value) if (char.IsControl(c) || c == ' ') return false;
            return value.Length <= 4096 && Uri.TryCreate(value, UriKind.Absolute, out uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Fragment) && !uri.Host.EndsWith(".", StringComparison.Ordinal);
        }

        internal static bool IsAllowedPackageUrl(string value, string version)
        {
            Uri uri;
            if (!IsCanonicalVersion(version) || !IsHttpsUrl(value, out uri) || !string.IsNullOrEmpty(uri.Query)) return false;
            if (uri.Host == OfficialHost || uri.Host == FallbackHost)
                return uri.AbsolutePath == "/updates/" + version + "/Pavise.exe";
            return uri.Host == "github.com" && uri.AbsolutePath == "/" + ReleaseRepository + "/releases/download/v" + version + "/Pavise.exe";
        }

        internal static bool IsAllowedRedirect(Uri initial, Uri next)
        {
            Uri checkedUri;
            if (initial == null || next == null || !IsHttpsUrl(next.AbsoluteUri, out checkedUri)) return false;
            if (initial.Host == OfficialHost || initial.Host == FallbackHost)
                return (next.Host == OfficialHost || next.Host == FallbackHost) && next.AbsolutePath == initial.AbsolutePath && string.IsNullOrEmpty(next.Query);
            if (initial.Host != "github.com") return false;
            if (next.Host == "github.com") return next.AbsolutePath == initial.AbsolutePath && string.IsNullOrEmpty(next.Query);
            // GitHub documents these exact release asset domains; never admit *.githubusercontent.com.
            // https://docs.github.com/en/actions/reference/runners/github-hosted-runners#communication-requirements-for-github-hosted-runners
            return next.Host == "release-assets.githubusercontent.com" || next.Host == "objects.githubusercontent.com"
                || next.Host == "github-releases.githubusercontent.com";
        }

        private static InvalidDataException Invalid(string message) { return new InvalidDataException(message); }
        private static byte[] Base64(string value, string message)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                if (Convert.ToBase64String(bytes) != value) throw Invalid(message);
                return bytes;
            }
            catch (FormatException) { throw Invalid(message); }
        }
        private static Dictionary<string, object> JsonObject(string text)
        {
            Dictionary<string, object> result = new JsonReader(text).Read() as Dictionary<string, object>;
            if (result == null) throw Invalid("更新清单必须为 JSON 对象。");
            return result;
        }
        private static void RequireFields(Dictionary<string, object> values, string[] fields)
        {
            if (values.Count != fields.Length) throw Invalid("更新清单字段无效或重复。");
            foreach (string field in fields) if (!values.ContainsKey(field)) throw Invalid("更新清单缺少字段：" + field);
        }
        private static string Text(Dictionary<string, object> values, string field)
        {
            string value = values[field] as string;
            if (value == null) throw Invalid("更新清单字段类型无效：" + field);
            return value;
        }
        private static long Number(Dictionary<string, object> values, string field)
        {
            object value = values[field];
            if (!(value is long)) throw Invalid("更新清单字段类型无效：" + field);
            return (long)value;
        }

        // Small strict JSON reader: no external serializer dependency, duplicate keys and trailing data are rejected.
        private sealed class JsonReader
        {
            private readonly string text;
            private int position;
            internal JsonReader(string value) { text = value; }
            internal object Read()
            {
                object value = Value(0); White();
                if (position != text.Length) throw Invalid("更新清单含多余内容。");
                return value;
            }
            private object Value(int depth)
            {
                White();
                if (depth > 4 || position >= text.Length) throw Invalid("更新清单 JSON 无效。");
                char c = text[position];
                if (c == '"') return String();
                if (c == '{')
                {
                    position++; White();
                    Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) return result;
                    do
                    {
                        White(); string key = String(); White(); Need(':');
                        if (result.ContainsKey(key)) throw Invalid("更新清单含重复字段。");
                        result.Add(key, Value(depth + 1)); White();
                        if (Take('}')) return result;
                        Need(',');
                    } while (true);
                }
                if (c == '[')
                {
                    position++; White(); List<object> result = new List<object>();
                    if (Take(']')) return result;
                    do
                    {
                        if (result.Count >= 32) throw Invalid("更新清单数组过大。");
                        result.Add(Value(depth + 1)); White();
                        if (Take(']')) return result;
                        Need(',');
                    } while (true);
                }
                int start = position;
                if (c == '-') position++;
                if (position >= text.Length || text[position] < '0' || text[position] > '9') throw Invalid("更新清单 JSON 值无效。");
                if (text[position] == '0') position++;
                else while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
                long number;
                if (!long.TryParse(text.Substring(start, position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
                    throw Invalid("更新清单数字无效。");
                return number;
            }
            private string String()
            {
                Need('"'); StringBuilder value = new StringBuilder();
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"') return value.ToString();
                    if (c < 32) throw Invalid("更新清单字符串含控制字符。");
                    if (c == '\\')
                    {
                        if (position >= text.Length) break;
                        c = text[position++];
                        switch (c)
                        {
                            case '"': case '\\': case '/': value.Append(c); break;
                            case 'b': value.Append('\b'); break;
                            case 'f': value.Append('\f'); break;
                            case 'n': value.Append('\n'); break;
                            case 'r': value.Append('\r'); break;
                            case 't': value.Append('\t'); break;
                            case 'u':
                                int code;
                                if (position + 4 > text.Length || !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                    throw Invalid("更新清单 Unicode 转义无效。");
                                value.Append((char)code); position += 4; break;
                            default: throw Invalid("更新清单转义无效。");
                        }
                    }
                    else value.Append(c);
                }
                throw Invalid("更新清单字符串未结束。");
            }
            private void White() { while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++; }
            private bool Take(char c) { if (position < text.Length && text[position] == c) { position++; return true; } return false; }
            private void Need(char c) { if (!Take(c)) throw Invalid("更新清单 JSON 结构无效。"); }
        }
    }
}
