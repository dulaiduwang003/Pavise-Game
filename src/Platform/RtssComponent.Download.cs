using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PaviseApp
{
    internal static partial class RtssComponent
    {
        // 从 MSI 官方接口获取下载令牌，文件直接下载到临时目录。
        internal const string MsiDownloadUrl = "https://download.msi.com/uti_exe/vga/MSIAfterburnerSetup.zip";
        private const string MsiTokenUrl = "https://www.msi.cn/api/v1/get_token?date=";
        internal const string MsiArchiveSha256 = "7C6A2F4FAA55385A787200C95DAAF1FEB103E853FC5BD827776D0983E54709D4";
        internal const string InstallerSha256 = "04F4704CED86A68B650BF87156C02E69160E6C049B576661250250BEEABAA5EF";
        private const string MsiWrapperName = "MSIAfterburnerInstaller466_20260922.exe";
        private const string MsiWrapperSha256 = "C8D8A725F34AD5DF6D8997ACE6CF6BB15C88155B86EE2DD30AF8A99872A74F00";
        private const string MsiPayloadSha256 = "E96B37C854817AB2BEA71455B589C7F64E30E47F1924F694DCCE9EA7A76F8817";
        private const string MsiSetupSha256 = "1E23BAFA8979865A14141E0EAC4B84ED8A61A399E6698AF4C0878CD500E09790";

        internal static void DownloadArchive(string destination, Action<RtssInstallProgress> progress,
            CancellationToken cancellation)
        {
            DownloadWithFallback(destination, progress, cancellation, DownloadMsiArchive,
                delegate(string path, Action<RtssInstallProgress> notify, CancellationToken token) {
                    DownloadFile(DownloadUrl, path, AutomaticPolicy.Text("正在从 Guru3D 官方源下载 RTSS（约 18 MB）",
                        "Downloading RTSS from Guru3D's official mirror (about 18 MB)"), notify, token);
                }, VerifyArchive);
        }

        internal static void DownloadMsiArchive(string destination, Action<RtssInstallProgress> progress,
            CancellationToken cancellation)
        {
            Report(progress, "Downloading", 2, AutomaticPolicy.Text("正在连接微星官方下载服务",
                "Connecting to MSI's official download service"));
            string token = ReadMsiDownloadToken(cancellation);
            DownloadFile(MsiDownloadUrl + "?__token__=" + token, destination,
                AutomaticPolicy.Text("正在从微星官网下载组件包（约 44 MB，仅安装 RTSS）",
                    "Downloading from MSI (about 44 MB; installs RTSS only)"), progress, cancellation);
        }

        // 哈希不匹配的安装包禁止解压或执行。
        internal static void DownloadWithFallback(string destination, Action<RtssInstallProgress> progress,
            CancellationToken cancellation,
            Action<string, Action<RtssInstallProgress>, CancellationToken> primary,
            Action<string, Action<RtssInstallProgress>, CancellationToken> fallback, Action<string> verify)
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(destination)) throw new IOException("RTSS 下载目标已存在。");
            var sources = new[] { primary, fallback };
            for (int i = 0; i < sources.Length; i++)
            {
                bool complete = false;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    sources[i](destination, progress, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    Report(progress, "Verifying", 72, AutomaticPolicy.Text("正在校验官方下载包",
                        "Verifying the official download"));
                    verify(destination);
                    cancellation.ThrowIfCancellationRequested();
                    complete = true;
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!(error is WebException) && !(error is IOException) && !(error is InvalidDataException) && !(error is TimeoutException)
                        && !(error is InvalidOperationException)) throw;
                    if (i == sources.Length - 1)
                        throw new IOException(AutomaticPolicy.Text("两个官方下载源均未完成下载或校验，请稍后重试。",
                            "Neither official source could provide a verified download. Try again later."), error);
                    Report(progress, "Downloading", 0, AutomaticPolicy.Text("微星源暂不可用，正在切换 Guru3D 官方源",
                        "MSI is unavailable; switching to Guru3D's official mirror"));
                }
                finally
                {
                    // 只删除本次下载产生的残留文件。
                    if (!complete && File.Exists(destination)) File.Delete(destination);
                }
            }
        }

        private static string ReadMsiDownloadToken(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(MsiTokenUrl + DateTime.Now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
            request.UserAgent = "Pavise-RTSS-Component/1.0";
            request.Accept = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 5000;
            request.AllowAutoRedirect = false;
            using (var abort = cancellation.Register(delegate { try { request.Abort(); } catch { } }))
            using (var timeout = new System.Threading.Timer(delegate { try { request.Abort(); } catch { } }, null, 15000, Timeout.Infinite))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (var body = new MemoryStream())
                    {
                        if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > 1024)
                            throw new InvalidDataException("微星官方下载令牌响应无效。");
                        byte[] buffer = new byte[256];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (body.Length + count > 1024) throw new InvalidDataException("微星官方下载令牌响应过长。");
                            body.Write(buffer, 0, count);
                        }
                        return ParseMsiDownloadToken(Encoding.UTF8.GetString(body.ToArray()));
                    }
                }
                catch (WebException) { cancellation.ThrowIfCancellationRequested(); throw; }
            }
        }

        internal static string ParseMsiDownloadToken(string json)
        {
            if (json == null || json.Length > 1024) throw new InvalidDataException("微星官方下载令牌无效。");
            string normalized = json.Replace("\\/", "/");
            Match match = Regex.Match(normalized,
                "\\A\\s*\\[\\s*\"(exp=[0-9]{10,11}~acl=/\\*~hmac=[a-fA-F0-9]{64})\"\\s*\\]\\s*\\z",
                RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException("微星官方下载令牌无效。");
            return match.Groups[1].Value;
        }

        private static string ComponentFileHash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                if (input.Length <= 0 || input.Length > MaxArchiveBytes)
                    throw new InvalidDataException("RTSS 安装包大小无效。");
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
            }
        }

        internal static Uri ResolveDownloadRedirect(Uri current, string location)
        {
            Uri next;
            if (String.IsNullOrWhiteSpace(location) || !Uri.TryCreate(current, location, out next)
                || next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0
                || next.AbsolutePath != current.AbsolutePath)
                throw new InvalidDataException("RTSS 下载跳转地址无效。");
            bool sameHost = String.Equals(next.Host, current.Host, StringComparison.OrdinalIgnoreCase);
            bool msiCdn = (current.Host == "download.msi.com" || current.Host == "download-2.msi.com")
                && (next.Host == "download.msi.com" || next.Host == "download-2.msi.com");
            if (!sameHost && !msiCdn) throw new InvalidDataException("RTSS 下载跳转到了未验证的来源。");
            return next;
        }

        private static bool VerifiedArchiveIsMsi(string path)
        {
            string hash = ComponentFileHash(path);
            if (String.Equals(hash, MsiArchiveSha256, StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(hash, ArchiveSha256, StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidDataException("RTSS 安装包版本或校验不匹配，已停止使用此下载包。");
        }

        private static void VerifyComponentFile(string path, string hash)
        {
            if (!String.Equals(ComponentFileHash(path), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RTSS 组件文件校验失败，已停止安装。");
        }

        internal static void ExtractComponentInstaller(string archive, string destination, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (VerifiedArchiveIsMsi(archive)) ExtractMsiInstaller(archive, destination, cancellation);
            else ExtractInstaller(archive, destination, cancellation);
            cancellation.ThrowIfCancellationRequested();
            VerifyComponentFile(destination, InstallerSha256);
        }

        private static void ExtractMsiInstaller(string archive, string destination, CancellationToken cancellation)
        {
            string staging = Path.Combine(Path.GetDirectoryName(destination), "msi-extract-" + Guid.NewGuid().ToString("N"));
            string wrapper = Path.Combine(staging, "wrapper.bin");
            string payload = Path.Combine(staging, "payload.zip");
            string setup = Path.Combine(staging, "setup.bin");
            Directory.CreateDirectory(staging);
            try
            {
                ExtractZipEntry(archive, wrapper, MsiWrapperName, cancellation);
                VerifyComponentFile(wrapper, MsiWrapperSha256);
                // 偏移仅适用于已校验哈希的 MSI 包，不执行外层安装器。
                CopyComponentRange(wrapper, payload, 900797, 42439758, cancellation);
                VerifyComponentFile(payload, MsiPayloadSha256);
                ExtractZipEntry(payload, setup, "MSIAfterburnerSetup466.exe", cancellation);
                VerifyComponentFile(setup, MsiSetupSha256);
                CopyComponentRange(setup, destination, 4882642, 18078504, cancellation);
            }
            finally
            {
                File.Delete(wrapper); File.Delete(payload); File.Delete(setup);
                Directory.Delete(staging, false);
            }
        }

        internal static void CopyComponentRange(string source, string destination, long offset, long count,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var input = File.OpenRead(source))
            {
                if (offset < 0 || count <= 0 || count > MaxInstallerBytes || offset > input.Length - count)
                    throw new InvalidDataException("RTSS 组件文件范围无效。");
                input.Position = offset;
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    while (count > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                        if (read == 0) throw new EndOfStreamException("RTSS 组件文件不完整。");
                        output.Write(buffer, 0, read); count -= read;
                    }
                }
            }
        }
    }
}
