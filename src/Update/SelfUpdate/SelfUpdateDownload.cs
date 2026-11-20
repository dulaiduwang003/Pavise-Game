using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class SelfUpdateDownload
    {
        private const int FeedTimeoutMilliseconds = 8000;
        private const int MirrorTimeoutMilliseconds = 180000;
        private const int DownloadTimeoutMilliseconds = 600000;
        private const int MaxRedirects = 5;

#if PAVISE_SELFTEST
        // 逻辑地址仍通过生产版 HTTPS 主机及重定向规则 仅自测试中的连接端点可以映射到本机回环 HTTP 服务

        internal static Func<Uri, Uri> FixtureTransportEndpoint;
        internal static int FixtureFeedTimeoutMilliseconds, FixtureMirrorTimeoutMilliseconds;
        internal static int FixtureDownloadTimeoutMilliseconds, FixtureReadTimeoutMilliseconds;
        private static int FixtureTimeout(int fixture, int production) { return fixture > 0 ? Math.Min(fixture, production) : production; }
#endif

        internal static SelfUpdatePackage Check(CancellationToken cancel)
        {
            string[] feeds = new string[] { "https://" + SelfUpdateManifest.OfficialHost + "/updates/stable.json",
                "https://" + SelfUpdateManifest.FallbackHost + "/updates/stable.json" };
            Exception last = null;
            SelfUpdatePackage older = null;
            Version currentVersion = new Version(App.Version);
            int feedTimeout = FeedTimeoutMilliseconds;
#if PAVISE_SELFTEST
            feedTimeout = FixtureTimeout(FixtureFeedTimeoutMilliseconds, feedTimeout);
#endif
            foreach (string feed in feeds)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    using (MemoryStream output = new MemoryStream())
                    {
                        Fetch(feed, output, SelfUpdateManifest.MaxManifestBytes, -1, null, cancel, feedTimeout);
                        string stringValue = new UTF8Encoding(false, true).GetString(output.ToArray());
                        SelfUpdatePackage package = SelfUpdateManifest.ParseAndVerify(stringValue);
                        cancel.ThrowIfCancellationRequested();
                        Version remoteVersion = new Version(package.Version);
                        if (remoteVersion.CompareTo(currentVersion) < 0)
                        {
                            // 有效但较旧的更新源属于健康响应 继续查找当前或更新版本
                            if (older == null || remoteVersion.CompareTo(new Version(older.Version)) > 0)
                                older = package;
                            continue;
                        }
                        return package;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!IsExpectedFailure(error)) throw;
                    last = error;
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (older != null) return older;
            throw new InvalidOperationException(Lang.Text("暂时无法取得有效的签名更新清单，请稍后重试，或通过夸克网盘、GitHub 下载最新版。", "Could not get a valid signed update manifest. Try again later, or download the latest version from Quark or GitHub."), last);
        }

        internal static string Download(SelfUpdatePackage package, string stagingDirectory, Action<SelfUpdateProgress> progress, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            SelfUpdatePackage verified = Revalidate(package);
            if (new Version(verified.Version).CompareTo(new Version(App.Version)) <= 0)
                throw new InvalidDataException(Lang.Text("更新版本必须高于当前版本。", "The update version must be newer than the current version."));
            string directory = PrepareStagingDirectory(stagingDirectory);
            Stopwatch elapsed = Stopwatch.StartNew();
            Exception last = null;
            int totalTimeout = DownloadTimeoutMilliseconds, mirrorTimeout = MirrorTimeoutMilliseconds;
#if PAVISE_SELFTEST
            totalTimeout = FixtureTimeout(FixtureDownloadTimeoutMilliseconds, totalTimeout);
            mirrorTimeout = FixtureTimeout(FixtureMirrorTimeoutMilliseconds, mirrorTimeout);
#endif
            foreach (string url in verified.DownloadUrls)
            {
                cancel.ThrowIfCancellationRequested();
                int remaining = totalTimeout - (int)Math.Min(elapsed.ElapsedMilliseconds, totalTimeout);
                if (remaining <= 0) throw new TimeoutException(Lang.Text("更新下载超过总时间限制，请稍后重试或使用备用下载。", "The update download exceeded its total time limit. Try again later or use an alternate download."));
                string path = Path.Combine(directory, "Pavise.update." + Guid.NewGuid().ToString("N") + ".exe");
                bool keep = false;
                try
                {
                    RejectReparseChain(directory);
                    Report(progress, "Downloading", 0, verified.Size);
                    using (FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        Fetch(url, output, verified.Size, verified.Size,
                            delegate(long received) { Report(progress, "Downloading", received, verified.Size); },
                            cancel, Math.Min(mirrorTimeout, remaining));
                        output.Flush(true);
                    }
                    cancel.ThrowIfCancellationRequested();
                    Report(progress, "Verifying", verified.Size, verified.Size);
                    VerifyDownloadedFile(verified, path, cancel);
                    cancel.ThrowIfCancellationRequested();
                    keep = true;
                    return path;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!IsExpectedFailure(error)) throw;
                    last = error;
                }
                finally
                {
                    if (!keep) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
                }
            }
            throw new InvalidOperationException(Lang.Text("更新包下载或校验失败，请重试，或通过夸克网盘、GitHub 下载最新版。", "The update package could not be downloaded or verified. Try again, or download the latest version from Quark or GitHub."), last);
        }

        private static SelfUpdatePackage Revalidate(SelfUpdatePackage package)
        {
            if (package == null) throw new ArgumentNullException("package");
            SelfUpdatePackage verified = SelfUpdateManifest.ParseAndVerify(package.ManifestText);
            if (package.Version != verified.Version || package.Size != verified.Size || package.Sha256 != verified.Sha256
                || package.DownloadUrls == null || package.DownloadUrls.Length != verified.DownloadUrls.Length)
                throw new InvalidDataException(Lang.Text("更新信息已改变，请重新检查更新。", "The update information changed. Check for updates again."));
            for (int i = 0; i < package.DownloadUrls.Length; i++)
                if (package.DownloadUrls[i] != verified.DownloadUrls[i]) throw new InvalidDataException(Lang.Text("更新地址已改变，请重新检查更新。", "The update download address changed. Check for updates again."));
            return verified;
        }

        internal static void VerifyDownloadedFile(SelfUpdatePackage package, string path, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            RejectReparseChain(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != package.Size || stream.Length > SelfUpdateManifest.MaxExecutableBytes)
                    throw new InvalidDataException(Lang.Text("更新程序大小不匹配。", "The update executable size does not match."));
                string hash;
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                if (!string.Equals(hash, package.Sha256, StringComparison.Ordinal)) throw new InvalidDataException(Lang.Text("更新程序 SHA-256 校验失败。", "The update executable failed SHA-256 verification."));
                cancel.ThrowIfCancellationRequested();
                // GetAssemblyName 只读取元数据 不加载或执行下载的程序集
                AssemblyName identity = AssemblyName.GetAssemblyName(path);
                if (identity.Version == null || identity.Version.ToString(4) != package.Version)
                    throw new InvalidDataException(Lang.Text("更新程序版本与签名清单不匹配。", "The update executable version does not match the signed manifest."));
            }
        }

        private static string PrepareStagingDirectory(string stagingDirectory)
        {
            bool drivePath = stagingDirectory != null && stagingDirectory.Length >= 3 && char.IsLetter(stagingDirectory[0])
                && stagingDirectory[1] == ':' && (stagingDirectory[2] == '\\' || stagingDirectory[2] == '/');
            bool uncPath = stagingDirectory != null && stagingDirectory.StartsWith("\\\\", StringComparison.Ordinal)
                && !stagingDirectory.StartsWith("\\\\?\\", StringComparison.Ordinal) && !stagingDirectory.StartsWith("\\\\.\\", StringComparison.Ordinal);
            if (!drivePath && !uncPath)
                throw new ArgumentException(Lang.Text("更新暂存目录必须为绝对路径。", "The update staging directory must be an absolute path."), "stagingDirectory");
            string directory = Path.GetFullPath(stagingDirectory).TrimEnd('\\', '/');
            if (directory.Length == 0 || string.Equals(directory, Path.GetPathRoot(directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(Lang.Text("更新暂存目录不能为磁盘根目录。", "The update staging directory cannot be a drive root."), "stagingDirectory");
            RejectReparseChain(directory);
            Directory.CreateDirectory(directory);
            RejectReparseChain(directory);
            return directory;
        }

        internal static void RejectReparseChain(string path)
        {
            string cursor = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(Lang.Text("更新路径被重定向，已停止自动更新。", "The update path is redirected; automatic update stopped."));
                cursor = Path.GetDirectoryName(cursor);
            }
        }

        private static void Report(Action<SelfUpdateProgress> progress, string stage, long received, long total)
        {
            if (progress == null) return;
            try { progress(new SelfUpdateProgress { Stage = stage, Received = received, Total = total }); } catch { }
        }

        private static bool IsExpectedFailure(Exception error)
        {
            return error is WebException || error is IOException || error is InvalidDataException || error is TimeoutException || error is CryptographicException
                || error is BadImageFormatException || error is DecoderFallbackException || error is NotSupportedException;
        }

        private static void Fetch(string url, Stream output, long limit, long expectedSize, Action<long> received,
            CancellationToken cancel, int timeoutMilliseconds)
        {
            // 保留系统默认 TLS 选择 进程若已有旧显式设置 也必须允许 TLS 1.2
            SecurityProtocolType protocols = ServicePointManager.SecurityProtocol;
            if (protocols != 0 && (protocols & SecurityProtocolType.Tls12) == 0)
                ServicePointManager.SecurityProtocol = protocols | SecurityProtocolType.Tls12;
            Uri initial;
            if (!SelfUpdateManifest.IsHttpsUrl(url, out initial)) throw new InvalidDataException(Lang.Text("更新地址必须为受信任的 HTTPS 地址。", "The update address must be a trusted HTTPS URL."));
            Uri current = initial;
            Stopwatch elapsed = Stopwatch.StartNew();
            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                cancel.ThrowIfCancellationRequested();
                int remaining = timeoutMilliseconds - (int)Math.Min(elapsed.ElapsedMilliseconds, timeoutMilliseconds);
                if (remaining <= 0) throw new TimeoutException(Lang.Text("更新连接超时。", "The update connection timed out."));
                Uri endpoint = current;
#if PAVISE_SELFTEST
                if (FixtureTransportEndpoint != null)
                {
                    endpoint = FixtureTransportEndpoint(current);
                    if (endpoint == null || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host != "127.0.0.1"
                        || endpoint.IsDefaultPort || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
                        throw new InvalidDataException("The self-update test transport must be a loopback HTTP endpoint.");
                }
#endif
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
#if PAVISE_SELFTEST
                if (FixtureTransportEndpoint != null) request.Proxy = null;
#endif
                request.Method = "GET";
                request.UserAgent = "Pavise-SelfUpdate/1";
                request.Accept = "application/octet-stream, application/json";
                request.AllowAutoRedirect = false;
                request.AutomaticDecompression = DecompressionMethods.None;
                request.Timeout = Math.Min(15000, remaining);
                request.ReadWriteTimeout = Math.Min(15000, remaining);
#if PAVISE_SELFTEST
                request.ReadWriteTimeout = FixtureTimeout(FixtureReadTimeoutMilliseconds, request.ReadWriteTimeout);
#endif
                request.KeepAlive = false;
                request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                using (CancellationTokenRegistration registration = cancel.Register(delegate { try { request.Abort(); } catch { } }))
                using (Timer timeout = new Timer(delegate { try { request.Abort(); } catch { } }, null, remaining, Timeout.Infinite))
                {
                    try
                    {
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                            {
                                Uri next;
                                if (redirect >= MaxRedirects || !Uri.TryCreate(current, response.Headers[HttpResponseHeader.Location], out next)
                                    || !SelfUpdateManifest.IsAllowedRedirect(initial, next))
                                    throw new InvalidDataException(Lang.Text("更新下载重定向无效或不受信任。", "The update download redirect is invalid or untrusted."));
                                current = next;
                                continue;
                            }
                            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException(Lang.Text("更新服务器未返回完整文件。", "The update server did not return the complete file."));
                            if (response.ContentLength > limit || (expectedSize >= 0 && response.ContentLength >= 0 && response.ContentLength != expectedSize))
                                throw new InvalidDataException(Lang.Text("更新文件长度与清单不匹配或超过限制。", "The update file length does not match the manifest or exceeds the limit."));
                            if (!string.IsNullOrEmpty(response.ContentEncoding) && !string.Equals(response.ContentEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException(Lang.Text("更新文件使用了不支持的传输编码。", "The update file uses an unsupported transfer encoding."));
                            using (Stream input = response.GetResponseStream())
                            {
                                if (input == null) throw new InvalidDataException(Lang.Text("更新服务器返回了空内容。", "The update server returned empty content."));
                                CopyBounded(input, output, limit, expectedSize, received, cancel);
                            }
                            return;
                        }
                    }
                    catch (WebException error)
                    {
                        if (error.Response != null) error.Response.Close();
                        cancel.ThrowIfCancellationRequested();
                        if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds) throw new TimeoutException(Lang.Text("更新连接超时。", "The update connection timed out."));
                        throw;
                    }
                }
            }
            throw new InvalidDataException(Lang.Text("更新重定向次数超过限制。", "Too many update redirects."));
        }

        internal static void CopyBounded(Stream input, Stream output, long limit, long expectedSize, Action<long> received, CancellationToken cancel)
        {
            long count = 0;
            byte[] buffer = new byte[65536];
            int read;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (count > limit - read) throw new InvalidDataException(Lang.Text("更新文件超过允许大小。", "The update file exceeds the allowed size."));
                output.Write(buffer, 0, read);
                count += read;
                if (received != null) received(count);
            }
            cancel.ThrowIfCancellationRequested();
            if ((expectedSize >= 0 && count != expectedSize) || count == 0) throw new InvalidDataException(Lang.Text("更新文件下载不完整。", "The update file download is incomplete."));
        }
    }
}
