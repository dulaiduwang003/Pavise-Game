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
				UpdateResult obj = Check();
				try
				{
					done(obj);
				}
				catch
				{
				}
			});
		}

		private static Source[] BuildSources()
		{
			string text = "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
			return new Source[2]
			{
				new Source
				{
					Name = Lang.T("site.entry"),
					Url = "https://pavise.club/version.json" + text
				},
				new Source
				{
					Name = "Cloudflare",
					Url = "https://pavise-website.2074055628.workers.dev/version.json" + text
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
			Source[] array = BuildSources();
			object gate = new object();
			AutoResetEvent signal = new AutoResetEvent(false);
			List<UpdateResult> hits = new List<UpdateResult>();
			int pending = array.Length;
			long firstHitTicks = 0L;
			Source[] array2 = array;
			foreach (Source source in array2)
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
			long num = DateTime.UtcNow.Ticks + 130000000;
			while (true)
			{
				int num2;
				long num3;
				lock (gate)
				{
					num2 = pending;
					num3 = firstHitTicks;
				}
				long ticks = DateTime.UtcNow.Ticks;
				if (num2 == 0 || ticks >= num)
				{
					break;
				}
				long num4 = num;
				if (num3 != 0)
				{
					long num5 = num3 + 25000000;
					if (ticks >= num5)
					{
						break;
					}
					if (num5 < num4)
					{
						num4 = num5;
					}
				}
				int num6 = (int)((num4 - ticks) / 10000) + 1;
				signal.WaitOne((num6 < 50) ? 50 : num6);
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
				Logger.Warn(Lang.T("log.updatechecker.5") + array.Length + Lang.T("log.updatechecker.6"));
				return updateResult;
			}
			updateResult.Newer = IsNewer(updateResult.Latest, App.Version);
			Logger.Log(Lang.T("log.updatechecker.7") + updateResult.Source + Lang.T("log.updatechecker.8") + updateResult.Latest);
			return updateResult;
		}

		private static UpdateResult Probe(Source src)
		{
			string text = FetchText(src.Url);
			if (text == null)
			{
				return null;
			}
			return ParseManifest(text, src.Name);
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
			string text = JsonValue(body, "version");
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			if (!IsCanonicalManifestVersion(text))
			{
				return null;
			}
			UpdateResult updateResult = new UpdateResult();
			updateResult.Ok = true;
			updateResult.Latest = text;
			updateResult.Source = source;
			updateResult.Url = App.ChangelogUrl;
			updateResult.Donate = ParseDonate(body);
			return updateResult;
		}

		internal static DonateInfo ParseDonate(string body)
		{
			string text = Clean(JsonValue(body, "donateId"), 64);
			if (string.IsNullOrEmpty(text) || !IsSaneDonateId(text))
			{
				return null;
			}
			string url = JsonValue(body, "donateUrl");
			if (!IsTrustedWebsiteUrl(url))
			{
				return null;
			}
			DonateInfo donateInfo = new DonateInfo();
			donateInfo.Id = text;
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
			string text = stringBuilder.ToString().Trim();
			if (text.Length == 0)
			{
				return null;
			}
			if (text.Length <= max)
			{
				return text;
			}
			return text.Substring(0, max);
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
						byte[] array = new byte[8192];
						MemoryStream memoryStream = new MemoryStream();
						int num;
						while ((num = stream.Read(array, 0, array.Length)) > 0)
						{
							if (memoryStream.Length + num > 262144)
							{
								memoryStream.Dispose();
								return null;
							}
							memoryStream.Write(array, 0, num);
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
			byte[] array = FetchImageBytes(url, maxBytes);
			string text = WebsiteFallbackFor(url);
			object obj = array;
			if (obj == null)
			{
				if (text != null)
				{
					return FetchImageBytes(text, maxBytes);
				}
				obj = null;
			}
			return (byte[])obj;
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
						byte[] array = new byte[8192];
						MemoryStream memoryStream = new MemoryStream();
						int num;
						while ((num = stream.Read(array, 0, array.Length)) > 0)
						{
							if (memoryStream.Length + num > maxBytes)
							{
								memoryStream.Dispose();
								return null;
							}
							memoryStream.Write(array, 0, num);
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
			string text = "\"" + key + "\"";
			for (int num = json.IndexOf(text, StringComparison.Ordinal); num >= 0; num = json.IndexOf(text, num + text.Length, StringComparison.Ordinal))
			{
				int i;
				for (i = num + text.Length; i < json.Length && (json[i] == ' ' || json[i] == '\t'); i++)
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
						string text2 = stringBuilder.ToString();
						if (text2.Length > 0)
						{
							return text2;
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
			string text = result.Host.ToLowerInvariant();
			if (result.IsDefaultPort && string.IsNullOrEmpty(result.UserInfo) && (text == new Uri("https://pavise.club/").Host || text == new Uri("https://pavise-website.2074055628.workers.dev/").Host))
			{
				return true;
			}
			return false;
		}

		public static bool IsNewer(string remote, string local)
		{
			int[] array = ParseVer(remote);
			int[] array2 = ParseVer(local);
			if (array.Length == 0)
			{
				return false;
			}
			int num = Math.Max(array.Length, array2.Length);
			for (int i = 0; i < num; i++)
			{
				int num2 = ((i < array.Length) ? array[i] : 0);
				int num3 = ((i < array2.Length) ? array2[i] : 0);
				if (num2 != num3)
				{
					return num2 > num3;
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
			string[] array = v.Substring(0, j).Split(new char[1] { '.' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string s in array)
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
