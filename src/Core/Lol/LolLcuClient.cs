// @author bdth 2074055628@qq.com
// 文件用途 LCU 本地接口客户端与界面收放

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PaviseApp
{
    internal sealed class LolHttpResult
    {
        public bool Reached;
        public int Status;
        public string Body;

        public bool Success
        {
            get { return Reached && Status >= 200 && Status < 300; }
        }
    }

    internal static class LolLcuClient
    {
        private const int MaximumResponseChars = 32 * 1024;
        private static readonly string[] GameflowPhases =
        {
            "None", "Lobby", "Matchmaking", "CheckedIntoTournament",
            "ReadyCheck", "ChampSelect",
            "GameStart", "FailedToLaunch", "InProgress", "Reconnect",
            "WaitingForStats", "PreEndOfGame", "EndOfGame",
            "TerminatedInError"
        };
        private static readonly Regex LoginSucceededPattern = new Regex(
            @"\""state\""\s*:\s*\""SUCCEEDED\""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsReady(LolLcuCredentials credentials)
        {
            LolHttpResult login = Send(credentials, "GET", "/lol-login/v1/session");
            if (!login.Success || !LoginSucceededPattern.IsMatch(login.Body ?? "")) return false;
            LolHttpResult summoner = Send(credentials, "GET", "/lol-summoner/v1/current-summoner");
            return summoner.Success;
        }

        public static string GetGameflowPhase(LolLcuCredentials credentials)
        {
            LolHttpResult result = Send(credentials, "GET", "/lol-gameflow/v1/gameflow-phase");
            string phase;
            return result.Success
                && TryParseGameflowPhaseBody(result.Body, out phase)
                ? phase : null;
        }

        internal static bool IsCredentialReachable(
            LolLcuCredentials credentials)
        {
            LolHttpResult result = Send(
                credentials,
                "GET",
                "/lol-gameflow/v1/gameflow-phase",
                1000);
            string phase;
            return result.Success
                && TryParseGameflowPhaseBody(result.Body, out phase);
        }

        internal static bool TryParseGameflowPhaseBody(
            string body, out string phase)
        {
            phase = null;
            if (string.IsNullOrWhiteSpace(body)) return false;
            string text = body.Trim();
            if (text.Length < 3 || text[0] != '"'
                || text[text.Length - 1] != '"')
                return false;
            string candidate = text.Substring(1, text.Length - 2);
            for (int i = 0; i < GameflowPhases.Length; i++)
            {
                if (!string.Equals(
                        candidate, GameflowPhases[i],
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                phase = GameflowPhases[i];
                return true;
            }
            return false;
        }

        public static bool KillUx(LolLcuCredentials credentials, string lolRoot)
        {
            if (!Send(credentials, "POST", "/riotclient/kill-ux").Success) return false;
            return WaitForUxExit(lolRoot, 8000);
        }

        public static bool RestoreUx(LolLcuCredentials credentials, string lolRoot)
        {
            Mutex restoreMutex = null;
            bool held = false;
            try
            {
                string suffix = LolHeadlessLease.StableHash(lolRoot);
                try
                {
                    restoreMutex = new Mutex(
                        false, "Global\\Pavise_LolUxRestore_" + suffix);
                }
                catch
                {
                    restoreMutex = new Mutex(
                        false, "Pavise_LolUxRestore_" + suffix);
                }
                try { held = restoreMutex.WaitOne(15000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return false;
                return RestoreUxSingleWriter(credentials, lolRoot);
            }
            catch { return false; }
            finally
            {
                if (held && restoreMutex != null)
                    try { restoreMutex.ReleaseMutex(); } catch { }
                if (restoreMutex != null)
                    try { restoreMutex.Close(); } catch { }
            }
        }

        private static bool RestoreUxSingleWriter(
            LolLcuCredentials credentials, string lolRoot)
        {
            if (LolRuntimeProcesses.IsUxRunning(lolRoot))
                return TryShowExistingUx(credentials, lolRoot);

            LolHttpResult launch = Send(
                credentials, "POST", "/riotclient/launch-ux");
            if (launch.Success && WaitForUx(lolRoot, 6000))
                return TryShowExistingUx(credentials, lolRoot);

            if (LolRuntimeProcesses.IsUxRunning(lolRoot))
                return TryShowExistingUx(credentials, lolRoot);
            LolHttpResult restart = Send(
                credentials, "POST", "/riotclient/kill-and-restart-ux");
            if (!restart.Success) return false;
            WaitForUx(lolRoot, 10000);
            return TryShowExistingUx(credentials, lolRoot);
        }

        private static bool TryShowExistingUx(
            LolLcuCredentials credentials, string lolRoot)
        {
            if (!LolRuntimeProcesses.IsUxRunning(lolRoot)) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                LolHttpResult show = Send(
                    credentials, "POST", "/riotclient/ux-show");
                if (show.Success && WaitForUx(lolRoot, 1000)) return true;
                if (!LolRuntimeProcesses.IsUxRunning(lolRoot)) return false;
                Thread.Sleep(350);
            }
            return false;
        }

        private static bool WaitForUx(string lolRoot, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (LolRuntimeProcesses.IsUxRunning(lolRoot)) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return LolRuntimeProcesses.IsUxRunning(lolRoot);
        }

        private static bool WaitForUxExit(string lolRoot, int timeoutMs)
        {
            int waited = 0;
            int absent = 0;
            while (waited < timeoutMs)
            {
                if (LolRuntimeProcesses.IsUxRunning(lolRoot))
                    absent = 0;
                else if (++absent >= 2)
                    return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return !LolRuntimeProcesses.IsUxRunning(lolRoot);
        }

        private static LolHttpResult Send(
            LolLcuCredentials credentials, string method, string relativePath)
        {
            return Send(credentials, method, relativePath, 3000);
        }

        private static LolHttpResult Send(
            LolLcuCredentials credentials,
            string method,
            string relativePath,
            int timeoutMs)
        {
            var result = new LolHttpResult();
            if (credentials == null || credentials.Port <= 0 || credentials.Port > 65535)
                return result;
            Uri uri;
            if (!Uri.TryCreate(
                "https://127.0.0.1:" + credentials.Port + relativePath,
                UriKind.Absolute, out uri) || !uri.IsLoopback)
                return result;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = method;
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.KeepAlive = true;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Accept = "application/json";
                request.UserAgent = "Pavise-LolRuntime";
                request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("riot:" + credentials.Token));
                request.ServerCertificateValidationCallback = ValidateLoopbackCertificate;
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    request.ContentType = "application/json";
                    request.ContentLength = 0;
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    result.Reached = true;
                    result.Status = (int)response.StatusCode;
                    result.Body = ReadBody(response);
                }
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    {
                        result.Reached = true;
                        result.Status = (int)response.StatusCode;
                        result.Body = ReadBody(response);
                    }
                }
            }
            catch { }
            return result;
        }

        private static bool ValidateLoopbackCertificate(
            object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            var request = sender as HttpWebRequest;
            return request != null && request.RequestUri != null && request.RequestUri.IsLoopback;
        }

        private static string ReadBody(HttpWebResponse response)
        {
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null) return "";
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = new StringBuilder(Math.Min(
                            MaximumResponseChars,
                            response.ContentLength > 0
                                ? (int)Math.Min(response.ContentLength, MaximumResponseChars)
                                : 1024));
                        var buffer = new char[2048];
                        while (text.Length < MaximumResponseChars)
                        {
                            int count = reader.Read(
                                buffer, 0,
                                Math.Min(buffer.Length, MaximumResponseChars - text.Length));
                            if (count <= 0) break;
                            text.Append(buffer, 0, count);
                        }
                        return text.ToString();
                    }
                }
            }
            catch { return ""; }
        }
    }
}
