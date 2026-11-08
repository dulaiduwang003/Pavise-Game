using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class UpdateChecker
	{
		private sealed class Source
		{
			public string Name;

			public string Url;
		}

		private const int RequestTimeoutMs = 8000;

		private const int TotalTimeoutMs = 13000;

		private const int GraceAfterFirstHitMs = 2500;

		private const int MaxBodyBytes = 262144;

		private const int MaxDonateId = 64;

		public static void CheckAsync(Action<UpdateResult> done)
		{
			ThreadPool.QueueUserWorkItem(delegate
			{
				UpdateResult checkResult = Check();
				try
				{
					done(checkResult);
				}
				catch
				{
				}
			});
		}

		private static Source[] BuildSources()
		{
			string toStringOffset = "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
			return new Source[2]
			{
				new Source
				{
					Name = Lang.T("site.entry"),
					Url = "https://pavise.club/version.json" + toStringOffset
				},
				new Source
				{
					Name = "Cloudflare",
					Url = "https://pavise-website.2074055628.workers.dev/version.json" + toStringOffset
				}
			};
		}

		private static UpdateResult Check()
		{
			try
			{
				SecurityProtocolType securityProtocol = ServicePointManager.SecurityProtocol;
				if (securityProtocol != 0)
				{
					ServicePointManager.SecurityProtocol = securityProtocol | SecurityProtocolType.Tls12;
				}
			}
			catch
			{
			}
			Source[] sources = BuildSources();
			object gate = new object();
			AutoResetEvent signal = new AutoResetEvent(false);
			List<UpdateResult> hits = new List<UpdateResult>();
			int pending = sources.Length;
			long firstHitTicks = 0L;
			Source[] sourceItems = sources;
			foreach (Source source in sourceItems)
			{
				Source src = source;
				ThreadPool.QueueUserWorkItem(delegate
				{
					UpdateResult updateResult2 = null;
					try
					{
						updateResult2 = Probe(src);
					}
					catch
					{
					}
					lock (gate)
					{
						if (updateResult2 != null)
						{
							hits.Add(updateResult2);
							if (firstHitTicks == 0)
							{
								firstHitTicks = DateTime.UtcNow.Ticks;
							}
						}
						pending--;
					}
					signal.Set();
				});
			}
			long ticksOffset = DateTime.UtcNow.Ticks + 130000000;
			while (true)
			{
				int pendingSnapshot;
				long firstHitTicksSnapshot;
				lock (gate)
				{
					pendingSnapshot = pending;
					firstHitTicksSnapshot = firstHitTicks;
				}
				long ticks = DateTime.UtcNow.Ticks;
				if (pendingSnapshot == 0 || ticks >= ticksOffset)
				{
					break;
				}
				long numericValue = ticksOffset;
				if (firstHitTicksSnapshot != 0)
				{
					long firstHitTicksSnapshotOffset = firstHitTicksSnapshot + 25000000;
					if (ticks >= firstHitTicksSnapshotOffset)
					{
						break;
					}
					if (firstHitTicksSnapshotOffset < numericValue)
					{
						numericValue = firstHitTicksSnapshotOffset;
					}
				}
				int checkValue = (int)((numericValue - ticks) / 10000) + 1;
				signal.WaitOne((checkValue < 50) ? 50 : checkValue);
			}
			UpdateResult updateResult = null;
			lock (gate)
			{
				foreach (UpdateResult item in hits)
				{
					if (updateResult == null || IsNewer(item.Latest, updateResult.Latest))
					{
						updateResult = item;
					}
				}
			}
			if (updateResult == null)
			{
				updateResult = new UpdateResult();
				updateResult.Error = Lang.T("t.updatechecker.4");
				Logger.Warn(Lang.T("log.updatechecker.5") + sources.Length + Lang.T("log.updatechecker.6"));
				return updateResult;
			}
			updateResult.Newer = IsNewer(updateResult.Latest, App.Version);
			Logger.Log(Lang.T("log.updatechecker.7") + updateResult.Source + Lang.T("log.updatechecker.8") + updateResult.Latest);
			return updateResult;
		}

		private static UpdateResult Probe(Source src)
		{
			string fetchTextResult = FetchText(src.Url);
			if (fetchTextResult == null)
			{
				return null;
			}
			return ParseManifest(fetchTextResult, src.Name);
		}

		internal static UpdateResult ParseManifest(string body, string source)
		{
			if (string.IsNullOrEmpty(body))
			{
				return null;
			}
			if (Encoding.UTF8.GetByteCount(body) > 262144)
			{
				return null;
			}
			string latestValue = JsonValue(body, "version");
			if (string.IsNullOrEmpty(latestValue))
			{
				return null;
			}
			if (!IsCanonicalManifestVersion(latestValue))
			{
				return null;
			}
			UpdateResult updateResult = new UpdateResult();
			updateResult.Ok = true;
			updateResult.Latest = latestValue;
			updateResult.Source = source;
			updateResult.Url = App.ChangelogUrl;
			updateResult.Donate = ParseDonate(body);
			return updateResult;
		}

		internal static DonateInfo ParseDonate(string body)
		{
			string idValue = Clean(JsonValue(body, "donateId"), 64);
			if (string.IsNullOrEmpty(idValue) || !IsSaneDonateId(idValue))
			{
				return null;
			}
			string url = JsonValue(body, "donateUrl");
			if (!IsTrustedWebsiteUrl(url))
			{
				return null;
			}
			DonateInfo donateInfo = new DonateInfo();
			donateInfo.Id = idValue;
			donateInfo.Url = url;
			return donateInfo;
		}

		private static bool IsSaneDonateId(string id)
		{
			foreach (char c in id)
			{
				if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
				{
					return false;
				}
			}
			return true;
		}

		private static string Clean(string s, int max)
		{
			if (string.IsNullOrEmpty(s))
			{
				return null;
			}
			StringBuilder stringBuilder = new StringBuilder(s.Length);
			foreach (char c in s)
			{
				switch (c)
				{
				case '\n':
					stringBuilder.Append('\n');
					break;
				case '\t':
				case '\r':
					stringBuilder.Append(' ');
					break;
				default:
					stringBuilder.Append(char.IsControl(c) ? ' ' : c);
					break;
				}
			}
			string toStringTrimmed = stringBuilder.ToString().Trim();
			if (toStringTrimmed.Length == 0)
			{
				return null;
			}
			if (toStringTrimmed.Length <= max)
			{
				return toStringTrimmed;
			}
			return toStringTrimmed.Substring(0, max);
		}

		private static bool IsCanonicalManifestVersion(string tag)
		{
			if (string.IsNullOrEmpty(tag) || tag.Trim() != tag)
			{
				return false;
			}
			Version result;
			if (!Version.TryParse(tag, out result) || result.Revision < 0)
			{
				return false;
			}
			try
			{
				return result.ToString(4) == tag;
			}
			catch
			{
				return false;
			}
		}

		private static HttpWebRequest NewRequest(string url)
		{
			HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create(url);
			httpWebRequest.UserAgent = "Pavise-Update-Check";
			httpWebRequest.Timeout = 8000;
			httpWebRequest.ReadWriteTimeout = 8000;
			httpWebRequest.KeepAlive = false;
			return httpWebRequest;
		}

		private static string FetchText(string url)
		{
			try
			{
				HttpWebRequest httpWebRequest = NewRequest(url);
				httpWebRequest.AllowAutoRedirect = true;
				using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
				{
					using (Stream stream = httpWebResponse.GetResponseStream())
					{
						if (stream == null)
						{
							return null;
						}
						if (httpWebResponse.ContentLength > 262144)
						{
							return null;
						}
						byte[] rawBytes = new byte[8192];
						MemoryStream memoryStream = new MemoryStream();
						int readResult;
						while ((readResult = stream.Read(rawBytes, 0, rawBytes.Length)) > 0)
						{
							if (memoryStream.Length + readResult > 262144)
							{
								memoryStream.Dispose();
								return null;
							}
							memoryStream.Write(rawBytes, 0, readResult);
						}
						byte[] bytes = memoryStream.ToArray();
						memoryStream.Dispose();
						return Encoding.UTF8.GetString(bytes);
					}
				}
			}
			catch
			{
				return null;
			}
		}

		public static byte[] FetchBytes(string url, int maxBytes)
		{
			if (!IsTrustedWebsiteUrl(url))
			{
				return null;
			}
			byte[] fetchImageBytesResult = FetchImageBytes(url, maxBytes);
			string websiteFallbackForResult = WebsiteFallbackFor(url);
			object instance = fetchImageBytesResult;
			if (instance == null)
			{
				if (websiteFallbackForResult != null)
				{
					return FetchImageBytes(websiteFallbackForResult, maxBytes);
				}
				instance = null;
			}
			return (byte[])instance;
		}

		internal static string WebsiteFallbackFor(string url)
		{
			if (!IsTrustedWebsiteUrl(url))
			{
				return null;
			}
			Uri uri = new Uri(url);
			if (uri.Host != new Uri("https://pavise.club/").Host)
			{
				return null;
			}
			return "https://pavise-website.2074055628.workers.dev/".TrimEnd('/') + uri.PathAndQuery;
		}

		private static byte[] FetchImageBytes(string url, int maxBytes)
		{
			try
			{
				HttpWebRequest httpWebRequest = NewRequest(url);
				httpWebRequest.AllowAutoRedirect = false;
				using (HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse())
				{
					using (Stream stream = httpWebResponse.GetResponseStream())
					{
						if (stream == null)
						{
							return null;
						}
						if (httpWebResponse.ContentLength > maxBytes)
						{
							return null;
						}
						byte[] rawBytes = new byte[8192];
						MemoryStream memoryStream = new MemoryStream();
						int readResult;
						while ((readResult = stream.Read(rawBytes, 0, rawBytes.Length)) > 0)
						{
							if (memoryStream.Length + readResult > maxBytes)
							{
								memoryStream.Dispose();
								return null;
							}
							memoryStream.Write(rawBytes, 0, readResult);
						}
						byte[] result = memoryStream.ToArray();
						memoryStream.Dispose();
						return result;
					}
				}
			}
			catch
			{
				return null;
			}
		}

		private static string JsonValue(string json, string key)
		{
			if (string.IsNullOrEmpty(json))
			{
				return null;
			}
			string message = "\"" + key + "\"";
			for (int iterationIndex = json.IndexOf(message, StringComparison.Ordinal); iterationIndex >= 0; iterationIndex = json.IndexOf(message, iterationIndex + message.Length, StringComparison.Ordinal))
			{
				int i;
				for (i = iterationIndex + message.Length; i < json.Length && (json[i] == ' ' || json[i] == '\t'); i++)
				{
				}
				if (i < json.Length && json[i] == ':')
				{
					for (i++; i < json.Length && (json[i] == ' ' || json[i] == '\t'); i++)
					{
					}
					if (i < json.Length && json[i] == '"')
					{
						StringBuilder stringBuilder = new StringBuilder();
						i++;
						while (i < json.Length && json[i] != '"')
						{
							if (json[i] == '\\' && i + 1 < json.Length)
							{
								i++;
								if (json[i] == 'u' && i + 4 < json.Length)
								{
									int result;
									if (int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result))
									{
										stringBuilder.Append((char)result);
									}
									i += 5;
									continue;
								}
								if (json[i] == 'n')
								{
									stringBuilder.Append('\n');
								}
								else if (json[i] == 't')
								{
									stringBuilder.Append('\t');
								}
								else
								{
									stringBuilder.Append(json[i]);
								}
								i++;
							}
							else
							{
								stringBuilder.Append(json[i]);
								i++;
							}
						}
						if (i >= json.Length || json[i] != '"')
						{
							return null;
						}
						string stringBuilderText = stringBuilder.ToString();
						if (stringBuilderText.Length > 0)
						{
							return stringBuilderText;
						}
					}
				}
			}
			return null;
		}

		public static bool IsTrustedWebsiteUrl(string url)
		{
			if (string.IsNullOrEmpty(url))
			{
				return false;
			}
			Uri result;
			if (!Uri.TryCreate(url, UriKind.Absolute, out result))
			{
				return false;
			}
			if (result.Scheme != Uri.UriSchemeHttps)
			{
				return false;
			}
			string toLowerInvariantResult = result.Host.ToLowerInvariant();
			if (result.IsDefaultPort && string.IsNullOrEmpty(result.UserInfo) && (toLowerInvariantResult == new Uri("https://pavise.club/").Host || toLowerInvariantResult == new Uri("https://pavise-website.2074055628.workers.dev/").Host))
			{
				return true;
			}
			return false;
		}

		public static bool IsNewer(string remote, string local)
		{
			int[] ver = ParseVer(remote);
			int[] ver2 = ParseVer(local);
			if (ver.Length == 0)
			{
				return false;
			}
			int lengthLimit = Math.Max(ver.Length, ver2.Length);
			for (int i = 0; i < lengthLimit; i++)
			{
				int isNewerValue = ((i < ver.Length) ? ver[i] : 0);
				int isNewerValue2 = ((i < ver2.Length) ? ver2[i] : 0);
				if (isNewerValue != isNewerValue2)
				{
					return isNewerValue > isNewerValue2;
				}
			}
			return false;
		}

		private static int[] ParseVer(string v)
		{
			if (v == null)
			{
				return new int[0];
			}
			v = v.Trim();
			int i;
			for (i = 0; i < v.Length && !char.IsDigit(v[i]); i++)
			{
			}
			v = v.Substring(i);
			int j;
			for (j = 0; j < v.Length && (char.IsDigit(v[j]) || v[j] == '.'); j++)
			{
			}
			List<int> list = new List<int>();
			string[] substringParts = v.Substring(0, j).Split(new char[1] { '.' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string s in substringParts)
			{
				int result;
				if (int.TryParse(s, out result))
				{
					list.Add(result);
				}
			}
			return list.ToArray();
		}
	}
}
