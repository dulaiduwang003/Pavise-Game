using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
	internal static class StrongBackgroundPolicy
	{
		private static readonly object gate = new object();

		private static readonly Dictionary<string, bool> identities = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

		internal static bool CandidateName(string name)
		{
			switch (WhitelistRule.NormalizeName(name).ToLowerInvariant())
			{
			case "googleupdate":
			case "googleupdater":
			case "microsoftedgeupdate":
			case "onedrivestandaloneupdater":
			case "adobearm":
			case "jusched":
				return true;
			default:
				return false;
			}
		}

		internal static bool MetadataMatches(string name, string original, string company, string product)
		{
			name = WhitelistRule.NormalizeName(name);
			if (!CandidateName(name) || !string.Equals(name, WhitelistRule.NormalizeName(original), StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			company = (company ?? "").Trim();
			product = product ?? "";
			switch (name.ToLowerInvariant())
			{
			case "googleupdate":
			case "googleupdater":
				if (company == "Google LLC" || company == "Google Inc.")
				{
					return product.IndexOf("Google", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				return false;
			case "microsoftedgeupdate":
				if (company == "Microsoft Corporation")
				{
					return product.IndexOf("Microsoft Edge", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				return false;
			case "onedrivestandaloneupdater":
				if (company == "Microsoft Corporation")
				{
					return product.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				return false;
			case "adobearm":
				if (company == "Adobe Inc." || company == "Adobe Systems Incorporated")
				{
					return product.IndexOf("Adobe", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				return false;
			case "jusched":
				if (company == "Oracle Corporation")
				{
					return product.IndexOf("Java", StringComparison.OrdinalIgnoreCase) >= 0;
				}
				return false;
			default:
				return false;
			}
		}

		internal static bool ApprovedImage(string path)
		{
			path = WhitelistRule.NormalizeImagePath(path);
			if (path.Length == 0 || !CandidateName(Path.GetFileNameWithoutExtension(path)))
			{
				return false;
			}
			try
			{
				FileInfo fileInfo = new FileInfo(path);
				string key = path + "|" + fileInfo.Length + "|" + fileInfo.LastWriteTimeUtc.Ticks;
				lock (gate)
				{
					bool value;
					if (identities.TryGetValue(key, out value))
					{
						return value;
					}
				}
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
				bool metadataMatchesSucceeded = MetadataMatches(Path.GetFileNameWithoutExtension(path), versionInfo.OriginalFilename, versionInfo.CompanyName, versionInfo.ProductName);
				lock (gate)
				{
					if (identities.Count >= 256)
					{
						identities.Clear();
					}
					identities[key] = metadataMatchesSucceeded;
				}
				return metadataMatchesSucceeded;
			}
			catch
			{
				return false;
			}
		}

		internal static bool Admit(PerformancePreset mode, long creation, bool protectedFamily, bool hasChildren, bool service, bool approvedImage)
		{
			if (!PresetValue.IsValid((int)mode))
			{
				return false;
			}
			if (mode == PerformancePreset.Extreme && creation > 0 && !protectedFamily)
			{
				if (!service)
				{
					if (!hasChildren)
					{
						return approvedImage;
					}
					return false;
				}
				return true;
			}
			return false;
		}
	}
}
