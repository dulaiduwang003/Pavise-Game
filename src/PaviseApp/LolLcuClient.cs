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
	internal static class LolLcuClient
	{
		private const int MaximumResponseChars = 32768;

		private static readonly string[] GameflowPhases = new string[14]
		{
			"None", "Lobby", "Matchmaking", "CheckedIntoTournament", "ReadyCheck", "ChampSelect", "GameStart", "FailedToLaunch", "InProgress", "Reconnect",
			"WaitingForStats", "PreEndOfGame", "EndOfGame", "TerminatedInError"
		};

		private static readonly Regex LoginSucceededPattern = new Regex("\\\"state\\\"\\s*:\\s*\\\"SUCCEEDED\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		public static bool IsReady(LolLcuCredentials credentials)
		{
			LolHttpResult lolHttpResult = Send(credentials, "GET", "/lol-login/v1/session");
			if (!lolHttpResult.Success || !LoginSucceededPattern.IsMatch(lolHttpResult.Body ?? ""))
			{
				return false;
			}
			LolHttpResult lolHttpResult2 = Send(credentials, "GET", "/lol-summoner/v1/current-summoner");
			return lolHttpResult2.Success;
		}

		public static string GetGameflowPhase(LolLcuCredentials credentials)
		{
			LolHttpResult lolHttpResult = Send(credentials, "GET", "/lol-gameflow/v1/gameflow-phase");
			string phase;
			if (!lolHttpResult.Success || !TryParseGameflowPhaseBody(lolHttpResult.Body, out phase))
			{
				return null;
			}
			return phase;
		}

		internal static bool IsCredentialReachable(LolLcuCredentials credentials)
		{
			LolHttpResult lolHttpResult = Send(credentials, "GET", "/lol-gameflow/v1/gameflow-phase", 1000);
			string phase;
			if (lolHttpResult.Success)
			{
				return TryParseGameflowPhaseBody(lolHttpResult.Body, out phase);
			}
			return false;
		}

		internal static bool TryParseGameflowPhaseBody(string body, out string phase)
		{
			phase = null;
			if (string.IsNullOrWhiteSpace(body))
			{
				return false;
			}
			string bodyTrimmed = body.Trim();
			if (bodyTrimmed.Length < 3 || bodyTrimmed[0] != '"' || bodyTrimmed[bodyTrimmed.Length - 1] != '"')
			{
				return false;
			}
			string a = bodyTrimmed.Substring(1, bodyTrimmed.Length - 2);
			for (int i = 0; i < GameflowPhases.Length; i++)
			{
				if (string.Equals(a, GameflowPhases[i], StringComparison.OrdinalIgnoreCase))
				{
					phase = GameflowPhases[i];
					return true;
				}
			}
			return false;
		}

		public static bool KillUx(LolLcuCredentials credentials, string lolRoot)
		{
			if (!Send(credentials, "POST", "/riotclient/kill-ux").Success)
			{
				return false;
			}
			return WaitForUxExit(lolRoot, 8000);
		}

		public static bool RestoreUx(LolLcuCredentials credentials, string lolRoot)
		{
			Mutex mutex = null;
			bool restoreUxReady = false;
			try
			{
				string stableHashResult = LolHeadlessLease.StableHash(lolRoot);
				try
				{
					mutex = new Mutex(false, "Global\\Pavise_LolUxRestore_" + stableHashResult);
				}
				catch
				{
					mutex = new Mutex(false, "Pavise_LolUxRestore_" + stableHashResult);
				}
				try
				{
					restoreUxReady = mutex.WaitOne(15000);
				}
				catch (AbandonedMutexException)
				{
					restoreUxReady = true;
				}
				if (!restoreUxReady)
				{
					return false;
				}
				return RestoreUxSingleWriter(credentials, lolRoot);
			}
			catch
			{
				return false;
			}
			finally
			{
				if (restoreUxReady && mutex != null)
				{
					try
					{
						mutex.ReleaseMutex();
					}
					catch
					{
					}
				}
				if (mutex != null)
				{
					try
					{
						mutex.Close();
					}
					catch
					{
					}
				}
			}
		}

		private static bool RestoreUxSingleWriter(LolLcuCredentials credentials, string lolRoot)
		{
			if (LolRuntimeProcesses.IsUxRunning(lolRoot))
			{
				return TryShowExistingUx(credentials, lolRoot);
			}
			LolHttpResult lolHttpResult = Send(credentials, "POST", "/riotclient/launch-ux");
			if (lolHttpResult.Success && WaitForUx(lolRoot, 6000))
			{
				return TryShowExistingUx(credentials, lolRoot);
			}
			if (LolRuntimeProcesses.IsUxRunning(lolRoot))
			{
				return TryShowExistingUx(credentials, lolRoot);
			}
			LolHttpResult lolHttpResult2 = Send(credentials, "POST", "/riotclient/kill-and-restart-ux");
			if (!lolHttpResult2.Success)
			{
				return false;
			}
			WaitForUx(lolRoot, 10000);
			return TryShowExistingUx(credentials, lolRoot);
		}

		private static bool TryShowExistingUx(LolLcuCredentials credentials, string lolRoot)
		{
			if (!LolRuntimeProcesses.IsUxRunning(lolRoot))
			{
				return false;
			}
			for (int i = 0; i < 3; i++)
			{
				LolHttpResult lolHttpResult = Send(credentials, "POST", "/riotclient/ux-show");
				if (lolHttpResult.Success && WaitForUx(lolRoot, 1000))
				{
					return true;
				}
				if (!LolRuntimeProcesses.IsUxRunning(lolRoot))
				{
					return false;
				}
				Thread.Sleep(350);
			}
			return false;
		}

		private static bool WaitForUx(string lolRoot, int timeoutMs)
		{
			for (int i = 0; i < timeoutMs; i += 250)
			{
				if (LolRuntimeProcesses.IsUxRunning(lolRoot))
				{
					return true;
				}
				Thread.Sleep(250);
			}
			return LolRuntimeProcesses.IsUxRunning(lolRoot);
		}

		private static bool WaitForUxExit(string lolRoot, int timeoutMs)
		{
			int i = 0;
			int waitForUxExitValue = 0;
			for (; i < timeoutMs; i += 250)
			{
				if (LolRuntimeProcesses.IsUxRunning(lolRoot))
				{
					waitForUxExitValue = 0;
				}
				else if (++waitForUxExitValue >= 2)
				{
					return true;
				}
				Thread.Sleep(250);
			}
			return !LolRuntimeProcesses.IsUxRunning(lolRoot);
		}

		private static LolHttpResult Send(LolLcuCredentials credentials, string method, string relativePath)
		{
			return Send(credentials, method, relativePath, 3000);
		}

		private static LolHttpResult Send(LolLcuCredentials credentials, string method, string relativePath, int timeoutMs)
		{
			LolHttpResult lolHttpResult = new LolHttpResult();
			if (credentials == null || credentials.Port <= 0 || credentials.Port > 65535)
			{
				return lolHttpResult;
			}
			Uri result;
			if (!Uri.TryCreate("https://127.0.0.1:" + credentials.Port + relativePath, UriKind.Absolute, out result) || !result.IsLoopback)
			{
				return lolHttpResult;
			}
			try
			{
				HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(result);
				httpWebRequest.Method = method;
				httpWebRequest.Proxy = null;
				httpWebRequest.AllowAutoRedirect = false;
				httpWebRequest.KeepAlive = true;
				httpWebRequest.Timeout = timeoutMs;
				httpWebRequest.ReadWriteTimeout = timeoutMs;
				httpWebRequest.Accept = "application/json";
				httpWebRequest.UserAgent = "Pavise-LolRuntime";
				httpWebRequest.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + credentials.Token));
				httpWebRequest.ServerCertificateValidationCallback = ValidateLoopbackCertificate;
				if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
				{
					httpWebRequest.ContentType = "application/json";
					httpWebRequest.ContentLength = 0L;
				}
				using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
				{
					lolHttpResult.Reached = true;
					lolHttpResult.Status = (int)httpWebResponse.StatusCode;
					lolHttpResult.Body = ReadBody(httpWebResponse);
				}
			}
			catch (WebException ex)
			{
				HttpWebResponse httpWebResponse2 = ex.Response as HttpWebResponse;
				if (httpWebResponse2 != null)
				{
					using (httpWebResponse2)
					{
						lolHttpResult.Reached = true;
						lolHttpResult.Status = (int)httpWebResponse2.StatusCode;
						lolHttpResult.Body = ReadBody(httpWebResponse2);
					}
				}
			}
			catch
			{
			}
			return lolHttpResult;
		}

		private static bool ValidateLoopbackCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
		{
			HttpWebRequest httpWebRequest = sender as HttpWebRequest;
			if (httpWebRequest != null && httpWebRequest.RequestUri != null)
			{
				return httpWebRequest.RequestUri.IsLoopback;
			}
			return false;
		}

		private static string ReadBody(HttpWebResponse response)
		{
			try
			{
				using (Stream stream = response.GetResponseStream())
				{
					if (stream == null)
					{
						return "";
					}
					using (StreamReader streamReader = new StreamReader(stream, Encoding.UTF8))
					{
						StringBuilder stringBuilder = new StringBuilder(Math.Min(32768, (int)((response.ContentLength > 0) ? Math.Min(response.ContentLength, 32768L) : 1024)));
						char[] charItems = new char[2048];
						while (stringBuilder.Length < 32768)
						{
							int readResult = streamReader.Read(charItems, 0, Math.Min(charItems.Length, 32768 - stringBuilder.Length));
							if (readResult <= 0)
							{
								break;
							}
							stringBuilder.Append(charItems, 0, readResult);
						}
						return stringBuilder.ToString();
					}
				}
			}
			catch
			{
				return "";
			}
		}
	}
}
