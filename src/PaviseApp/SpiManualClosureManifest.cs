using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal static class SpiManualClosureManifest
	{
		private static string Header(SpiOwnerStamp stamp)
		{
			return "pavise-manual-spi-close-1\nowner\t" + stamp.ManualReceiptOwner + "\n";
		}

		internal static byte[] Bytes(SpiOwnerStamp stamp, IList<string> rows)
		{
			return new UTF8Encoding(false, true).GetBytes(Header(stamp) + ((rows.Count == 0) ? "" : (string.Join("\n", new List<string>(rows).ToArray()) + "\n")));
		}

		internal static IDictionary<string, string> Read(SpiOwnership.SpiOwnerAccess closedAccess)
		{
			string @string = new UTF8Encoding(false, true).GetString(closedAccess.ReadClosedManifest());
			string headerResult = Header(closedAccess.Stamp);
			if (!@string.StartsWith(headerResult, StringComparison.Ordinal) || !@string.EndsWith("\n", StringComparison.Ordinal))
			{
				throw new InvalidDataException("Closed manual SPI evidence has another owner");
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			MatchVisualEffects.Effect[] effects = MatchVisualEffects.Effects;
			foreach (MatchVisualEffects.Effect effect in effects)
			{
				hashSet.Add(PerformanceActionStore.Slot("MatchVisualEffectsV1", effect.Name));
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
			string substringResult = @string.Substring(headerResult.Length);
			string message = null;
			if (substringResult.Length != 0)
			{
				string[] trimEndParts = substringResult.TrimEnd('\n').Split('\n');
				foreach (string entryText in trimEndParts)
				{
					string[] entryTextParts = entryText.Split('\t');
					if (entryTextParts.Length != 2 || !hashSet.Contains(entryTextParts[0]) || dictionary.ContainsKey(entryTextParts[0]) || (message != null && string.CompareOrdinal(message, entryTextParts[0]) >= 0))
					{
						throw new InvalidDataException("Invalid closed manual SPI receipt coordinate");
					}
					SpiOwnerIdentity.Hash(entryTextParts[1]);
					dictionary.Add(entryTextParts[0], entryTextParts[1]);
					message = entryTextParts[0];
				}
			}
			return dictionary;
		}
	}
}
